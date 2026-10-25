using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundTransactionalAdmissionBoundaryTests
{
    [Theory]
    [InlineData("enqueue")]
    [InlineData("claim")]
    [InlineData("start")]
    [InlineData("start-mock")]
    [InlineData("register-mock")]
    [InlineData("register-claim")]
    public async Task Binding_that_commits_first_invalidates_another_stores_old_unbound_preflight_before_any_legacy_admission(string operation)
    {
        using var f = new AdmissionCell(); var plan = f.Plan();
        Assert.False(FoundationStore.ExistingCredentialRuntimeBindingRequired(f.DatabasePath, f.Customer, f.Cell));
        var originalAudit = f.Number("SELECT count(*) FROM audit");
        using var gate = new WriterGate();
        var binding = f.Clock.Paused(gate, f.Bind);
        Task<object?>? oldAdmission = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            f.AssertWriterOwned(); f.AssertActivationBusy();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oldAdmission = Dedicated(() => { entered.TrySetResult(); return f.Legacy(operation, plan); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(oldAdmission.IsCompleted); Assert.Empty(f.Effects());
        }
        finally { gate.Release.Set(); }
        _ = await binding.WaitAsync(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<FoundationStoreException>(async () => _ = await oldAdmission!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("BoundPipelineConsumerRequired", error.Code); Assert.Equal(403, error.Status);
        Assert.Empty(f.Effects()); Assert.Equal(originalAudit + 1, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(1, f.Number("SELECT count(*) FROM credential_runtime_bindings"));
        Assert.True(f.Other.GetCredentialRuntimeBindingStatus().BindingRequired); f.AssertNoCredentialUse(); f.AssertReleased();
    }

    [Theory]
    [InlineData("enqueue")]
    [InlineData("claim")]
    [InlineData("register-mock")]
    [InlineData("register-claim")]
    public async Task An_old_admission_that_owns_the_writer_first_prevents_a_false_quiescent_binding(string operation)
    {
        using var f = new AdmissionCell(); var plan = f.Plan();
        if (operation == "claim") _ = f.Other.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "old-queued");
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        using var gate = new WriterGate();
        var oldAdmission = f.Clock.Paused(gate, () => f.Legacy(operation, plan));
        Task<FoundationCredentialRuntimeBinding>? binding = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); f.AssertWriterOwned();
            binding = Dedicated(f.Bind);
            await f.WaitForActivationLease(); // The queued binder really owns the actual lease while SQLite is busy.
            f.AssertActivationBusy(); Assert.False(binding.IsCompleted);
        }
        finally { gate.Release.Set(); }
        var admitted = await oldAdmission.WaitAsync(TimeSpan.FromSeconds(30)); Assert.NotNull(admitted);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(async () => _ = await binding!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("CredentialRuntimeQuiescenceRequired", error.Code);
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRuntimeBound'"));
        if (operation == "enqueue") Assert.Equal(1, f.Number("SELECT count(*) FROM jobs WHERE state='Queued'"));
        else if (operation == "claim") Assert.Equal(1, f.Number("SELECT count(*) FROM jobs WHERE state='Claimed' AND fence=1 AND attempt=1"));
        else Assert.Equal(1, f.Number("SELECT count(*) FROM worker_heartbeats WHERE state='Running' AND generation=1"));
        f.AssertNoCredentialUse(); f.AssertReleased();
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("credential-reference")]
    [InlineData("provider")]
    [InlineData("malformed-json")]
    [InlineData("missing-contract-field")]
    [InlineData("disabled")]
    public void Typed_enqueue_reads_actual_stored_connection_after_an_earlier_valid_allowlist_probe(string mutation)
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer(); using var run = consumer.OpenRun(f.Store);
        run.RequireConnection(f.Template.Connection, "default", "dev");
        var plan = f.Plan();
        // This models stored metadata corruption after a valid plan and target preflight, without changing generation.
        // The transaction must read and constrain the actual connection before any idempotent return or queue mutation.
        f.MutateStoredConnection(mutation); var before = f.AllEffects();
        var error = Assert.Throws<FoundationStoreException>(() => run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "changed-target"));
        Assert.Equal(mutation is "provider" or "malformed-json" or "missing-contract-field" ? "StoredPipelineConnectionInvalid" : "BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(403, error.Status);
        Assert.Equal(before, f.AllEffects()); f.AssertNoCredentialUse(); f.AssertActivationBusy();
    }

    [Fact]
    public void Normalized_allowlist_order_keeps_a_real_stored_connection_eligible_without_creating_credentials_or_handlers()
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer(); using var run = consumer.OpenRun(f.Store);
        var config = JsonNode.Parse(f.Configuration)!.AsObject();
        config["allowedInputKeys"] = JsonSerializer.SerializeToNode(f.Template.Connection.AllowedInputKeys.Reverse().ToArray());
        f.UpdateConfiguration(config.ToJsonString()); var plan = f.Plan();
        var job = run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "normalized");
        Assert.Equal("Queued", job.State); Assert.Single(f.Store.PendingOutbox(f.Customer)); f.AssertNoCredentialUse();
    }

    [Theory]
    [InlineData("enqueue")]
    [InlineData("claim")]
    [InlineData("start")]
    public async Task A_real_run_waiting_for_a_connection_writer_revalidates_the_committed_generation_inside_admission(string phase)
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer(); using var run = consumer.OpenRun(f.Store);
        var plan = f.Plan(); FoundationJob? job = null;
        if (phase != "enqueue") job = run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "prior-job");
        if (phase == "start") job = run.ClaimNext(f.Customer, "typed-worker", TimeSpan.FromMinutes(2));
        run.RequireConnection(f.Template.Connection, "default", "dev");
        var effects = f.Effects(); var audits = f.Number("SELECT count(*) FROM audit");
        using var gate = new WriterGate();
        var update = f.Clock.Paused(gate, () => f.Other.PutConnection(f.Customer, "admin", f.Template.Connection.Id,
            f.Template.Connection.Provider, f.Configuration));
        Task<object?>? admission = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); f.AssertWriterOwned(); f.AssertActivationBusy();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            admission = Dedicated<object?>(() =>
            {
                entered.TrySetResult();
                return phase switch
                {
                    "enqueue" => run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "waited-enqueue"),
                    "claim" => run.ClaimNext(f.Customer, "typed-worker", TimeSpan.FromMinutes(2)),
                    _ => run.StartMockDispatch(f.Customer, job!.Id, "typed-worker", job.Fence, f.Verifier, "1.0.0")
                };
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.False(admission.IsCompleted);
        }
        finally { gate.Release.Set(); }
        Assert.Equal(2, (await update.WaitAsync(TimeSpan.FromSeconds(30))).Generation);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(async () => _ = await admission!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(effects, f.Effects());
        Assert.Equal(audits + 1, f.Number("SELECT count(*) FROM audit")); f.AssertNoCredentialUse();
    }

    [Fact]
    public async Task Disposing_the_real_consumer_after_initial_SQL_admission_rolls_back_its_uncommitted_queue_and_audit()
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer(); using var run = consumer.OpenRun(f.Store);
        var plan = f.Plan(); var before = f.AllEffects(); using var gate = new WriterGate();
        var enqueue = f.Clock.Paused(gate, () => run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "final-cutoff"));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); f.AssertWriterOwned(); f.AssertActivationBusy(); consumer.Dispose();
        }
        finally { gate.Release.Set(); }
        var error = await Assert.ThrowsAsync<FoundationStoreException>(async () => _ = await enqueue.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("BoundPipelineConsumerDisposed", error.Code); Assert.Equal(before, f.AllEffects()); f.AssertNoCredentialUse();
    }

    [Theory]
    [InlineData("disposed-run")]
    [InlineData("disposed-consumer")]
    [InlineData("other-store")]
    public void Only_the_actual_current_owned_run_can_enter_the_internal_transactional_path(string mismatch)
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer(); using var run = consumer.OpenRun(f.Store);
        var plan = f.Plan(); var before = f.AllEffects();
        if (mismatch == "disposed-run") run.Dispose();
        if (mismatch == "disposed-consumer") consumer.Dispose();
        var target = mismatch == "other-store" ? f.Other : f.Store;
        // Negative-only reflection supplies an actual foreign run to the internal store boundary.
        // No constructor or DTO is used to forge a positive proof.
        var method = typeof(FoundationStore).GetMethod("EnqueuePlanForBoundConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<FoundationStoreException>(() => Invoke(method, target,
            [run, f.Customer, "operator", plan.Id, plan.Digest, "invalid-run"]));
        Assert.Equal(mismatch == "other-store" ? "BoundPipelineRunMismatch" : mismatch == "disposed-run" ? "BoundPipelineRunDisposed" : "BoundPipelineConsumerDisposed", error.Code);
        Assert.Equal(before, f.AllEffects()); f.AssertNoCredentialUse();
    }

    [Fact]
    public void A_copied_same_instance_database_cannot_gain_a_run_from_the_original_actual_activation_or_path_binding()
    {
        using var f = new AdmissionCell(); f.Bind(); using var consumer = f.Consumer();
        var backup = f.Store.CreateBackup(f.Path("copy.sqlite"));
        var copied = new FoundationStore(backup.DatabasePath, f.Cell, f.Customer, f.Clock, initializeSchema: false);
        Assert.Equal(f.Store.GetCellInstanceIdentity().DatabaseInstanceId, copied.GetCellInstanceIdentity().DatabaseInstanceId);
        var before = f.AllEffects(); Assert.Equal("ActivationBindingDenied", Assert.Throws<FoundationStoreException>(() => consumer.OpenRun(copied)).Code);
        Assert.Equal(before, f.AllEffects()); f.AssertNoCredentialUse(); f.AssertReleased();
    }

    [Theory]
    [InlineData("complete", "Succeeded")]
    [InlineData("fail", "OutcomeUnknown")]
    [InlineData("reconcile", "ReconciledNoEffect")]
    [InlineData("cancel", "OutcomeUnknown")]
    public void Existing_owned_worker_heartbeat_stop_and_safe_outcomes_do_not_require_new_execution_admission(string outcome, string expected)
    {
        using var f = new AdmissionCell(); f.Bind(); FoundationJob dispatched; FoundationWorkerHandle worker;
        using (var consumer = f.Consumer())
        using (var run = consumer.OpenRun(f.Store))
        {
            worker = run.RegisterWorker(f.Customer, "owned-worker", Environment.ProcessId, f.Clock.GetUtcNow(), "MockLoopback");
            var plan = f.Plan(); _ = run.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "outcome-job");
            var claimed = run.ClaimNext(f.Customer, "owned-worker", TimeSpan.FromMinutes(2))!;
            dispatched = run.StartMockDispatch(f.Customer, claimed.Id, "owned-worker", claimed.Fence, f.Verifier, "1.0.0").Job;
        }
        f.Other.HeartbeatWorker(f.Customer, worker);
        if (outcome == "complete") _ = f.Other.CompleteJob(f.Customer, dispatched.Id, "owned-worker", dispatched.Fence, "{\"mock\":true,\"deploymentVerified\":false}");
        else if (outcome == "cancel") _ = f.Other.CancelJob(f.Customer, "operator", dispatched.Id);
        else
        {
            _ = f.Other.FailJob(f.Customer, dispatched.Id, "owned-worker", dispatched.Fence, true, "SyntheticInterrupted");
            if (outcome == "reconcile") _ = f.Other.ReconcileJob(f.Customer, "operator", dispatched.Id, "FailedNoEffect", "{\"syntheticEvidence\":true}");
        }
        f.Other.StopWorker(f.Customer, worker);
        Assert.Equal(expected, f.Store.GetJob(f.Customer, "operator", dispatched.Id).State);
        Assert.Equal(1, f.Number("SELECT count(*) FROM worker_heartbeats WHERE state='Stopped'"));
        var eventId = f.Store.PendingOutbox(f.Customer).Single().EventId;
        Assert.True(f.Store.ConsumeOutbox(f.Customer, eventId)); Assert.False(f.Store.ConsumeOutbox(f.Customer, eventId));
        f.AssertNoCredentialUse(); f.AssertReleased();
    }

    [Fact]
    public void Disabled_worker_metadata_registration_remains_available_after_binding_without_execution_authority()
    {
        using var f = new AdmissionCell(); f.Bind();
        var worker = f.Other.RegisterWorker(f.Customer, "disabled", Environment.ProcessId, f.Clock.GetUtcNow(), "Disabled");
        f.Other.HeartbeatWorker(f.Customer, worker); f.Other.StopWorker(f.Customer, worker);
        Assert.Equal(1, f.Number("SELECT count(*) FROM worker_heartbeats WHERE mode='Disabled' AND state='Stopped'"));
        Assert.Empty(f.Rows("SELECT * FROM jobs")); f.AssertNoCredentialUse(); f.AssertReleased();
    }

    [Fact]
    public void Restored_execution_hold_still_denies_public_admission_and_cannot_be_replaced_with_historical_binding()
    {
        using var f = new AdmissionCell(); f.Bind(); var plan = f.Plan();
        var backup = f.Store.CreateBackup(f.Path("restore-source.sqlite")); var restoredPath = f.Path("restored.sqlite");
        var restored = FoundationStore.RestoreBackup(backup, restoredPath, f.Clock);
        var instance = restored.GetCellInstanceIdentity().DatabaseInstanceId;
        var head = f.Authority.ReadCurrent()!; _ = f.Authority.Activate(restoredPath, instance, head.Generation, head.Sha256);
        var before = f.Rows("SELECT * FROM audit ORDER BY sequence", restoredPath);
        foreach (var operation in new Action[]
        {
            () => restored.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "restored"),
            () => restored.ClaimNext(f.Customer, "old-worker", TimeSpan.FromMinutes(1)),
            () => restored.StartMockDispatch(f.Customer, "missing-job", "old-worker", 1, f.Verifier, "1.0.0"),
            () => restored.RegisterWorker(f.Customer, "old-worker", Environment.ProcessId, f.Clock.GetUtcNow(), "MockLoopback")
        }) Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(operation).Code);
        Assert.Equal(before, f.Rows("SELECT * FROM audit ORDER BY sequence", restoredPath));
        Assert.Equal(0, Scalar(restoredPath, "SELECT count(*) FROM jobs"));
        using var consumer = f.Consumer(instance: instance);
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => consumer.OpenRun(restored)).Code);
        Assert.True(restored.GetCredentialRuntimeBindingStatus().BindingRequired); Assert.Null(restored.GetCredentialRuntimeBindingStatus().CurrentBinding);
        f.AssertNoCredentialUse(); f.AssertReleased();
    }

    static object? Invoke(MethodInfo method, object target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    static Task<T> Dedicated<T>(Func<T> action) => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    static SqliteConnection Open(string path, int timeout = 30)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = timeout }.ToString());
        db.Open(); return db;
    }
    static long Scalar(string path, string sql) { using var db = Open(path); using var query = db.CreateCommand(); query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar()); }

    sealed class WriterGate : IDisposable
    {
        internal int Used;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
    sealed class AdmissionClock(CredentialClock source) : TimeProvider
    {
        readonly AsyncLocal<WriterGate?> gate = new();
        public override DateTimeOffset GetUtcNow()
        {
            var current = gate.Value;
            if (current is not null && Interlocked.Exchange(ref current.Used, 1) == 0)
            {
                current.Entered.TrySetResult();
                if (!current.Release.Wait(TimeSpan.FromSeconds(40))) throw new TimeoutException("Synthetic writer boundary was not released.");
            }
            return source.GetUtcNow();
        }
        internal Task<T> Paused<T>(WriterGate pause, Func<T> action) => Dedicated(() =>
        { gate.Value = pause; try { return action(); } finally { gate.Value = null; } });
    }

    sealed class AdmissionCell : IDisposable
    {
        public CredentialFixture Template { get; } = new();
        public AdmissionClock Clock { get; }
        public FoundationStore Store { get; }
        public FoundationStore Other { get; }
        public FoundationCellActivationAuthority Authority { get; }
        public FoundationCellActivationAuthorityIdentity AuthorityIdentity { get; }
        public CredentialVaultNamespaceIdentity Namespace { get; }
        public EntitlementVerifier Verifier { get; }
        public string DatabasePath => Path("admission.sqlite");
        public string Customer => Template.Scope.CustomerId;
        public string Cell => Template.Scope.CellId;
        public string Configuration => Template.Dispatch.Connection.ConfigurationJson;
        readonly string instance, authorityPath;
        readonly string futurePath, futureInstance;
        int factories;
        public AdmissionCell()
        {
            Clock = new(Template.Clock); Store = new(DatabasePath, Cell, Customer, Clock);
            Store.BootstrapAdministrator("admin");
            foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
                Store.SetMembership(Customer, "admin", actor, "default", "dev", [role]);
            Store.PutConnection(Customer, "admin", Template.Connection.Id, Template.Connection.Provider, Configuration);
            Other = new(DatabasePath, Cell, Customer, Clock, initializeSchema: false);
            instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
            authorityPath = Path("activation"); Authority = FoundationCellActivationAuthority.Provision(authorityPath, Customer, Cell, Template.Clock);
            _ = Authority.Activate(DatabasePath, instance, 0, null); AuthorityIdentity = Authority.DescribeIdentity();
            Namespace = Template.Vault.DescribeNamespace(); Verifier = new([], Template.Clock);
            futurePath = Path("future.sqlite"); futureInstance = new FoundationStore(futurePath, Cell, Customer, Template.Clock).GetCellInstanceIdentity().DatabaseInstanceId;
            Template.Keys.Requests.Clear();
        }
        public string Path(string file) => System.IO.Path.Combine(Template.Root, file);
        public FoundationCredentialRuntimeBinding Bind() => FoundationCredentialBoundTransport.BindCurrent(Store, Template.Vault, Authority, Namespace, AuthorityIdentity, instance);
        public FoundationCredentialBoundPipelineConsumer Consumer(string? instance = null)
        {
            var binding = Store.GetCredentialRuntimeBindingStatus().CurrentBinding!;
            return FoundationCredentialBoundPipelineConsumer.ForMockHandlers(Template.Vault, Authority, Namespace, AuthorityIdentity,
                instance ?? this.instance, binding.Sha256, [Template.Scope], () => { Interlocked.Increment(ref factories); throw new InvalidOperationException("SQL-only fixture factory must not run."); }, Template.Clock);
        }
        public FoundationPlan Plan()
        {
            var request = Template.Request with { RequestId = Guid.NewGuid().ToString("D") };
            var plan = Store.CreatePlan(Customer, "requester", new("default", "dev", Template.Connection.Id, "pipeline.dispatch",
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, Template.Clock.Now.AddMinutes(20), request.SourceRevision));
            return Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest);
        }
        public object? Legacy(string operation, FoundationPlan plan) => operation switch
        {
            "enqueue" => Other.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, "old-preflight"),
            "claim" => Other.ClaimNext(Customer, "legacy-worker", TimeSpan.FromMinutes(2)),
            "start" => Other.StartDispatch(Customer, "missing-job", "legacy-worker", 1),
            "start-mock" => Other.StartMockDispatch(Customer, "missing-job", "legacy-worker", 1, Verifier, "1.0.0"),
            _ => Other.RegisterWorker(Customer, "legacy-worker", Environment.ProcessId, Template.Clock.Now, operation == "register-claim" ? "ClaimHold" : "MockLoopback")
        };
        public void MutateStoredConnection(string mutation)
        {
            if (mutation == "disabled") { Execute("UPDATE connections SET enabled=0"); return; }
            if (mutation == "malformed-json") { UpdateConfiguration("[]"); return; }
            var config = JsonNode.Parse(Configuration)!.AsObject();
            if (mutation == "repository") config["repository"] = "different-repository";
            if (mutation == "credential-reference") config["authProfileId"] = "profile/different";
            if (mutation == "provider") config["provider"] = PipelineRequestFactory.AzureDevOps;
            if (mutation == "missing-contract-field") config.Remove("allowedInputKeys");
            UpdateConfiguration(config.ToJsonString());
        }
        public void UpdateConfiguration(string json) { using var db = Open(DatabasePath); using var query = db.CreateCommand(); query.CommandText = "UPDATE connections SET configuration=$json"; query.Parameters.AddWithValue("$json", json); query.ExecuteNonQuery(); }
        void Execute(string sql) { using var db = Open(DatabasePath); using var query = db.CreateCommand(); query.CommandText = sql; query.ExecuteNonQuery(); }
        public long Number(string sql) => Scalar(DatabasePath, sql);
        public string[] Rows(string sql, string? path = null)
        {
            using var db = Open(path ?? DatabasePath); using var query = db.CreateCommand(); query.CommandText = sql;
            using var reader = query.ExecuteReader(); var rows = new List<string>();
            while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
            return rows.ToArray();
        }
        public string[] Effects() => new[] { "jobs", "outbox", "inbox", "worker_heartbeats" }.SelectMany(table => Rows("SELECT * FROM " + table + " ORDER BY 1,2").Select(row => table + ":" + row)).ToArray();
        public string[] AllEffects() => [.. Effects(), .. Rows("SELECT * FROM audit ORDER BY sequence")];
        public void AssertWriterOwned()
        {
            using var contender = Open(DatabasePath, timeout: 1);
            var error = Assert.Throws<SqliteException>(() => { using var unexpected = contender.BeginTransaction(deferred: false); });
            Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });
        }
        public void AssertActivationBusy()
        {
            var head = Authority.ReadCurrent()!;
            Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => Authority.Activate(futurePath, futureInstance, head.Generation, head.Sha256)).Code);
        }
        public async Task WaitForActivationLease()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { using var guard = new FileStream(System.IO.Path.Combine(authorityPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return; }
                await Task.Delay(20, stop.Token);
            }
        }
        public void AssertReleased() { using var guard = new FileStream(System.IO.Path.Combine(authorityPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        public void AssertNoCredentialUse() { Assert.Empty(Template.Keys.Requests); Assert.Equal(0, Volatile.Read(ref factories)); }
        public void Dispose() => Template.Dispose();
    }
}
