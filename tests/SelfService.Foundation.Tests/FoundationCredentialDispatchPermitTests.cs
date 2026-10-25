using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialDispatchPermitTests
{
    const string Runtime = "1.0.0";

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Each_transport_callback_reads_current_SQLite_authority_before_one_mock_POST(string provider)
    {
        using var f = new CredentialFixture(provider); await f.StoreToken(); var store = Store(f);
        var callbacks = new CountedSource(Source(f, store)); using var handler = new CaptureHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, callbacks, handler, f.Clock);
        var before = AuditCount(f);
        Assert.Equal(200, (await transport.SendAsync(f.Wire, default)).StatusCode);
        Assert.Equal(2, callbacks.Calls); Assert.Equal(1, handler.Calls); Assert.Equal("Bearer " + CredentialFixture.Token, handler.Authorization);
        Assert.Equal(before, AuditCount(f)); Assert.Equal("Dispatched", store.GetJob(f.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("operator")]
    [InlineData("regranted-requester")]
    [InlineData("connection")]
    [InlineData("policy")]
    [InlineData("cancel")]
    [InlineData("suspend")]
    [InlineData("lease")]
    [InlineData("instance")]
    [InlineData("source")]
    [InlineData("catalog")]
    [InlineData("approval")]
    public async Task Authority_changed_during_asynchronous_key_custody_is_reloaded_and_admits_zero_POSTs(string change)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var store = Store(f);
        var callbacks = new CountedSource(Source(f, store)); var keys = new HeldKeys(f.Keys);
        var vault = f.Reopen(keys); using var handler = new CaptureHandler(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(vault, callbacks, handler, f.Clock);
        var sending = transport.SendAsync(f.Wire, stop.Token);
        try
        {
            await keys.Entered.Task.WaitAsync(stop.Token);
            switch (change)
            {
                case "requester": case "approver": case "operator":
                    store.SetMembership(f.Scope.CustomerId, "admin", change, "default", "dev", [], false); break;
                case "regranted-requester": store.SetMembership(f.Scope.CustomerId, "admin", "requester", "default", "dev", ["Requester"]); break;
                case "connection": store.PutConnection(f.Scope.CustomerId, "admin", f.Connection.Id, f.Connection.Provider, f.Dispatch.Connection.ConfigurationJson); break;
                case "policy": Execute(f, "UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1"); break;
                case "cancel": store.CancelJob(f.Scope.CustomerId, "operator", f.Dispatch.Job.Id); break;
                case "suspend": store.SetCustomerEnabled(f.Scope.CustomerId, "admin", false); break;
                case "lease": f.Clock.Now = f.Clock.Now.AddSeconds(121); break;
                case "instance": Execute(f, "UPDATE cell SET database_instance_id='abababababababababababababababab' WHERE singleton=1"); break;
                case "source": Execute(f, "UPDATE plans SET source_revision='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'"); break;
                case "catalog": Execute(f, "UPDATE plans SET catalog_digest='cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'"); break;
                case "approval": Execute(f, "DELETE FROM approvals"); break;
            }
        }
        finally { keys.Release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => sending);
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode); Assert.Equal(2, callbacks.Calls); Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        // Authorization denial cannot retain the cross-instance vault admission guard.
        Assert.False((await f.Reopen().RevokeAsync(f.Scope.AuthProfileId, 1)).Enabled);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("fence")]
    [InlineData("job")]
    [InlineData("plan")]
    [InlineData("source")]
    [InlineData("catalog")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("profile")]
    [InlineData("connection")]
    [InlineData("wire")]
    public async Task Caller_selected_pins_scope_or_wire_never_replace_stored_authority(string mismatch)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var scope = f.Scope;
        if (mismatch == "project") scope = scope with { ProjectId = "other-project" };
        if (mismatch == "environment") scope = scope with { EnvironmentId = "prod" };
        if (mismatch == "profile") scope = scope with { AuthProfileId = "other-profile" };
        if (mismatch == "connection") scope = scope with { ConnectionId = "other-connection" };
        var source = new FoundationCredentialDispatchPermitSource(Store(f), mismatch == "job" ? "missing-job" : f.Dispatch.Job.Id,
            mismatch == "worker" ? "other-worker" : f.Dispatch.Job.WorkerId!, mismatch == "fence" ? f.Dispatch.Job.Fence + 1 : f.Dispatch.Job.Fence,
            scope, mismatch == "plan" ? new string('b', 64) : f.Dispatch.Plan.Digest,
            mismatch == "source" ? new string('b', 40) : f.Dispatch.Plan.SourceRevision,
            mismatch == "catalog" ? new string('c', 64) : f.Dispatch.Plan.CatalogDigest, new EntitlementVerifier([], f.Clock), Runtime);
        var wire = mismatch == "wire" ? f.Wire with { BodyJson = "{}" } : f.Wire;
        using var handler = new CaptureHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, source, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default)); Assert.Equal(0, handler.Calls);
        Assert.Equal("Dispatched", Store(f).GetJob(f.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_refuses_another_customer_or_cell_before_current_authority_lookup(bool customer)
    {
        using var f = new CredentialFixture();
        var scope = customer ? f.Scope with { CustomerId = "10000000-0000-0000-0000-000000000099" } :
            f.Scope with { CellId = "20000000-0000-0000-0000-000000000099" };
        Assert.Equal("credential-durable-dispatch-binding-invalid", Assert.Throws<CredentialException>(() => Source(f, Store(f), scope: scope)).Code);
    }

    [Fact]
    public async Task Restored_copy_holds_dispatched_work_and_cannot_issue_a_permit_or_POST()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var source = Store(f);
        var snapshot = source.CreateBackup(Path.Combine(f.Root, "snapshot.db"));
        var restored = FoundationStore.RestoreBackup(snapshot, Path.Combine(f.Root, "restored.db"), f.Clock);
        Assert.NotEqual(source.GetCellInstanceIdentity().DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal("OutcomeUnknown", restored.GetJob(f.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
        using var handler = new CaptureHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, Source(f, restored), handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_released_or_switched_real_activation_lease_cannot_supply_new_authority()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var source = Store(f); var db = Path.Combine(f.Root, "cell.db");
        var authority = FoundationCellActivationAuthority.Provision(Path.Combine(f.Root, "authority"), f.Scope.CustomerId, f.Scope.CellId);
        var current = authority.Activate(db, source.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        using var lease = authority.AcquireLease(db);
        var guarded = new FoundationStore(db, f.Scope.CellId, f.Scope.CustomerId, f.Clock, initializeSchema: false, activationGuard: lease.AssertCurrent);
        var permits = Source(f, guarded); Assert.NotNull(await permits.AuthorizeAsync(f.Wire, default));
        var other = new FoundationStore(Path.Combine(f.Root, "other.db"), f.Scope.CellId, f.Scope.CustomerId, f.Clock);
        Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => authority.Activate(Path.Combine(f.Root, "other.db"),
            other.GetCellInstanceIdentity().DatabaseInstanceId, current.Generation, current.Sha256)).Code);
        lease.Dispose(); _ = authority.Activate(Path.Combine(f.Root, "other.db"), other.GetCellInstanceIdentity().DatabaseInstanceId, current.Generation, current.Sha256);
        using var handler = new CaptureHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Scope_arrays_are_detached_and_cancellation_does_not_read_custody_or_send()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var source = Source(f, Store(f));
        f.Scope.RequiredScopes[0] = "caller-mutated-scope";
        Assert.NotNull(await source.AuthorizeAsync(f.Wire, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.AuthorizeAsync(f.Wire, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("signed-required")]
    [InlineData("runtime")]
    [InlineData("disabled-feature")]
    public async Task Current_disconnected_feature_and_trusted_runtime_policy_restrict_permits(string policy)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var store = Store(f); var dispatch = f.Dispatch; var wire = f.Wire;
        if (policy == "disabled-feature")
        {
            var config = new FoundationFeatureConfiguration("LocalPreview", [FoundationFeatures.SourceCatalog], 100, "0.0.0", "2.0.0");
            var verifier = new EntitlementVerifier([], f.Clock);
            var plan = store.PrepareConfigurationChange(f.Scope.CustomerId, "admin", "default", "dev", config, 0, "Reviewed synthetic feature restriction", verifier, Runtime);
            store.ApproveAdministrativePlan(f.Scope.CustomerId, "approver", plan.Id, plan.Digest);
            store.ExecuteConfigurationChange(f.Scope.CustomerId, "admin", plan.Id, plan.Digest, verifier, Runtime);
            (dispatch, wire) = DispatchAgain(f, store);
        }
        var source = new FoundationCredentialDispatchPermitSource(store, dispatch.Job.Id, dispatch.Job.WorkerId!, dispatch.Job.Fence,
            f.Scope, dispatch.Plan.Digest, dispatch.Plan.SourceRevision, dispatch.Plan.CatalogDigest,
            new EntitlementVerifier([], f.Clock, requireSignedMode: policy == "signed-required"), policy == "runtime" ? "invalid-runtime" : Runtime);
        using var handler = new CaptureHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, source, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default)); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Signed_entitlement_expiry_without_a_policy_change_is_rechecked_before_secret_resolution()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var store = Store(f); using var key = RSA.Create(2048);
        var document = new EntitlementDocument(EntitlementVerifier.DocumentSchema, "fixture-issuer", "fixture-license", f.Scope.CustomerId, f.Scope.CellId,
            1, "evaluation", FoundationFeatures.Available.ToArray(), f.Clock.Now.AddMinutes(-1), f.Clock.Now.AddMinutes(1), "fixture-key");
        var signed = new SignedEntitlement(document, Convert.ToBase64String(key.SignData(EntitlementVerifier.CanonicalDocument(document), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        var verifier = new EntitlementVerifier([new("fixture-issuer", "fixture-key", key.ExportSubjectPublicKeyInfoPem())], f.Clock, requireSignedMode: true);
        var config = new FoundationFeatureConfiguration("Licensed", FoundationFeatures.Available.ToArray(), 100, "0.0.0", "2.0.0", signed);
        var plan = store.PrepareConfigurationChange(f.Scope.CustomerId, "admin", "default", "dev", config, 0, "Reviewed synthetic signed entitlement", verifier, Runtime);
        store.ApproveAdministrativePlan(f.Scope.CustomerId, "approver", plan.Id, plan.Digest);
        store.ExecuteConfigurationChange(f.Scope.CustomerId, "admin", plan.Id, plan.Digest, verifier, Runtime);
        var (dispatch, wire) = DispatchAgain(f, store);
        var source = new FoundationCredentialDispatchPermitSource(store, dispatch.Job.Id, dispatch.Job.WorkerId!, dispatch.Job.Fence,
            f.Scope, dispatch.Plan.Digest, dispatch.Plan.SourceRevision, dispatch.Plan.CatalogDigest, verifier, Runtime);
        Assert.NotNull(await source.AuthorizeAsync(wire, default)); var keyLookups = f.Keys.Requests.Count;
        f.Clock.Now = f.Clock.Now.AddSeconds(61);
        using var handler = new CaptureHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, source, handler, f.Clock);
        await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(wire, default)); Assert.Equal(0, handler.Calls);
        Assert.Equal(keyLookups, f.Keys.Requests.Count); Assert.Equal("Dispatched", store.GetJob(f.Scope.CustomerId, "operator", dispatch.Job.Id).State);
    }

    static (FoundationDispatch Dispatch, PipelineWireRequest Wire) DispatchAgain(CredentialFixture f, FoundationStore store)
    {
        var request = f.Request with { RequestId = Guid.NewGuid().ToString("D") };
        var plan = store.CreatePlan(f.Scope.CustomerId, "requester", new("default", "dev", f.Connection.Id, "pipeline.dispatch",
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, f.Clock.Now.AddMinutes(20), request.SourceRevision));
        store.ApprovePlan(f.Scope.CustomerId, "approver", plan.Id, plan.Digest);
        var job = store.EnqueuePlan(f.Scope.CustomerId, "operator", plan.Id, plan.Digest, "second-job");
        var claim = store.ClaimNext(f.Scope.CustomerId, "credential-worker", TimeSpan.FromMinutes(2))!;
        var dispatch = store.StartDispatch(f.Scope.CustomerId, job.Id, "credential-worker", claim.Fence);
        return (dispatch, PipelineRequestFactory.Create(f.Connection, request, new(f.Scope.CustomerId, f.Scope.CellId, f.Connection.Id,
            f.Connection.Generation, PipelineRequestFactory.ConnectionDigest(f.Connection), PipelineRequestFactory.RequestDigest(f.Connection, request),
            plan.Digest, plan.ExpiresAt), f.Clock.Now));
    }

    static FoundationStore Store(CredentialFixture f) => new(Path.Combine(f.Root, "cell.db"), f.Scope.CellId, f.Scope.CustomerId, f.Clock, initializeSchema: false);
    static FoundationCredentialDispatchPermitSource Source(CredentialFixture f, FoundationStore store, CredentialScope? scope = null,
        EntitlementVerifier? verifier = null, string runtime = Runtime) => new(store, f.Dispatch.Job.Id, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            scope ?? f.Scope, f.Dispatch.Plan.Digest, f.Dispatch.Plan.SourceRevision, f.Dispatch.Plan.CatalogDigest, verifier ?? new([], f.Clock), runtime);
    static void Execute(CredentialFixture f, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Root, "cell.db"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    static long AuditCount(CredentialFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Root, "cell.db"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM audit"; return (long)command.ExecuteScalar()!;
    }
    sealed class CountedSource(ICredentialDispatchPermitSource source) : ICredentialDispatchPermitSource
    {
        public int Calls { get; private set; }
        public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        { Calls++; return source.AuthorizeAsync(wire, cancellationToken); }
    }
    sealed class HeldKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }
    sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Authorization = request.Headers.Authorization?.ToString(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
    }
}
