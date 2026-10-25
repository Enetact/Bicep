using System.Net;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class PipelineAuthenticatedTransportTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Full_durable_permit_vault_and_authorization_header_flow_uses_a_mock_handler(string provider)
    {
        using var f = new CredentialFixture(provider); await f.StoreToken();
        var permits = new PermitSource(f); var handler = new CapturedHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        var response = await transport.SendAsync(f.Wire, default);
        Assert.True(transport.IsMock); Assert.Equal(200, response.StatusCode); Assert.Equal(1, handler.Calls);
        Assert.Equal(f.Wire.Uri, handler.Target); Assert.Equal("Bearer " + CredentialFixture.Token, handler.Authorization);
        Assert.Equal(f.Wire.BodyJson, handler.Body); Assert.DoesNotContain(CredentialFixture.Token, response.Body);
        Assert.DoesNotContain("password", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-private-value", response.Body);
    }

    [Fact]
    public async Task Production_transport_is_disabled_by_default_and_makes_no_authorization_or_network_call()
    {
        using var f = new CredentialFixture(); var permits = new PermitSource(f);
        using var transport = new PipelineAuthenticatedTransport(f.Vault, permits, clock: f.Clock);
        await Assert.ThrowsAsync<PipelineTransportDisabledException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(0, permits.Calls); Assert.False(transport.IsMock);
    }

    [Theory]
    [InlineData("scheme")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("method")]
    [InlineData("authorization")]
    [InlineData("header_injection")]
    public async Task Invalid_targets_or_headers_are_denied_before_credential_resolution(string mutation)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var permits = new PermitSource(f); var handler = new CapturedHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        var headers = new Dictionary<string, string>(f.Wire.Headers);
        var wire = mutation switch
        {
            "scheme" => f.Wire with { Uri = new Uri(f.Wire.Uri.AbsoluteUri.Replace("https:", "http:", StringComparison.Ordinal)) },
            "host" => f.Wire with { Uri = new Uri("https://attacker.invalid/" + f.Wire.Uri.PathAndQuery) },
            "port" => f.Wire with { Uri = new UriBuilder(f.Wire.Uri) { Port = 444 }.Uri },
            "userinfo" => f.Wire with { Uri = new UriBuilder(f.Wire.Uri) { UserName = "attacker" }.Uri },
            "fragment" => f.Wire with { Uri = new UriBuilder(f.Wire.Uri) { Fragment = "hidden" }.Uri },
            "method" => f.Wire with { Method = "GET" },
            _ => f.Wire with { Headers = headers }
        };
        if (mutation == "authorization") headers["Authorization"] = "Bearer injected";
        if (mutation == "header_injection") headers["Accept"] = "application/json\r\nInjected:true";
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default));
        Assert.Equal(0, permits.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("plan")]
    [InlineData("body")]
    [InlineData("source")]
    [InlineData("path")]
    public async Task Retargeted_wire_cannot_use_a_durable_dispatch_permit(string mutation)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock);
        var wire = mutation switch
        {
            "customer" => f.Wire with { Binding = f.Wire.Binding with { CustomerId = "10000000-0000-0000-0000-000000000099" } },
            "plan" => f.Wire with { Binding = f.Wire.Binding with { PlanDigest = new string('b', 64) } },
            "source" => f.Wire with { Binding = f.Wire.Binding with { SourceRevision = new string('b', 40) } },
            "path" => f.Wire with { Uri = new Uri(f.Wire.Uri.AbsoluteUri.Replace("customer-repo", "other-repo", StringComparison.Ordinal)) },
            _ => f.Wire with { BodyJson = "{}" }
        };
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task One_use_permit_cannot_replay_and_cannot_be_serialized()
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        var permit = CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, f.Wire, f.Scope, f.Clock.Now);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(permit));
        var permits = new PermitSource(f) { Reused = permit }; var handler = new CapturedHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        await transport.SendAsync(f.Wire, default);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default)); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("self_approval")]
    [InlineData("queued")]
    [InlineData("expired_lease")]
    [InlineData("read_only")]
    [InlineData("machine_requester")]
    public void Factory_rejects_unapproved_or_nonchanging_or_lost_authority_snapshots(string mutation)
    {
        using var f = new CredentialFixture(); var dispatch = mutation switch
        {
            "unapproved" => f.Dispatch with { Plan = f.Dispatch.Plan with { ApproverKey = null } },
            "self_approval" => f.Dispatch with { Plan = f.Dispatch.Plan with { ApproverKey = f.Dispatch.Plan.RequesterKey } },
            "queued" => f.Dispatch with { Job = f.Dispatch.Job with { State = "Queued" } },
            "expired_lease" => f.Dispatch with { Job = f.Dispatch.Job with { LeaseUntil = f.Clock.Now } },
            "machine_requester" => f.Dispatch with { Plan = f.Dispatch.Plan with { RequesterKey = "workload:forged" } },
            _ => f.Dispatch with { Plan = f.Dispatch.Plan with { ChangesExternalState = false } }
        };
        Assert.Throws<CredentialException>(() => CredentialDispatchPermit.FromDurableDispatch(dispatch, f.Wire, f.Scope, f.Clock.Now));
    }

    [Fact]
    public async Task Expired_permit_revoked_profile_or_authorizer_denial_send_nothing()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler();
        var permit = CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, f.Wire, f.Scope, f.Clock.Now);
        f.Clock.Now = f.Clock.Now.AddSeconds(61);
        using (var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f) { Reused = permit }, handler, f.Clock))
            await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        f.Clock.Now = f.Clock.Now.AddSeconds(-61);
        await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1);
        using (var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock))
            await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        using (var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f) { Deny = true }, handler, f.Clock))
            await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Redirects_and_errors_never_expose_reflected_credentials_or_retry(int status)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler { Status = status, ResponseBody = CredentialFixture.Token };
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock);
        var response = await transport.SendAsync(f.Wire, default);
        Assert.Equal(status == 302 ? 502 : status, response.StatusCode); Assert.Empty(response.Body); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("reflect")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task Untrusted_success_bodies_are_bounded_and_secret_safe(string fault)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler
        { ResponseBody = fault switch { "reflect" => "{\"token\":\"" + CredentialFixture.Token + "\"}", "oversized" => new string('x', 262145), _ => "{\"workflow_run_id\":1,\"workflow_run_id\":2}" } };
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock);
        Assert.Empty((await transport.SendAsync(f.Wire, default)).Body); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Handler_failure_is_sanitized_and_never_retries_an_ambiguous_post()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler { Throw = true };
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(f.Wire, default));
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Slow_key_custody_cannot_extend_the_original_dispatch_permit()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler();
        f.Keys.OnGet = () => { f.Clock.Now = f.Clock.Now.AddSeconds(61); f.Keys.OnGet = null; };
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new PermitSource(f), handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Durable_authority_revocation_during_key_lookup_is_rechecked_before_the_POST()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var handler = new CapturedHandler(); var permits = new PermitSource(f);
        f.Keys.OnGet = () => permits.Deny = true;
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(2, permits.Calls); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_does_not_consume_authority_or_resolve_a_secret()
    {
        using var f = new CredentialFixture(); var permits = new PermitSource(f); var handler = new CapturedHandler(); using var ct = new CancellationTokenSource(); ct.Cancel();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(f.Wire, ct.Token));
        Assert.Equal(0, permits.Calls); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_change_cannot_commit_between_final_authorization_and_send_admission_and_does_not_wait_for_response(bool rotate)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var other = f.Reopen();
        var authorizerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permits = new PermitSource(f) { BeforeFinal = async ct => { authorizerEntered.TrySetResult(); await authorize.Task.WaitAsync(ct); } };
        var handler = new HeldAdmissionHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = transport.SendAsync(f.Wire, timeout.Token);
        Task<CredentialProfile>? change = null;
        using var replacement = CredentialSecret.FromUtf8("synthetic-replacement-access-token");
        try
        {
            await authorizerEntered.Task.WaitAsync(timeout.Token);
            // An independent writer cannot enter the same custody gate while final authorization is suspended.
            Assert.Throws<IOException>(() => { using var competing = new FileStream(Path.Combine(f.VaultRoot, ".vault.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            change = rotate ? other.PutAsync(f.Scope, replacement, f.Clock.Now.AddHours(1), 1, timeout.Token) :
                other.RevokeAsync(f.Scope.AuthProfileId, 1, timeout.Token);
            Assert.False(change.IsCompleted);
            authorize.TrySetResult(); await handler.Admitted.Task.WaitAsync(timeout.Token);
            var changed = await change.WaitAsync(timeout.Token);
            Assert.Equal(2, changed.Generation); Assert.Equal(rotate, changed.Enabled);
            Assert.False(dispatch.IsCompleted); // The vault gate is released before the remote response, not held over it.
            Assert.Equal("Bearer " + CredentialFixture.Token, Assert.Single(handler.Authorizations));
            handler.Release.TrySetResult(); await dispatch;
            if (rotate)
            {
                await transport.SendAsync(f.Wire, timeout.Token);
                Assert.Equal("Bearer synthetic-replacement-access-token", handler.Authorizations[1]);
            }
            else
            {
                await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, timeout.Token));
                Assert.Single(handler.Authorizations);
            }
        }
        finally
        {
            authorize.TrySetResult(); handler.Release.TrySetResult();
            try { await dispatch; } catch (Exception error) when (error is OperationCanceledException or PipelineContractException or HttpRequestException) { }
            if (change is not null) await change;
        }
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("sendfailure")]
    public async Task Every_failed_admission_releases_its_vault_gate_for_subsequent_revocation(string failure)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); using var cancellation = new CancellationTokenSource();
        var permits = new PermitSource(f); var handler = new CapturedHandler { Throw = failure == "sendfailure" };
        permits.BeforeFinal = ct =>
        {
            if (failure == "deny") permits.Deny = true;
            if (failure == "exception") throw new CredentialException("synthetic-authorizer-denied");
            if (failure == "cancel") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(f.Wire, cancellation.Token));
        else if (failure == "sendfailure") await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(f.Wire, cancellation.Token));
        else await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, cancellation.Token));
        Assert.Equal(failure == "sendfailure" ? 1 : 0, handler.Calls);
        var revoked = await f.Reopen().RevokeAsync(f.Scope.AuthProfileId, 1);
        Assert.False(revoked.Enabled); Assert.Equal(2, revoked.Generation);
    }

    [Fact]
    public async Task Final_authorizer_cannot_extend_a_credential_that_expires_before_send_admission()
    {
        using var f = new CredentialFixture(); using var token = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await f.Vault.PutAsync(f.Scope, token, f.Clock.Now.AddSeconds(45));
        var permits = new PermitSource(f) { BeforeFinal = _ => { f.Clock.Now = f.Clock.Now.AddSeconds(31); return Task.CompletedTask; } };
        var handler = new CapturedHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(0, handler.Calls);
        Assert.False((await f.Reopen().RevokeAsync(f.Scope.AuthProfileId, 1)).Enabled);
    }

    sealed class PermitSource(CredentialFixture f) : ICredentialDispatchPermitSource
    {
        public int Calls { get; private set; }
        public bool Deny { get; set; }
        public CredentialDispatchPermit? Reused { get; init; }
        public Func<CancellationToken, Task>? BeforeFinal { get; set; }
        public async Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            Calls++; cancellationToken.ThrowIfCancellationRequested();
            if (Calls == 2 && BeforeFinal is not null) await BeforeFinal(cancellationToken);
            return Deny ? null : Reused ?? CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, wire, f.Scope, f.Clock.Now);
        }
    }
    sealed class HeldAdmissionHandler : HttpMessageHandler
    {
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Authorizations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization!.ToString()); Admitted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}") };
        }
    }
    sealed class CapturedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Target { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public int Status { get; init; } = 200;
        public string ResponseBody { get; init; } = "{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\",\"password\":\"provider-private-value\"}";
        public bool Throw { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Target = request.RequestUri; Authorization = request.Headers.Authorization?.ToString(); Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Throw) throw new HttpRequestException(CredentialFixture.Token);
            var response = new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json") };
            if (Status == 302) response.Headers.Location = new Uri("https://attacker.invalid/"); return response;
        }
    }
}
