using System.Net;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialBoundTransportCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Exact_persisted_strong_namespace_and_actual_activation_authorize_one_owned_mock_operation(string provider)
    {
        using var f = await BoundTransportFixture.Create(provider); using var handler = new BoundCaptureHandler();
        using var transport = f.Transport(handler);
        var response = await transport.SendAsync(f.Wire, default);
        Assert.Equal(200, response.StatusCode); Assert.Equal(1, handler.Calls); Assert.True(handler.ContentDisposed);
        Assert.Equal("Bearer " + CredentialFixture.Token, handler.Authorization);
        Assert.True(transport.IsMock); Assert.Equal(f.Binding, f.Store.GetCredentialRuntimeBindingStatus().CurrentBinding);
        Assert.NotNull(f.Binding!.Namespace.AuthorityNamespaceId);
        Assert.DoesNotContain(f.Cell.Root, JsonSerializer.Serialize(f.Binding));
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(f.Binding));
    }

    [Fact]
    public async Task External_transport_remains_disabled_before_keys_or_operation()
    {
        using var f = await BoundTransportFixture.Create(); f.Cell.Keys.Requests.Clear();
        using var transport = new FoundationCredentialBoundTransport(f.Store, f.Vault, f.Authority, f.Namespace, f.AuthorityIdentity,
            f.Instance, f.Binding!.Sha256, f.DispatchPins, f.Verifier, BoundTransportFixture.Runtime, clock: f.Cell.Clock);
        await Assert.ThrowsAsync<PipelineTransportDisabledException>(() => transport.SendAsync(f.Wire, default));
        Assert.False(transport.IsMock); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Required_binding_rejects_the_old_foundation_permit_path_before_any_source_key()
    {
        using var f = await BoundTransportFixture.Create(); using var handler = new BoundCaptureHandler();
        var old = new FoundationCredentialDispatchPermitSource(f.Store, f.Dispatch.Job.Id, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            f.Cell.Scope, f.Dispatch.Plan.Digest, f.Dispatch.Plan.SourceRevision, f.Dispatch.Plan.CatalogDigest, f.Verifier, BoundTransportFixture.Runtime);
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, old, handler, f.Cell.Clock);
        f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Bound_factory_never_falls_back_when_the_current_cell_was_never_bound()
    {
        using var f = await BoundTransportFixture.Create(bind: false); using var handler = new BoundCaptureHandler();
        using var transport = f.Transport(handler, new string('a', 64)); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, handler.Calls);
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
    }

    [Fact]
    public async Task Reviewed_binding_digest_is_an_exact_constraint_before_any_source_key()
    {
        using var f = await BoundTransportFixture.Create(); using var handler = new BoundCaptureHandler();
        using var transport = f.Transport(handler, new string('b', 64)); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("operator")]
    public async Task Authority_lost_after_send_is_uncertain_and_owned_response_is_disposed_before_final_denial(string actor)
    {
        using var f = await BoundTransportFixture.Create();
        using var handler = new BoundCaptureHandler(() => f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", actor, "default", "dev", [], false));
        using var transport = f.Transport(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-bound-operation-uncertain", error.Message);
        Assert.Equal(1, handler.Calls); Assert.True(handler.ContentDisposed);
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        // An error must release this operation's actual activation guard once all owned awaits and disposal have finished.
        var other = new FoundationStore(Path.Combine(f.Cell.Root, "other.db"), f.Cell.Scope.CellId, f.Cell.Scope.CustomerId, f.Cell.Clock);
        var current = f.Authority.ReadCurrent()!;
        var switched = f.Authority.Activate(Path.Combine(f.Cell.Root, "other.db"), other.GetCellInstanceIdentity().DatabaseInstanceId,
            current.Generation, current.Sha256);
        Assert.Equal(current.Generation + 1, switched.Generation);
    }
}

internal sealed class BoundTransportFixture : IDisposable
{
    public const string Runtime = "1.0.0";
    public CredentialFixture Cell { get; }
    public FoundationStore Store { get; }
    public CredentialVault Vault { get; }
    public FoundationCellActivationAuthority Authority { get; }
    public FoundationCellActivationAuthorityIdentity AuthorityIdentity { get; }
    public CredentialVaultNamespaceIdentity Namespace { get; }
    public string Instance { get; }
    public string DatabasePath { get; }
    public FoundationCredentialRuntimeBinding? Binding { get; }
    public FoundationDispatch Dispatch { get; private set; } = null!;
    public PipelineWireRequest Wire { get; private set; } = null!;
    public EntitlementVerifier Verifier { get; }
    public FoundationCredentialBoundDispatch DispatchPins => new(Dispatch.Job.Id, Dispatch.Job.WorkerId!, Dispatch.Job.Fence, Cell.Scope,
        Dispatch.Plan.Digest, Dispatch.Plan.SourceRevision, Dispatch.Plan.CatalogDigest);

    BoundTransportFixture(string provider, bool bind)
    {
        Cell = new CredentialFixture(provider);
        DatabasePath = Path.Combine(Cell.Root, "bound-cell.db");
        Store = new(DatabasePath, Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
        Store.BootstrapAdministrator("admin");
        foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
            Store.SetMembership(Cell.Scope.CustomerId, "admin", actor, "default", "dev", [role]);
        Store.PutConnection(Cell.Scope.CustomerId, "admin", Cell.Connection.Id, provider, Cell.Dispatch.Connection.ConfigurationJson);
        var options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "bound-vault"),
            RollbackProtection = new(Path.Combine(Cell.Root, "bound-rollback")) };
        Vault = CredentialVault.ProvisionStrong(options, Cell.Keys, Cell.Clock); Namespace = Vault.DescribeNamespace();
        Instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
        Authority = FoundationCellActivationAuthority.Provision(Path.Combine(Cell.Root, "bound-activation"), Cell.Scope.CustomerId, Cell.Scope.CellId, Cell.Clock);
        _ = Authority.Activate(DatabasePath, Instance, 0, null); AuthorityIdentity = Authority.DescribeIdentity();
        if (bind) Binding = FoundationCredentialBoundTransport.BindCurrent(Store, Vault, Authority, Namespace, AuthorityIdentity, Instance);
        Verifier = new([], Cell.Clock);
    }

    public static async Task<BoundTransportFixture> Create(string provider = PipelineRequestFactory.GitHub, bool bind = true)
    {
        var fixture = new BoundTransportFixture(provider, bind);
        try
        {
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            await fixture.Vault.PutAsync(fixture.Cell.Scope, secret, fixture.Cell.Clock.Now.AddHours(1));
            var plan = fixture.Store.CreatePlan(fixture.Cell.Scope.CustomerId, "requester", new("default", "dev", fixture.Cell.Connection.Id,
                "pipeline.dispatch", JsonSerializer.Serialize(fixture.Cell.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true,
                fixture.Cell.Clock.Now.AddMinutes(20), fixture.Cell.Request.SourceRevision));
            fixture.Store.ApprovePlan(fixture.Cell.Scope.CustomerId, "approver", plan.Id, plan.Digest);
            using var admission = bind ? new BoundFixtureAdmission(fixture.Store, fixture.Vault, fixture.Authority, fixture.Cell.Scope, fixture.Cell.Clock) : null;
            var keyReads = fixture.Cell.Keys.Requests.Count;
            var job = admission is null ? fixture.Store.EnqueuePlan(fixture.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "bound-request") :
                admission.Run.EnqueuePlan(fixture.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "bound-request");
            var claim = admission is null ? fixture.Store.ClaimNext(fixture.Cell.Scope.CustomerId, "bound-worker", TimeSpan.FromMinutes(2))! :
                admission.Run.ClaimNext(fixture.Cell.Scope.CustomerId, "bound-worker", TimeSpan.FromMinutes(2))!;
            fixture.Dispatch = admission is null ? fixture.Store.StartDispatch(fixture.Cell.Scope.CustomerId, job.Id, "bound-worker", claim.Fence) :
                admission.Run.StartMockDispatch(fixture.Cell.Scope.CustomerId, job.Id, "bound-worker", claim.Fence, fixture.Verifier, Runtime);
            Assert.Equal(keyReads, fixture.Cell.Keys.Requests.Count);
            fixture.Wire = PipelineRequestFactory.Create(fixture.Cell.Connection, fixture.Cell.Request,
                new(fixture.Cell.Scope.CustomerId, fixture.Cell.Scope.CellId, fixture.Cell.Connection.Id, 1,
                    PipelineRequestFactory.ConnectionDigest(fixture.Cell.Connection),
                    PipelineRequestFactory.RequestDigest(fixture.Cell.Connection, fixture.Cell.Request), plan.Digest, plan.ExpiresAt), fixture.Cell.Clock.Now);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public FoundationCredentialBoundTransport Transport(HttpMessageHandler handler, string? bindingSha256 = null, CredentialVault? vault = null,
        TimeSpan? timeout = null) => FoundationCredentialBoundTransport.ForMockHandler(Store, vault ?? Vault, Authority, Namespace, AuthorityIdentity,
            Instance, bindingSha256 ?? Binding!.Sha256, DispatchPins, Verifier, Runtime, handler, Cell.Clock, timeout);

    public void Dispose() => Cell.Dispose();
}

internal sealed class BoundCaptureHandler(Action? onSend = null) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? Authorization { get; private set; }
    public bool ContentDisposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; Authorization = request.Headers.Authorization?.ToString(); onSend?.Invoke();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new TrackedContent(() => ContentDisposed = true) });
    }
    sealed class TrackedContent(Action disposed) : StringContent("{}")
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
}
