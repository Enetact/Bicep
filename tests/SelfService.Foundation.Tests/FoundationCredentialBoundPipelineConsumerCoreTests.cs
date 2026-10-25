using System.Net;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialBoundPipelineConsumerCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Keyfree_startup_and_real_run_scope_use_the_existing_dispatcher_for_each_mock_provider(string provider)
    {
        using var f = await BoundTransportFixture.Create(provider); ConsumerHandler? handler = null; var calls = 0;
        using var consumer = Consumer(f, () => { calls++; return handler = new ConsumerHandler(Response(f)); });
        f.Cell.Keys.Requests.Clear(); consumer.ValidateStartup(f.Store);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, calls); Released(f);
        using (var run = consumer.OpenRun(f.Store))
        {
            Busy(f); Assert.Equal(1, consumer.ScopeCount); Assert.Same(f.Cell.Clock, consumer.Clock);
            run.RequireConnection(f.Cell.Connection, "default", "dev");
            Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, calls);
            using (var transport = run.CreateTransport(f.Dispatch, f.Cell.Connection, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
                f.Verifier, BoundTransportFixture.Runtime))
            {
                Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(1, calls);
                var result = await new PipelineDispatcher(transport, consumer.Clock).DispatchAsync(f.Cell.Connection, f.Cell.Request, Approval(f));
                Assert.Equal("Accepted", result.State); Assert.True(result.Mock); Assert.Equal("73", result.ExternalRunId);
                Assert.Equal(1, handler!.Posts); Busy(f); run.AssertCurrent();
            }
            Assert.Equal(1, handler!.Disposals); Busy(f);
        }
        Released(f);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("profile")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("customer")]
    public async Task Prequeue_eligibility_is_an_exact_keyfree_constraint_without_returning_authority(string mismatch)
    {
        using var f = await BoundTransportFixture.Create(); var calls = 0;
        using var consumer = Consumer(f, () => { calls++; return new ConsumerHandler(Response(f)); }); using var run = consumer.OpenRun(f.Store);
        var connection = mismatch switch
        {
            "generation" => f.Cell.Connection with { Generation = 2 }, "digest" => f.Cell.Connection with { Organization = "different-org" },
            "profile" => f.Cell.Connection with { CredentialReference = "profile/different" },
            "customer" => f.Cell.Connection with { CustomerId = "10000000-0000-0000-0000-000000000099" }, _ => f.Cell.Connection
        };
        f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => run.RequireConnection(connection,
            mismatch == "project" ? "other" : "default", mismatch == "environment" ? "qa" : "dev"));
        Assert.Equal("BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(403, error.Status);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, calls); Busy(f);
        Assert.Equal("Dispatched", f.Store.GetJob(f.Cell.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
    }

    [Fact]
    public async Task Startup_rechecks_current_persisted_binding_without_keys_handlers_or_mutation()
    {
        using var f = await BoundTransportFixture.Create(); var calls = 0;
        using var consumer = Consumer(f, () => { calls++; return new ConsumerHandler(Response(f)); }, binding: new string('b', 64));
        var status = f.Store.GetCredentialRuntimeBindingStatus(); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => consumer.ValidateStartup(f.Store));
        Assert.Equal("CredentialRuntimeBindingMismatch", error.Code); Assert.Equal(status, f.Store.GetCredentialRuntimeBindingStatus());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, calls); Released(f);
    }

    [Fact]
    public async Task Configuration_snapshots_both_the_scope_list_and_its_nested_permissions()
    {
        using var f = await BoundTransportFixture.Create(); var supplied = f.Cell.Scope with { RequiredScopes = ["actions:write"] };
        var scopes = new List<CredentialScope> { supplied }; using var consumer = Consumer(f, () => new ConsumerHandler(Response(f)), scopes);
        supplied.RequiredScopes[0] = "untrusted:permission"; scopes[0] = f.Cell.Scope with { ProjectId = "other" }; scopes.Clear();
        using var run = consumer.OpenRun(f.Store); using var transport = run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime);
        Assert.Equal("Accepted", (await new PipelineDispatcher(transport, consumer.Clock).DispatchAsync(f.Cell.Connection, f.Cell.Request, Approval(f))).State);
        Assert.Equal(1, consumer.ScopeCount);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("capacity")]
    [InlineData("duplicate")]
    [InlineData("ambiguous")]
    [InlineData("foreign")]
    [InlineData("invalid")]
    public async Task Scope_configuration_is_finite_exact_and_cannot_supply_foreign_or_ambiguous_authority(string change)
    {
        using var f = await BoundTransportFixture.Create(); var scope = f.Cell.Scope;
        IReadOnlyList<CredentialScope> scopes = change switch
        {
            "empty" => [], "capacity" => Enumerable.Repeat(scope, FoundationCredentialBoundPipelineConsumer.MaximumScopes + 1).ToArray(),
            "duplicate" => [scope, scope], "ambiguous" => [scope, scope with { RequiredScopes = ["actions:write", "contents:read"] }],
            "foreign" => [scope with { CustomerId = "10000000-0000-0000-0000-000000000099" }],
            _ => [scope with { RequiredScopes = ["untrusted:permission"] }]
        };
        var calls = 0; f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<CredentialException>(() => Consumer(f, () => { calls++; return new ConsumerHandler(Response(f)); }, scopes));
        Assert.Equal("credential-bound-consumer-configuration-invalid", error.Code); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, calls);
        Released(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_null_or_exception_is_opaque_and_has_no_key_or_HTTP_effect(bool throws)
    {
        using var f = await BoundTransportFixture.Create(); var calls = 0;
        using var consumer = Consumer(f, () => { calls++; if (throws) throw new IOException(CredentialFixture.Token); return null!; });
        using var run = consumer.OpenRun(f.Store); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<PipelineContractException>(() => run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal("pipeline-bound-consumer-handler-unavailable", error.ReasonCode); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(1, calls); Busy(f);
    }

    [Fact]
    public async Task Handler_reuse_is_rejected_without_disposing_its_first_transport_or_fetching_keys()
    {
        using var f = await BoundTransportFixture.Create(); var handler = new ConsumerHandler(Response(f));
        using var consumer = Consumer(f, () => handler); using var run = consumer.OpenRun(f.Store);
        using var first = run.CreateTransport(f.Dispatch, f.Cell.Connection, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            f.Verifier, BoundTransportFixture.Runtime); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<PipelineContractException>(() => run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal("pipeline-bound-consumer-handler-reused", error.ReasonCode); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, handler.Disposals);
        Assert.Equal("Accepted", (await new PipelineDispatcher(first, consumer.Clock).DispatchAsync(f.Cell.Connection, f.Cell.Request, Approval(f))).State);
        first.Dispose(); Assert.Equal(1, handler.Disposals);
        Assert.Throws<PipelineContractException>(() => run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal(1, handler.Disposals);
    }

    [Fact]
    public async Task Authority_changed_by_the_factory_is_reloaded_before_ownership_is_returned()
    {
        using var f = await BoundTransportFixture.Create(); var handler = new ConsumerHandler(Response(f));
        using var consumer = Consumer(f, () =>
        { f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", "requester", "default", "dev", [], false); return handler; });
        using var run = consumer.OpenRun(f.Store); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<PipelineContractException>(() => run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(0, handler.Posts); Assert.Equal(1, handler.Disposals); Busy(f);
    }

    [Fact]
    public async Task Consumer_disposal_blocks_new_work_but_does_not_dispose_an_owned_run_or_transport()
    {
        using var f = await BoundTransportFixture.Create(); var handler = new ConsumerHandler(Response(f));
        using var consumer = Consumer(f, () => handler); using var run = consumer.OpenRun(f.Store);
        using var transport = run.CreateTransport(f.Dispatch, f.Cell.Connection, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            f.Verifier, BoundTransportFixture.Runtime);
        consumer.Dispose(); Busy(f); run.AssertCurrent(); Assert.Equal(0, handler.Disposals);
        Assert.Equal("BoundPipelineConsumerDisposed", Assert.Throws<FoundationStoreException>(() => consumer.OpenRun(f.Store)).Code);
        Assert.Throws<FoundationStoreException>(() => run.CreateTransport(f.Dispatch, f.Cell.Connection,
            f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal("Accepted", (await new PipelineDispatcher(transport, consumer.Clock).DispatchAsync(f.Cell.Connection, f.Cell.Request, Approval(f))).State);
        Busy(f); transport.Dispose(); run.Dispose(); Released(f);
    }

    [Fact]
    public async Task Handler_disposal_failure_is_opaque_without_transferring_a_shared_owner()
    {
        using var f = await BoundTransportFixture.Create(); var handler = new ConsumerHandler(Response(f), failDispose: true);
        using var consumer = Consumer(f, () => handler); using var run = consumer.OpenRun(f.Store);
        var transport = run.CreateTransport(f.Dispatch, f.Cell.Connection, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            f.Verifier, BoundTransportFixture.Runtime); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<PipelineContractException>(() => transport.Dispose());
        Assert.Equal("pipeline-bound-consumer-handler-unavailable", error.ReasonCode); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(1, handler.Disposals); Assert.Equal(0, handler.Posts);
        run.Dispose(); Released(f);
    }

    [Fact]
    public async Task Concurrent_metadata_runs_do_not_take_admission_gates_and_do_not_weaken_actual_writers()
    {
        using var f = await BoundTransportFixture.Create(); var factoryCalls = 0;
        using var consumer = Consumer(f, () => { factoryCalls++; return new ConsumerHandler(Response(f)); });
        using var vaultGate = new FileStream(Path.Combine(f.Cell.Root, "bound-vault", ".vault.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var rollbackGate = new FileStream(Path.Combine(f.Cell.Root, "bound-rollback", ".rollback.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        f.Cell.Keys.Requests.Clear(); consumer.ValidateStartup(f.Store);
        using var first = consumer.OpenRun(f.Store); using var second = consumer.OpenRun(f.Store);
        first.RequireConnection(f.Cell.Connection, "default", "dev"); second.AssertCurrent();
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, factoryCalls);
        foreach (var path in MetadataFiles(f))
            Assert.Throws<IOException>(() => { using var unexpected = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); });
        var writing = f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1);
        Assert.False(writing.IsCompleted); Assert.Empty(f.Cell.Keys.Requests);
        vaultGate.Dispose(); rollbackGate.Dispose();
        var revoked = await writing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(revoked.Enabled); Assert.Equal(2, revoked.Generation);
        first.AssertCurrent(); second.AssertCurrent(); Assert.Equal(0, factoryCalls); Busy(f);
    }

    [Fact]
    public async Task Metadata_readiness_does_not_read_or_authorize_the_mutable_rollback_ledger()
    {
        using var f = await BoundTransportFixture.Create(); using var consumer = Consumer(f, () => new ConsumerHandler(Response(f)));
        var statePath = Path.Combine(f.Cell.Root, "bound-rollback", "state.json"); var before = await File.ReadAllBytesAsync(statePath);
        using (var ledger = new FileStream(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            f.Cell.Keys.Requests.Clear(); consumer.ValidateStartup(f.Store); using var run = consumer.OpenRun(f.Store); run.AssertCurrent();
            Assert.Empty(f.Cell.Keys.Requests);
            using var handler = new ConsumerHandler(Response(f)); using var transport = f.Transport(handler);
            var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
            Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
            Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, handler.Posts);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath)); Released(f);
    }

    [Fact]
    public async Task Multiple_consumer_readers_coexist_with_a_real_dispatch_waiting_for_its_source_key()
    {
        using var f = await BoundTransportFixture.Create(); var held = new ConsumerHeldKeys(f.Cell.Keys); var factoryCalls = 0;
        var vaultOptions = f.Cell.Options with { RootPath = Path.Combine(f.Cell.Root, "bound-vault"),
            RollbackProtection = new(Path.Combine(f.Cell.Root, "bound-rollback")) };
        var vault = new CredentialVault(vaultOptions, held, f.Cell.Clock);
        using var consumer = Consumer(f, () => { factoryCalls++; return new ConsumerHandler(Response(f)); });
        using var handler = new ConsumerHandler(Response(f)); using var transport = f.Transport(handler, vault: vault);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); f.Cell.Keys.Requests.Clear();
        var pending = transport.SendAsync(f.Wire, timeout.Token); PipelineWireResponse? response = null;
        try
        {
            await held.Entered.Task.WaitAsync(timeout.Token);
            Assert.Throws<IOException>(() => { using var unexpected = new FileStream(Path.Combine(f.Cell.Root, "bound-vault", ".vault.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            consumer.ValidateStartup(f.Store);
            using var first = consumer.OpenRun(f.Store); using var second = consumer.OpenRun(f.Store);
            first.RequireConnection(f.Cell.Connection, "default", "dev"); second.AssertCurrent();
            Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, factoryCalls); Assert.Equal(0, handler.Posts); Assert.False(pending.IsCompleted); Busy(f);
        }
        finally { held.Release.TrySetResult(); response = await pending.WaitAsync(timeout.Token); }
        Assert.NotNull(response); Assert.Equal(200, response.StatusCode); Assert.Equal(1, handler.Posts); Released(f);
    }

    static IEnumerable<string> MetadataFiles(BoundTransportFixture f) =>
    [Path.Combine(f.Cell.Root, "bound-vault", ".credential-namespace.json"), Path.Combine(f.Cell.Root, "bound-vault", "rollback-binding.json"),
        Path.Combine(f.Cell.Root, "bound-rollback", ".credential-namespace.json"), Path.Combine(f.Cell.Root, "bound-rollback", "authority.json")];

    sealed class ConsumerHeldKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken);
        }
    }

    static FoundationCredentialBoundPipelineConsumer Consumer(BoundTransportFixture f, Func<HttpMessageHandler> factory,
        IReadOnlyList<CredentialScope>? scopes = null, string? binding = null) => FoundationCredentialBoundPipelineConsumer.ForMockHandlers(
            f.Vault, f.Authority, f.Namespace, f.AuthorityIdentity, f.Instance, binding ?? f.Binding!.Sha256, scopes ?? [f.Cell.Scope], factory, f.Cell.Clock);

    static PipelineApproval Approval(BoundTransportFixture f) => new(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, f.Cell.Connection.Id,
        f.Cell.Connection.Generation, PipelineRequestFactory.ConnectionDigest(f.Cell.Connection),
        PipelineRequestFactory.RequestDigest(f.Cell.Connection, f.Cell.Request), f.Dispatch.Plan.Digest, f.Dispatch.Plan.ExpiresAt);
    static string Response(BoundTransportFixture f) => f.Cell.Connection.Provider == PipelineRequestFactory.GitHub
        ? JsonSerializer.Serialize(new { workflow_run_id = 73, run_url = "https://api.github.com/repos/customer-org/customer-repo/actions/runs/73",
            html_url = "https://github.com/customer-org/customer-repo/actions/runs/73", head_sha = f.Cell.Request.SourceRevision })
        : JsonSerializer.Serialize(new { id = 73, pipeline = new { id = 42 },
            url = "https://dev.azure.com/customer-org/e1111111-1111-1111-1111-111111111111/_apis/pipelines/42/runs/73" });
    static void Busy(BoundTransportFixture f)
    {
        var binding = f.Authority.ReadCurrent()!;
        Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() =>
            f.Authority.Activate(f.DatabasePath, f.Instance, binding.Generation, binding.Sha256)).Code);
    }
    static void Released(BoundTransportFixture f)
    {
        using var guard = new FileStream(Path.Combine(f.Cell.Root, "bound-activation", ".activation.guard"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, guard.Length);
    }
    sealed class ConsumerHandler(string body, bool failDispose = false) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
        protected override void Dispose(bool disposing)
        { if (disposing) { Disposals++; if (failDispose) throw new IOException(CredentialFixture.Token); } base.Dispose(disposing); }
    }
}
