using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundAdmissionCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Actual_run_admits_SQL_only_registration_enqueue_claim_and_mock_intent_without_keys_or_handlers(string provider)
    {
        using var f = await BoundTransportFixture.Create(provider); FinishExisting(f); var spy = new FactorySpy();
        using var consumer = Consumer(f, spy); using var run = consumer.OpenRun(f.Store); var plan = Plan(f); f.Cell.Keys.Requests.Clear();
        var worker = run.RegisterWorker(f.Cell.Scope.CustomerId, "core-admission-worker", Environment.ProcessId, DateTimeOffset.UtcNow, "MockLoopback");
        var queued = run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "core-admission-once");
        Assert.Equal(queued.Id, run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "core-admission-once").Id);
        var claim = run.ClaimNext(f.Cell.Scope.CustomerId, worker.WorkerId, TimeSpan.FromMinutes(2))!;
        Assert.Equal(queued.Id, claim.Id); Assert.Equal(1, claim.Fence);
        var dispatch = run.StartMockDispatch(f.Cell.Scope.CustomerId, claim.Id, worker.WorkerId, claim.Fence, f.Verifier, BoundTransportFixture.Runtime);
        Assert.Equal("Dispatched", dispatch.Job.State); Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
        run.Dispose();
        // Known outcomes and monitoring retain their existing authority; they are not fresh execution admission.
        Assert.Equal("Succeeded", f.Store.CompleteJob(f.Cell.Scope.CustomerId, claim.Id, worker.WorkerId, claim.Fence,
            "{\"syntheticAdmissionOnly\":true}").State);
        f.Store.HeartbeatWorker(f.Cell.Scope.CustomerId, worker); f.Store.StopWorker(f.Cell.Scope.CustomerId, worker);
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM jobs WHERE id=$id", ("$id", queued.Id)));
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM outbox WHERE job_id=$id", ("$id", queued.Id)));
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM worker_heartbeats WHERE worker_id=$id AND state='Stopped'", ("$id", worker.WorkerId)));
        Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("authProfileId")]
    public async Task Enqueue_uses_actual_stored_configuration_instead_of_an_earlier_eligible_target(string field)
    {
        using var f = await BoundTransportFixture.Create(); FinishExisting(f); var spy = new FactorySpy();
        using var consumer = Consumer(f, spy); using var run = consumer.OpenRun(f.Store); var plan = Plan(f);
        run.RequireConnection(f.Cell.Connection, "default", "dev");
        MutateConfiguration(f, field, field == "organization" ? "different-org" : "profile/different");
        var before = Snapshot(f.DatabasePath); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "changed-stored-target"));
        Assert.Equal("BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(403, error.Status);
        Assert.Equal(before, Snapshot(f.DatabasePath)); Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Unsupported_selected_oldest_job_rolls_back_prior_uncommitted_expiry_housekeeping()
    {
        using var f = await BoundTransportFixture.Create(); var spy = new FactorySpy(); using var consumer = Consumer(f, spy);
        using var run = consumer.OpenRun(f.Store); var plan = Plan(f);
        _ = run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "oldest-unsupported");
        Sql(f.DatabasePath, "UPDATE jobs SET lease_until=0 WHERE id=$id", ("$id", f.Dispatch.Job.Id));
        MutateConfiguration(f, "organization", "different-org"); var before = Snapshot(f.DatabasePath); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => run.ClaimNext(f.Cell.Scope.CustomerId, "scope-mismatch-worker", TimeSpan.FromMinutes(1)));
        Assert.Equal("BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(before, Snapshot(f.DatabasePath));
        Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Dedicated_cell_run_can_record_expired_unknown_history_without_granting_an_unrelated_fresh_scope()
    {
        using var f = await BoundTransportFixture.Create(); var spy = new FactorySpy();
        using var consumer = Consumer(f, spy, scopes: [f.Cell.Scope with { ProjectId = "different", EnvironmentId = "qa" }]);
        using var run = consumer.OpenRun(f.Store); Sql(f.DatabasePath, "UPDATE jobs SET lease_until=0 WHERE id=$id", ("$id", f.Dispatch.Job.Id));
        f.Cell.Keys.Requests.Clear();
        Assert.Null(run.ClaimNext(f.Cell.Scope.CustomerId, "cell-history-worker", TimeSpan.FromMinutes(1)));
        Assert.Equal("OutcomeUnknown", f.Store.GetJob(f.Cell.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM audit WHERE action='JobLeaseExpired' AND target_id=$id", ("$id", f.Dispatch.Job.Id)));
        Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Actual_target_changed_after_claim_is_refused_before_dispatch_or_blocked_audit_mutation()
    {
        using var f = await BoundTransportFixture.Create(); FinishExisting(f); var spy = new FactorySpy();
        using var consumer = Consumer(f, spy); using var run = consumer.OpenRun(f.Store); var plan = Plan(f);
        _ = run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "claimed-before-change");
        var claim = run.ClaimNext(f.Cell.Scope.CustomerId, "claimed-worker", TimeSpan.FromMinutes(1))!;
        MutateConfiguration(f, "organization", "different-org"); var before = Snapshot(f.DatabasePath); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => run.StartMockDispatch(f.Cell.Scope.CustomerId, claim.Id,
            "claimed-worker", claim.Fence, f.Verifier, BoundTransportFixture.Runtime));
        Assert.Equal("BoundPipelineConnectionNotAllowed", error.Code); Assert.Equal(before, Snapshot(f.DatabasePath));
        Assert.Equal("Claimed", f.Store.GetJob(f.Cell.Scope.CustomerId, "operator", claim.Id).State);
        Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("oversize")]
    public async Task Shared_parser_is_bounded_strict_and_the_public_permit_keeps_its_opaque_credential_error(string fault)
    {
        using var f = await BoundTransportFixture.Create(); var node = JsonNode.Parse(f.Dispatch.Connection.ConfigurationJson)!;
        var json = fault switch
        {
            "provider" => Alter(node, "provider", PipelineRequestFactory.AzureDevOps),
            "unknown" => Alter(node, "unexpectedPrivateValue", CredentialFixture.Token),
            "duplicate" => "{\"provider\":\"github-actions\",\"provider\":\"github-actions\"}",
            "oversize" => new string('x', 65537), _ => "{"
        };
        var invalid = f.Dispatch.Connection with { ConfigurationJson = json }; f.Cell.Keys.Requests.Clear();
        var parsed = Assert.Throws<FoundationStoreException>(() => FoundationPipelineConnection.FromStored(invalid, f.Cell.Scope.CellId));
        Assert.Equal("StoredPipelineConnectionInvalid", parsed.Code);
        var error = Assert.Throws<CredentialException>(() => CredentialDispatchPermit.FromDurableDispatch(f.Dispatch with { Connection = invalid },
            f.Wire, f.Cell.Scope, f.Cell.Clock.Now));
        Assert.Equal("credential-dispatch-authority-invalid", error.Code);
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Owner_disposal_only_after_independent_reader_observes_committed_enqueue_reports_uncertain_not_no_effect()
    {
        using var f = await BoundTransportFixture.Create(); FinishExisting(f); var spy = new FactorySpy();
        using var actualHostLease = f.Authority.AcquireLease(f.DatabasePath, f.Instance);
        var armed = false; var observedCommit = false; FoundationCredentialBoundPipelineConsumer? consumer = null;
        const string key = "postcommit-known-job";
        // This existing callback retains actual authority. Its independent SQL read only times a synthetic postcommit owner disposal.
        var store = new FoundationStore(f.DatabasePath, f.Cell.Scope.CellId, f.Cell.Scope.CustomerId, f.Cell.Clock, initializeSchema: false,
            activationGuard: (path, instance) =>
            {
                actualHostLease.AssertCurrent(path, instance);
                if (armed && Number(f.DatabasePath, "SELECT count(*) FROM jobs WHERE idempotency_key=$key AND state='Queued'", ("$key", key)) == 1)
                { observedCommit = true; consumer!.Dispose(); }
            });
        consumer = Consumer(f, spy); using var owned = consumer; using var run = consumer.OpenRun(store);
        var plan = Plan(f, store); f.Cell.Keys.Requests.Clear(); armed = true;
        var error = Assert.Throws<FoundationStoreException>(() => run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, key));
        Assert.True(observedCommit); Assert.Equal("BoundConsumerAdmissionUncertain", error.Code); Assert.Equal(503, error.Status);
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM jobs WHERE idempotency_key=$key", ("$key", key)));
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM outbox o JOIN jobs j ON j.id=o.job_id AND j.customer_id=o.customer_id WHERE j.idempotency_key=$key", ("$key", key)));
        Assert.Equal(1, Number(f.DatabasePath, "SELECT count(*) FROM audit a JOIN jobs j ON j.id=a.target_id AND j.customer_id=a.customer_id WHERE a.action='JobQueued' AND j.idempotency_key=$key", ("$key", key)));
        Assert.Equal(0, spy.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData(false, "MockLoopback")]
    [InlineData(false, "ClaimHold")]
    [InlineData(true, "MockLoopback")]
    [InlineData(true, "ClaimHold")]
    public async Task Restored_execution_hold_denies_fresh_execution_registration_but_keeps_disabled_monitoring(bool bound, string mode)
    {
        using var f = await BoundTransportFixture.Create(bind: bound);
        var snapshot = f.Store.CreateBackup(Path.Combine(f.Cell.Root, "registration-backup.sqlite"));
        var target = Path.Combine(f.Cell.Root, "registration-restored.sqlite"); var restored = FoundationStore.RestoreBackup(snapshot, target, f.Cell.Clock);
        Assert.True(Number(target, "SELECT count(*) FROM recovery_execution_holds") > 0);
        var before = Snapshot(target); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<FoundationStoreException>(() => restored.RegisterWorker(f.Cell.Scope.CustomerId,
            "held-execution-worker", Environment.ProcessId, DateTimeOffset.UtcNow, mode));
        Assert.Equal("RecoveryExecutionHeld", error.Code); Assert.Equal(403, error.Status); Assert.Equal(before, Snapshot(target));
        var monitor = restored.RegisterWorker(f.Cell.Scope.CustomerId, "disabled-monitor", Environment.ProcessId, DateTimeOffset.UtcNow, "Disabled");
        restored.HeartbeatWorker(f.Cell.Scope.CustomerId, monitor); restored.StopWorker(f.Cell.Scope.CustomerId, monitor);
        Assert.Equal(1, Number(target, "SELECT count(*) FROM worker_heartbeats WHERE worker_id='disabled-monitor' AND state='Stopped'"));
        Assert.Empty(f.Cell.Keys.Requests);
    }

    static FoundationCredentialBoundPipelineConsumer Consumer(BoundTransportFixture f, FactorySpy spy, IReadOnlyList<CredentialScope>? scopes = null) =>
        FoundationCredentialBoundPipelineConsumer.ForMockHandlers(f.Vault, f.Authority, f.Namespace, f.AuthorityIdentity, f.Instance,
            f.Binding!.Sha256, scopes ?? [f.Cell.Scope], spy.Create, f.Cell.Clock);
    static FoundationPlan Plan(BoundTransportFixture f, FoundationStore? store = null)
    {
        store ??= f.Store; var request = f.Cell.Request with { RequestId = Guid.NewGuid().ToString("D") };
        var plan = store.CreatePlan(f.Cell.Scope.CustomerId, "requester", new("default", "dev", f.Cell.Connection.Id, "pipeline.dispatch",
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, f.Cell.Clock.Now.AddMinutes(10), request.SourceRevision));
        return store.ApprovePlan(f.Cell.Scope.CustomerId, "approver", plan.Id, plan.Digest);
    }
    static void FinishExisting(BoundTransportFixture f) => f.Store.FailJob(f.Cell.Scope.CustomerId, f.Dispatch.Job.Id,
        f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence, true, "SyntheticAdmissionFixture");
    static string Alter(JsonNode node, string field, string value) { node[field] = value; return node.ToJsonString(); }
    static void MutateConfiguration(BoundTransportFixture f, string field, string value) => Sql(f.DatabasePath,
        "UPDATE connections SET configuration=$configuration WHERE id=$id", ("$configuration", Alter(JsonNode.Parse(f.Dispatch.Connection.ConfigurationJson)!, field, value)), ("$id", f.Cell.Connection.Id));
    static string Snapshot(string path)
    {
        using var db = Open(path); var rows = new List<string>();
        foreach (var table in new[] { "jobs", "outbox", "worker_heartbeats", "audit" })
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid"; using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        }
        return string.Join("\n", rows);
    }
    static long Number(string path, string sql, params (string Key, object Value)[] values)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Key, value.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }
    static void Sql(string path, string sql, params (string Key, object Value)[] values)
    { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Key, value.Value); command.ExecuteNonQuery(); }
    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    sealed class FactorySpy
    {
        public int Calls { get; private set; }
        public HttpMessageHandler Create() { Calls++; throw new InvalidOperationException("SQL admission must not create an HTTP handler."); }
    }
}
