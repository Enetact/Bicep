using System.Net;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialOperationDeadlineTests
{
    static readonly TimeSpan FixtureTimeout = TimeSpan.FromMilliseconds(100);

    [Theory]
    [InlineData("authorizer")]
    [InlineData("key")]
    public async Task Dispatch_deadline_before_admission_sends_nothing_releases_custody_and_permits_a_later_attempt(string stage)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var authorizer = new DeadlinePermits(f) { HoldFinal = stage == "authorizer" };
        var keys = new DeadlineKeys(f.Keys) { Hold = stage == "key" };
        var vault = new CredentialVault(f.Options, keys, f.Clock); using var handler = new DeadlineHandler();
        using (var timed = PipelineAuthenticatedTransport.ForMockHandler(vault, authorizer, handler, f.Clock, FixtureTimeout))
        {
            var failure = await Assert.ThrowsAsync<PipelineContractException>(() => timed.SendAsync(f.Wire, default));
            Assert.Equal("pipeline-credential-admission-timeout", failure.ReasonCode); Assert.Equal(0, handler.Calls);
            Assert.DoesNotContain(CredentialFixture.Token, failure.ToString());
        }
        authorizer.HoldFinal = false; keys.Hold = false;
        // A separate instance can read/write after cancellation; no leaked admission guard survives.
        using (await f.Reopen().ResolveAsync(f.Scope)) { }
        using var nextHandler = new DeadlineHandler(); using var next = PipelineAuthenticatedTransport.ForMockHandler(vault, new DeadlinePermits(f), nextHandler, f.Clock);
        Assert.Equal(200, (await next.SendAsync(f.Wire, default)).StatusCode); Assert.Equal(1, nextHandler.Calls);
    }

    [Fact]
    public async Task Stalled_admitted_response_has_bounded_body_deadline_and_remains_an_unknown_outcome_without_retry()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); using var handler = new DeadlineHandler { StallBody = true };
        // This case needs to reach HTTP admission after real vault custody/IO.
        // Keep the short preadmission deadline above separate from this body fixture.
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new DeadlinePermits(f), handler, f.Clock, TimeSpan.FromSeconds(5));
        var approval = new PipelineApproval(f.Scope.CustomerId, f.Scope.CellId, f.Connection.Id, f.Connection.Generation,
            PipelineRequestFactory.ConnectionDigest(f.Connection), PipelineRequestFactory.RequestDigest(f.Connection, f.Request),
            f.Dispatch.Plan.Digest, f.Dispatch.Plan.ExpiresAt);
        var dispatching = new PipelineDispatcher(transport, f.Clock).DispatchAsync(f.Connection, f.Request, approval);
        var entered = await Task.WhenAny(handler.BodyReadEntered.Task, dispatching).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(handler.BodyReadEntered.Task, entered);
        var bodyCancellation = await handler.BodyReadEntered.Task;
        var result = await dispatching.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(bodyCancellation.IsCancellationRequested);
        Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.ExternalRunId); Assert.Equal(1, handler.Calls);
        Assert.False((await f.Reopen().RevokeAsync(f.Scope.AuthProfileId, 1)).Enabled);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_final_authorization_preserves_its_token_and_releases_admission_guard()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var permits = new DeadlinePermits(f) { HoldFinal = true };
        using var handler = new DeadlineHandler(); using var caller = new CancellationTokenSource();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock, TimeSpan.FromSeconds(2));
        var sending = transport.SendAsync(f.Wire, caller.Token); await permits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, handler.Calls);
        Assert.False((await f.Reopen().RevokeAsync(f.Scope.AuthProfileId, 1)).Enabled);
    }

    [Theory]
    [InlineData("assertion")]
    [InlineData("body")]
    public async Task Token_acquisition_deadline_covers_assertion_and_response_body_without_persisting_a_profile(string stage)
    {
        using var f = new CredentialFixture(PipelineRequestFactory.AzureDevOps); var assertion = new DeadlineAssertions { Hold = stage == "assertion" };
        using var handler = new DeadlineHandler { Acquisition = true, StallBody = stage == "body" };
        using var client = CredentialTokenHttpClient.ForMockHandler(handler, FixtureTimeout);
        var source = new CredentialEntraFederatedSource(f.Scope, new(DeadlineAssertions.Tenant, DeadlineAssertions.Client), f.Vault, assertion, client, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync());
        Assert.Equal("credential-token-acquisition-timeout", error.Code);
        Assert.Equal(stage == "body" ? 1 : 0, handler.Calls); Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
    }

    [Fact]
    public async Task Token_acquisition_caller_cancellation_preserves_caller_token_and_creates_no_profile()
    {
        using var f = new CredentialFixture(PipelineRequestFactory.AzureDevOps); var assertion = new DeadlineAssertions { Hold = true };
        using var handler = new DeadlineHandler { Acquisition = true }; using var client = CredentialTokenHttpClient.ForMockHandler(handler, TimeSpan.FromSeconds(2));
        var source = new CredentialEntraFederatedSource(f.Scope, new(DeadlineAssertions.Tenant, DeadlineAssertions.Client), f.Vault, assertion, client, f.Clock);
        using var caller = new CancellationTokenSource(); var acquiring = source.AcquireAndStoreAsync(cancellationToken: caller.Token);
        await assertion.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, handler.Calls); Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    }

    [Fact]
    public async Task GitHub_private_key_callback_is_inside_the_same_acquisition_deadline_before_any_token_request()
    {
        using var f = new CredentialFixture(); using var handler = new DeadlineHandler();
        using var client = CredentialTokenHttpClient.ForMockHandler(handler, FixtureTimeout); var keys = new DeadlineGitHubKey();
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, client, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync());
        Assert.Equal("credential-token-acquisition-timeout", error.Code); Assert.Equal(1, keys.Calls); Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    }

    [Fact]
    public async Task Acquisition_deadline_also_covers_encryption_key_lookup_and_releases_store_gate_without_partial_profile()
    {
        using var f = new CredentialFixture(PipelineRequestFactory.AzureDevOps); using var handler = new DeadlineHandler { Acquisition = true };
        using var client = CredentialTokenHttpClient.ForMockHandler(handler, FixtureTimeout);
        var delayed = new CredentialVault(f.Options, new DeadlineKeys(f.Keys) { Hold = true }, f.Clock);
        var source = new CredentialEntraFederatedSource(f.Scope, new(DeadlineAssertions.Tenant, DeadlineAssertions.Client), delayed, new DeadlineAssertions(), client, f.Clock);
        Assert.Equal("credential-token-acquisition-timeout", (await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync())).Code);
        Assert.Equal(1, handler.Calls); Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
        using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
        Assert.Equal(1, (await f.Vault.PutAsync(f.Scope, material, f.Clock.Now.AddHours(1))).Generation);
    }

    [Fact]
    public void Trusted_timeout_options_cannot_disable_or_unbound_credential_operation_deadlines()
    {
        using var f = new CredentialFixture();
        foreach (var invalid in new[] { Timeout.InfiniteTimeSpan, TimeSpan.Zero, TimeSpan.FromMilliseconds(49), TimeSpan.FromSeconds(31) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialTokenHttpClient(operationTimeout: invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineAuthenticatedTransport(f.Vault, new DeadlinePermits(f), operationTimeout: invalid));
        }
    }

    sealed class DeadlinePermits(CredentialFixture fixture) : ICredentialDispatchPermitSource
    {
        int calls;
        public bool HoldFinal { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (HoldFinal && call % 2 == 0) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return CredentialDispatchPermit.FromDurableDispatch(fixture.Dispatch, wire, fixture.Scope, fixture.Clock.Now);
        }
    }
    sealed class DeadlineKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public bool Hold { get; set; }
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }
    sealed class DeadlineAssertions : ICredentialEntraAssertionProvider
    {
        public const string Tenant = "11111111-1111-1111-1111-111111111111", Client = "22222222-2222-2222-2222-222222222222";
        public bool Hold { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialSecret> GetAssertionAsync(CredentialScope scope, string tenantId, string clientId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CredentialSecret.FromUtf8("synthetic-federated-assertion");
        }
    }
    sealed class DeadlineGitHubKey : ICredentialGitHubAppKeyProvider
    {
        public int Calls { get; private set; }
        public async ValueTask<CredentialSecret> GetPrivateKeyAsync(CredentialScope scope, CancellationToken cancellationToken)
        { Calls++; await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("Unreachable fixture key lookup."); }
    }
    sealed class DeadlineHandler : HttpMessageHandler
    {
        public bool StallBody { get; init; }
        public bool Acquisition { get; init; }
        public int Calls { get; private set; }
        public TaskCompletionSource<CancellationToken> BodyReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            HttpContent body = StallBody ? new StreamContent(new DeadlineStream(BodyReadEntered)) : new StringContent(Acquisition ?
                "{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\"" + CredentialFixture.Token + "\"}" :
                "{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
    sealed class DeadlineStream(TaskCompletionSource<CancellationToken> entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous response reads are forbidden.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(cancellationToken); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
