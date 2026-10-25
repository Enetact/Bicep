using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialBoundTransportBoundaryTests
{
    const string DeniedReason = "pipeline-credential-authority-denied";
    const string UncertainReason = "pipeline-credential-bound-operation-uncertain";

    [Fact]
    public async Task Never_bound_legacy_mock_transport_remains_usable_but_new_bound_factory_never_falls_back()
    {
        using var f = new BoundFixture(bind: false); await f.Initialize();
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        var before = f.Template.Keys.Requests.Count;
        using var boundHandler = new Posts(); using var bound = f.Transport(boundHandler);
        await Denied(() => bound.SendAsync(f.Wire, default));
        Assert.Equal(before, f.Template.Keys.Requests.Count); Assert.Equal(0, boundHandler.Calls);
        using var legacyHandler = new Posts(); using var legacy = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, f.LegacyPermits(), legacyHandler, f.Template.Clock);
        Assert.Equal(200, (await legacy.SendAsync(f.Wire, default)).StatusCode); Assert.Equal(1, legacyHandler.Calls);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binding_policy_is_reloaded_even_by_a_legacy_authorizer_constructed_before_cutover(bool authorizerPredatesBinding)
    {
        using var f = new BoundFixture(bind: false, prepareJob: false);
        // This source predates the binding. Its future job need not exist: the durable binding policy
        // must deny before looking up that job, rather than caching an optional/unbound mode.
        var provisional = f.MakeDispatchPins("future-job", "worker", 1, new string('a', 64));
        var early = new FoundationCredentialDispatchPermitSource(f.Store, provisional.JobId, provisional.WorkerId, provisional.Fence,
            provisional.Scope, provisional.PlanDigest, provisional.SourceRevision, provisional.CatalogDigest, new([], f.Template.Clock), "1.0.0");
        f.Bind(); f.PrepareJob(); await f.Initialize();
        var source = authorizerPredatesBinding ? early : f.LegacyPermits();
        if (authorizerPredatesBinding)
        {
            var frozen = typeof(FoundationCredentialDispatchPermitSource).GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(early);
            var method = typeof(FoundationStore).GetMethod("AuthorizeCurrentCredentialDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var denied = Assert.Throws<TargetInvocationException>(() => method.Invoke(f.Store, [frozen, f.Wire, new EntitlementVerifier([], f.Template.Clock), "1.0.0", CancellationToken.None, null]));
            Assert.Equal("CredentialRuntimeBoundSessionRequired", Assert.IsType<FoundationStoreException>(denied.InnerException).Code);
        }
        using var handler = new Posts(); using var legacy = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, source, handler, f.Template.Clock);
        var before = f.Template.Keys.Requests.Count; await Denied(() => legacy.SendAsync(f.Wire, default));
        Assert.Equal(before, f.Template.Keys.Requests.Count); Assert.Equal(0, handler.Calls);
        using var goodHandler = new Posts(); using var good = f.Transport(goodHandler);
        Assert.Equal(200, (await good.SendAsync(f.Wire, default)).StatusCode); Assert.Equal(1, goodHandler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_same_customer_cell_vault_cannot_replace_the_persisted_namespace_even_with_its_actual_identity(bool callerUsesAlternateIdentity)
    {
        using var f = new BoundFixture(); await f.Initialize();
        var options = f.VaultOptions with { RootPath = f.Path("alternate-vault"), RollbackProtection = null };
        var alternate = CredentialVault.Provision(options, f.Template.Keys, f.Template.Clock);
        Assert.Equal(f.Namespace.CustomerId, alternate.DescribeNamespace().CustomerId); Assert.Equal(f.Namespace.CellId, alternate.DescribeNamespace().CellId);
        Assert.NotEqual(f.Namespace.VaultNamespaceId, alternate.DescribeNamespace().VaultNamespaceId);
        var before = f.Template.Keys.Requests.Count; using var handler = new Posts();
        using var transport = f.Transport(handler, vault: alternate, expectedNamespace: callerUsesAlternateIdentity ? alternate.DescribeNamespace() : f.Namespace);
        await Denied(() => transport.SendAsync(f.Wire, default)); Assert.Equal(before, f.Template.Keys.Requests.Count); Assert.Equal(0, handler.Calls);
        f.AssertActivationReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copied_same_id_activation_root_is_not_the_persisted_guard_authority(bool callerUsesCopiedIdentity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This native custody fixture requires Windows.");
        using var f = new BoundFixture(); await f.Initialize();
        var copied = f.Path("copied-authority-" + Guid.NewGuid().ToString("N")); ActivationProvisionFixtureRoots.Create(copied);
        foreach (var path in Directory.GetFiles(f.AuthorityPath)) File.Copy(path, System.IO.Path.Combine(copied, System.IO.Path.GetFileName(path)));
        var alternate = new FoundationCellActivationAuthority(copied, f.Customer, f.CellId, f.Template.Clock);
        Assert.Equal(f.Activation, alternate.ReadCurrent());
        Assert.Equal(f.Identity.AuthorityId, alternate.DescribeIdentity().AuthorityId);
        Assert.NotEqual(f.Identity.CustodyCommitmentSha256, alternate.DescribeIdentity().CustodyCommitmentSha256);
        var reads = f.Template.Keys.Requests.Count; using var handler = new Posts();
        using var transport = f.Transport(handler, authority: alternate, expectedAuthority: callerUsesCopiedIdentity ? alternate.DescribeIdentity() : f.Identity);
        await Denied(() => transport.SendAsync(f.Wire, default)); Assert.Equal(reads, f.Template.Keys.Requests.Count); Assert.Equal(0, handler.Calls);
        f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("instance")]
    [InlineData("namespace")]
    [InlineData("authority")]
    [InlineData("job")]
    [InlineData("worker")]
    [InlineData("fence")]
    [InlineData("plan")]
    [InlineData("source")]
    [InlineData("catalog")]
    public async Task Well_formed_caller_pin_changes_do_not_supply_current_authority_or_reach_source_keys(string pin)
    {
        using var f = new BoundFixture(); await f.Initialize(); var dispatch = f.Dispatch;
        dispatch = pin switch
        {
            "job" => dispatch with { JobId = "other-job" }, "worker" => dispatch with { WorkerId = "other-worker" },
            "fence" => dispatch with { Fence = dispatch.Fence + 1 }, "plan" => dispatch with { PlanDigest = new string('b', 64) },
            "source" => dispatch with { SourceRevision = new string('b', 40) }, "catalog" => dispatch with { CatalogDigest = new string('c', 64) }, _ => dispatch
        };
        using var handler = new Posts(); using var transport = f.Transport(handler, dispatch: dispatch,
            instance: pin == "instance" ? Guid.NewGuid().ToString("N") : null,
            bindingSha: pin == "binding" ? new string('a', 64) : null,
            expectedNamespace: pin == "namespace" ? f.Namespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } : null,
            expectedAuthority: pin == "authority" ? f.Identity with { AuthorityId = Guid.NewGuid().ToString("N") } : null);
        var before = f.Template.Keys.Requests.Count; await Denied(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(before, f.Template.Keys.Requests.Count); Assert.Equal(0, handler.Calls); f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Claimed")]
    [InlineData("Dispatched")]
    [InlineData("RunningWorker")]
    [InlineData("DisabledWorker")]
    public void Explicit_first_binding_requires_real_local_quiescence_and_does_not_repair_or_cancel_work(string activity)
    {
        using var f = new BoundFixture(bind: false, prepareJob: false);
        if (activity.EndsWith("Worker", StringComparison.Ordinal))
            f.Store.RegisterWorker(f.Customer, "live-cutover-worker", Environment.ProcessId, DateTimeOffset.UtcNow,
                activity == "DisabledWorker" ? "Disabled" : "MockLoopback");
        else f.PrepareJob(activity);
        var jobs = f.Rows("SELECT id,state,worker_id,lease_until,fence,attempt FROM jobs ORDER BY id");
        var workers = f.Rows("SELECT * FROM worker_heartbeats ORDER BY worker_id"); var audit = f.Number("SELECT count(*) FROM audit");
        var error = Assert.Throws<FoundationStoreException>(() => f.Bind());
        Assert.Equal("CredentialRuntimeQuiescenceRequired", error.Code); Assert.Equal(403, error.Status);
        Assert.Equal(jobs, f.Rows("SELECT id,state,worker_id,lease_until,fence,attempt FROM jobs ORDER BY id"));
        Assert.Equal(workers, f.Rows("SELECT * FROM worker_heartbeats ORDER BY worker_id")); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); Assert.Empty(f.Template.Keys.Requests); f.AssertActivationReleased();
    }

    [Fact]
    public void Binding_is_explicit_immutable_metadata_and_cannot_be_reprovisioned_by_an_exact_repeat()
    {
        using var f = new BoundFixture(prepareJob: false); var binding = f.Binding!; var rows = f.Rows("SELECT * FROM credential_runtime_bindings");
        Assert.Empty(f.Template.Keys.Requests); Assert.True(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        Assert.Equal(binding, f.Store.GetCredentialRuntimeBindingStatus().CurrentBinding);
        Assert.Equal("CredentialRuntimeAlreadyBound", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code);
        Assert.Equal(rows, f.Rows("SELECT * FROM credential_runtime_bindings")); Assert.Empty(f.Template.Keys.Requests);
        Assert.False(binding.RebindingAvailable); Assert.False(binding.RecoveryHoldReleased); Assert.False(binding.ExternalExecutionAuthorized);
        var text = JsonSerializer.Serialize(binding);
        foreach (var privateValue in new[] { f.RuntimePath, f.AuthorityPath, f.VaultOptions.RootPath, CredentialFixture.Token, "Ciphertext", "PrivateKey" }) Assert.DoesNotContain(privateValue, text);
        f.AssertActivationReleased();
    }

    [Fact]
    public async Task Case_aliases_of_the_same_actual_database_and_authority_do_not_create_a_different_binding()
    {
        using var f = new BoundFixture(); await f.Initialize();
        var aliasStore = new FoundationStore(f.RuntimePath.ToUpperInvariant(), f.CellId, f.Customer, f.Template.Clock, initializeSchema: false);
        var aliasAuthority = new FoundationCellActivationAuthority(f.AuthorityPath.ToUpperInvariant(), f.Customer, f.CellId, f.Template.Clock);
        Assert.Equal(f.Identity, aliasAuthority.DescribeIdentity()); Assert.Equal(f.Binding, aliasStore.GetCredentialRuntimeBindingStatus().CurrentBinding);
        using var handler = new Posts(); using var transport = f.Transport(handler, store: aliasStore, authority: aliasAuthority);
        Assert.Equal(200, (await transport.SendAsync(f.Wire, default)).StatusCode); Assert.Equal(1, handler.Calls); f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("operator")]
    [InlineData("regrant")]
    [InlineData("connection")]
    [InlineData("policy")]
    public async Task Current_durable_authority_is_rechecked_after_source_key_wait_before_any_POST(string mutation)
    {
        using var f = new BoundFixture(); await f.Initialize(); var keys = new HeldKeys(f.Template.Keys);
        var vault = new CredentialVault(f.VaultOptions, keys, f.Template.Clock); using var handler = new Posts(); using var transport = f.Transport(handler, vault: vault);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var pending = transport.SendAsync(f.Wire, deadline.Token);
        try
        {
            await keys.Entered.Task.WaitAsync(deadline.Token); f.AssertBusy();
            switch (mutation)
            {
                case "requester": case "approver": case "operator": f.Store.SetMembership(f.Customer, "admin", mutation, "default", "dev", [], false); break;
                case "regrant": f.Store.SetMembership(f.Customer, "admin", "requester", "default", "dev", ["Requester"]); break;
                case "connection": f.Store.PutConnection(f.Customer, "admin", f.Template.Connection.Id, f.Template.Connection.Provider, f.Template.Dispatch.Connection.ConfigurationJson); break;
                case "policy": f.Sql("UPDATE cell SET policy_version=policy_version+1"); break;
            }
        }
        finally { keys.Release.TrySetResult(); }
        await Denied(() => pending); Assert.Equal(0, handler.Calls); Assert.Equal("Dispatched", f.Store.GetJob(f.Customer, "auditor", f.Dispatch.JobId).State);
        f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("key")]
    [InlineData("handler")]
    [InlineData("body")]
    [InlineData("body-disposal")]
    public async Task Actual_activation_guard_spans_key_handler_body_and_asynchronous_owned_disposal(string phase)
    {
        using var f = new BoundFixture(); await f.Initialize(); var keys = new HeldKeys(f.Template.Keys);
        var body = new HeldBody(waitForRead: phase == "body", waitForDisposal: phase == "body-disposal");
        using var handler = new Posts(wait: phase == "handler", body: body);
        var vault = phase == "key" ? new CredentialVault(f.VaultOptions, keys, f.Template.Clock) : f.Vault;
        using var transport = f.Transport(handler, vault: vault); using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = transport.SendAsync(f.Wire, watchdog.Token);
        var entered = phase switch { "key" => keys.Entered.Task, "handler" => handler.Entered.Task, "body" => body.ReadEntered.Task, _ => body.DisposeEntered.Task };
        try { await entered.WaitAsync(watchdog.Token); Assert.False(pending.IsCompleted); f.AssertBusy(); }
        finally { keys.Release.TrySetResult(); handler.Release.TrySetResult(); body.ReleaseRead.TrySetResult(); body.ReleaseDispose.TrySetResult(); }
        Assert.Equal(200, (await pending).StatusCode); Assert.Equal(1, handler.Calls); f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("key")]
    [InlineData("handler")]
    [InlineData("body")]
    public async Task Caller_cancellation_cannot_detach_an_uncooperative_owned_await_or_release_its_activation_lease(string phase)
    {
        using var f = new BoundFixture(); await f.Initialize(); var keys = new HeldKeys(f.Template.Keys, ignoreCancellation: true);
        var body = new HeldBody(waitForRead: phase == "body"); using var handler = new Posts(wait: phase == "handler", body: body);
        var vault = phase == "key" ? new CredentialVault(f.VaultOptions, keys, f.Template.Clock) : f.Vault;
        using var transport = f.Transport(handler, vault: vault); using var cancellation = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var pending = transport.SendAsync(f.Wire, cancellation.Token);
        var entered = phase switch { "key" => keys.Entered.Task, "handler" => handler.Entered.Task, _ => body.ReadEntered.Task };
        try
        {
            await entered.WaitAsync(watchdog.Token); cancellation.Cancel(); await Task.Delay(100, watchdog.Token);
            Assert.False(pending.IsCompleted); f.AssertBusy();
        }
        finally { keys.Release.TrySetResult(); handler.Release.TrySetResult(); body.ReleaseRead.TrySetResult(); body.ReleaseDispose.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(phase == "key" ? 0 : 1, handler.Calls); f.AssertActivationReleased();
    }

    [Fact]
    public async Task Disposing_outer_transport_does_not_free_an_operation_whose_handler_still_uses_the_request()
    {
        using var f = new BoundFixture(); await f.Initialize(); using var handler = new Posts(wait: true); var transport = f.Transport(handler);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var pending = transport.SendAsync(f.Wire, default);
        try
        {
            await handler.Entered.Task.WaitAsync(watchdog.Token); transport.Dispose(); await Task.Delay(100, watchdog.Token);
            Assert.False(pending.IsCompleted); f.AssertBusy();
        }
        finally { handler.Release.TrySetResult(); }
        PipelineWireResponse? result = null;
        var error = await Record.ExceptionAsync(async () => result = await pending);
        // HttpClient disposal requests cancellation; an already-invoked uncooperative handler can
        // still deliver an authenticated response. Neither outcome may release the lease early.
        if (error is null) Assert.Equal(200, Assert.IsType<PipelineWireResponse>(result).StatusCode);
        else { Assert.Equal(UncertainReason, Assert.IsType<HttpRequestException>(error).Message); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); }
        Assert.Equal(1, handler.Calls); f.AssertActivationReleased();
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("body")]
    public async Task Failure_after_HTTP_invocation_remains_sanitized_uncertainty_and_does_not_invent_job_success(string phase)
    {
        using var f = new BoundFixture(); await f.Initialize(); var body = new HeldBody(failRead: phase == "body");
        using var handler = new Posts(fail: phase == "handler", body: body); using var transport = f.Transport(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(UncertainReason, error.Message); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Calls); Assert.Equal("Dispatched", f.Store.GetJob(f.Customer, "operator", f.Dispatch.JobId).State);
        Assert.Equal(0, f.Number("SELECT count(*) FROM audit WHERE action='JobCompleted'")); f.AssertActivationReleased();
    }

    [Fact]
    public async Task Successful_switch_after_guard_release_denies_the_old_transport_before_another_key_read()
    {
        using var f = new BoundFixture(); await f.Initialize(); using var handler = new Posts(); using var transport = f.Transport(handler);
        Assert.Equal(200, (await transport.SendAsync(f.Wire, default)).StatusCode); f.AssertActivationReleased();
        var target = f.SwitchTarget(); _ = f.Authority.Activate(target.Path, target.Instance, f.Activation.Generation, f.Activation.Sha256);
        var reads = f.Template.Keys.Requests.Count; await Denied(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(reads, f.Template.Keys.Requests.Count); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Restored_instance_preserves_binding_history_but_owner_activation_cannot_reuse_or_rebind_it()
    {
        using var f = new BoundFixture(); await f.Initialize(); var before = f.Rows("SELECT * FROM credential_runtime_bindings");
        var backup = f.Store.CreateBackup(f.Path("binding-backup.sqlite")); var path = f.Path("restored-binding.sqlite");
        var restored = FoundationStore.RestoreBackup(backup, path, f.Template.Clock); var instance = restored.GetCellInstanceIdentity().DatabaseInstanceId;
        var status = restored.GetCredentialRuntimeBindingStatus(); Assert.True(status.BindingRequired); Assert.Null(status.CurrentBinding); Assert.Equal(1, status.HistoricalBindingCount);
        Assert.Equal(before, Rows(path, "SELECT * FROM credential_runtime_bindings"));
        var selected = f.Authority.Activate(path, instance, f.Activation.Generation, f.Activation.Sha256);
        var reads = f.Template.Keys.Requests.Count;
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => FoundationCredentialBoundTransport.BindCurrent(restored, f.Vault,
            f.Authority, f.Namespace, f.Identity, instance)).Code);
        using var handler = new Posts(); using var transport = f.Transport(handler, store: restored, instance: instance);
        await Denied(() => transport.SendAsync(f.Wire, default)); Assert.Equal(reads, f.Template.Keys.Requests.Count); Assert.Equal(0, handler.Calls);
        Assert.Equal(selected, f.Authority.ReadCurrent()); Assert.Equal(before, Rows(path, "SELECT * FROM credential_runtime_bindings"));
    }

    [Fact]
    public async Task Removing_binding_rows_without_removing_retained_audit_never_downgrades_to_legacy_optional_policy()
    {
        using var f = new BoundFixture(); await f.Initialize();
        using (var db = Open(f.RuntimePath)) using (var transaction = db.BeginTransaction()) using (var query = db.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='credential_runtime_bindings'";
            using var reader = query.ExecuteReader(); var triggers = new List<(string Name, string Sql)>(); while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1))); reader.Close();
            query.CommandText = string.Join(';', triggers.Select(t => "DROP TRIGGER " + t.Name)) + ";DELETE FROM credential_runtime_bindings;" + string.Join(';', triggers.Select(t => t.Sql));
            query.ExecuteNonQuery(); transaction.Commit();
        }
        Assert.True(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        using var handler = new Posts(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, f.LegacyPermits(), handler, f.Template.Clock);
        var reads = f.Template.Keys.Requests.Count; await Denied(() => transport.SendAsync(f.Wire, default));
        Assert.Equal(0, handler.Calls); Assert.Equal(reads, f.Template.Keys.Requests.Count);
        Assert.Equal("CredentialRuntimeAlreadyBound", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code); f.AssertActivationReleased();
    }

    static async Task Denied(Func<Task> action)
    { var error = await Assert.ThrowsAsync<PipelineContractException>(action); Assert.Equal(DeniedReason, error.ReasonCode); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); }
    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    static string[] Rows(string path, string sql)
    { using var db = Open(path); using var query = db.CreateCommand(); query.CommandText = sql; using var reader = query.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray())); return rows.ToArray(); }

    sealed class HeldKeys(ICredentialKeyProvider inner, bool ignoreCancellation = false) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (ignoreCancellation) await Release.Task; else await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }

    sealed class Posts(bool wait = false, bool fail = false, HeldBody? body = null) : HttpMessageHandler
    {
        int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls); Entered.TrySetResult(); if (wait) await Release.Task;
            if (fail) throw new HttpRequestException(CredentialFixture.Token);
            return new(HttpStatusCode.OK) { Content = body is null ? new StringContent("{\"workflow_run_id\":42}") : new StreamContent(body) };
        }
    }

    sealed class HeldBody(bool waitForRead = false, bool waitForDisposal = false, bool failRead = false) : Stream
    {
        readonly byte[] content = Encoding.UTF8.GetBytes("{\"workflow_run_id\":42}"); int position; int asyncDisposed;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position == 0) { ReadEntered.TrySetResult(); if (waitForRead) await ReleaseRead.Task; }
            if (failRead) throw new IOException(CredentialFixture.Token);
            var count = Math.Min(buffer.Length, content.Length - position); content.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref asyncDisposed, 1) == 0) { DisposeEntered.TrySetResult(); if (waitForDisposal) await ReleaseDispose.Task; } Dispose(); GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class BoundFixture : IDisposable
    {
        public CredentialFixture Template { get; } = new();
        public string Customer => Template.Scope.CustomerId;
        public string CellId => Template.Scope.CellId;
        public string RuntimePath => Path("runtime.sqlite");
        public string AuthorityPath => Path("runtime-authority");
        public FoundationStore Store { get; }
        public CredentialVault Vault => Template.Vault;
        public CredentialVaultOptions VaultOptions => Template.Options;
        public CredentialVaultNamespaceIdentity Namespace { get; }
        public FoundationCellActivationAuthority Authority { get; }
        public FoundationCellActivationAuthorityIdentity Identity { get; }
        public FoundationCellActivationBinding Activation { get; }
        public FoundationCredentialRuntimeBinding? Binding { get; private set; }
        public FoundationCredentialBoundDispatch Dispatch { get; private set; } = null!;
        public PipelineWireRequest Wire { get; private set; } = null!;
        public BoundFixture(bool bind = true, bool prepareJob = true)
        {
            // Only this fresh target store is selected/bound. Template supplies disposable vault/key and wire contracts,
            // not its pre-created job. No pending target work or worker registration exists at owner cutover.
            Store = new(RuntimePath, CellId, Customer, Template.Clock); Store.BootstrapAdministrator("admin");
            foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator"), ("auditor", "Auditor") })
                Store.SetMembership(Customer, "admin", actor, "default", "dev", [role]);
            Store.PutConnection(Customer, "admin", Template.Connection.Id, Template.Connection.Provider, Template.Dispatch.Connection.ConfigurationJson);
            Namespace = Vault.DescribeNamespace(); Authority = FoundationCellActivationAuthority.Provision(AuthorityPath, Customer, CellId, Template.Clock);
            Activation = Authority.Activate(RuntimePath, Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null); Identity = Authority.DescribeIdentity();
            if (bind) Bind(); if (prepareJob) PrepareJob();
        }
        public void Bind() => Binding = FoundationCredentialBoundTransport.BindCurrent(Store, Vault, Authority, Namespace, Identity, Store.GetCellInstanceIdentity().DatabaseInstanceId);
        public FoundationJob PrepareJob(string state = "Dispatched")
        {
            var request = Template.Request with { RequestId = Guid.NewGuid().ToString("D") };
            var plan = Store.CreatePlan(Customer, "requester", new("default", "dev", Template.Connection.Id, "pipeline.dispatch", JsonSerializer.Serialize(request,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, Template.Clock.Now.AddMinutes(20), request.SourceRevision));
            Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest); var job = Store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, Guid.NewGuid().ToString("N"));
            if (state != "Queued") job = Store.ClaimNext(Customer, "bound-worker", TimeSpan.FromMinutes(2))!;
            if (state == "Dispatched") job = Store.StartDispatch(Customer, job.Id, "bound-worker", job.Fence).Job;
            Dispatch = MakeDispatchPins(job.Id, job.WorkerId ?? "bound-worker", Math.Max(1, job.Fence), plan.Digest);
            Wire = PipelineRequestFactory.Create(Template.Connection, request, new(Customer, CellId, Template.Connection.Id, Template.Connection.Generation,
                PipelineRequestFactory.ConnectionDigest(Template.Connection), PipelineRequestFactory.RequestDigest(Template.Connection, request), plan.Digest, plan.ExpiresAt), Template.Clock.Now);
            return job;
        }
        public FoundationCredentialBoundDispatch MakeDispatchPins(string job, string worker, long fence, string plan) => new(job, worker, fence, Template.Scope, plan, Template.Request.SourceRevision, "");
        public Task Initialize() => Template.StoreToken();
        public FoundationCredentialBoundTransport Transport(HttpMessageHandler handler, CredentialVault? vault = null, FoundationCellActivationAuthority? authority = null,
            CredentialVaultNamespaceIdentity? expectedNamespace = null, FoundationCellActivationAuthorityIdentity? expectedAuthority = null, FoundationStore? store = null,
            string? instance = null, string? bindingSha = null, FoundationCredentialBoundDispatch? dispatch = null) => FoundationCredentialBoundTransport.ForMockHandler(
                store ?? Store, vault ?? Vault, authority ?? Authority, expectedNamespace ?? Namespace, expectedAuthority ?? Identity,
                instance ?? Store.GetCellInstanceIdentity().DatabaseInstanceId, bindingSha ?? Binding?.Sha256 ?? new string('0', 64), dispatch ?? Dispatch,
                new([], Template.Clock), "1.0.0", handler, Template.Clock, TimeSpan.FromSeconds(30));
        public FoundationCredentialDispatchPermitSource LegacyPermits() => new(Store, Dispatch.JobId, Dispatch.WorkerId, Dispatch.Fence, Dispatch.Scope,
            Dispatch.PlanDigest, Dispatch.SourceRevision, Dispatch.CatalogDigest, new([], Template.Clock), "1.0.0");
        public (string Path, string Instance) SwitchTarget()
        { var path = Path("switch.sqlite"); var store = new FoundationStore(path, CellId, Customer, Template.Clock); return (path, store.GetCellInstanceIdentity().DatabaseInstanceId); }
        public void AssertBusy()
        { var target = SwitchTarget(); Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => Authority.Activate(target.Path, target.Instance, Activation.Generation, Activation.Sha256)).Code); }
        public void AssertActivationReleased()
        {
            // A new shared read lease would not detect a leaked operation lease. Take the actual
            // exclusive guard without changing its bytes/history, then verify the current binding.
            using (var exclusive = new FileStream(System.IO.Path.Combine(AuthorityPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(0, exclusive.Length);
            Assert.Equal(Activation.Generation, Authority.ReadCurrent()!.Generation);
        }
        public string Path(string name) => System.IO.Path.Combine(Template.Root, name);
        public long Number(string sql) { using var db = Open(RuntimePath); using var query = db.CreateCommand(); query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar()); }
        public void Sql(string sql) { using var db = Open(RuntimePath); using var query = db.CreateCommand(); query.CommandText = sql; query.ExecuteNonQuery(); }
        public string[] Rows(string sql) => FoundationCredentialBoundTransportBoundaryTests.Rows(RuntimePath, sql);
        public void Dispose() => Template.Dispose();
    }
}
