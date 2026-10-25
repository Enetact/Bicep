using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;
using JointFixture = SelfService.Foundation.Tests.FoundationCredentialScopedRecoveryJointCoordinatorTests.JointFixture;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialScopedRecoveryJointBoundaryTests
{
    [Theory]
    [InlineData("null-list")] [InlineData("empty")] [InlineData("null-item")] [InlineData("missing")]
    [InlineData("duplicate")] [InlineData("extra")] [InlineData("actor")] [InlineData("version")]
    [InlineData("customer")] [InlineData("cell")] [InlineData("workload")] [InlineData("support")]
    [InlineData("capability")] [InlineData("expired")]
    public async Task Invalid_complete_scope_contexts_never_reach_private_guards_keys_or_either_admission(string attack)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)).ToList();
        Corrupt(contexts, attack, f.Cell.Clock.Now); var before = f.ProtectedSnapshot(); f.ClearKeys();
        using (var checkpoint = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var stage = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, review, coordinator, attack == "null-list" ? null! : contexts));
        Assert.Equal(0, f.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.Null((await f.Read(review.Id)).Execution);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("source")]
    [InlineData("authority")] [InlineData("signer")] [InlineData("public-key")] [InlineData("commitment")] [InlineData("database-instance")]
    public async Task Constructor_pins_the_configured_customer_cell_namespace_authority_signer_and_database_instance(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var identity = f.Identity;
        var wrong = change switch
        {
            "customer" => identity with { CustomerId = Guid.NewGuid().ToString() },
            "cell" => identity with { CellId = Guid.NewGuid().ToString() },
            "source" => identity with { SourceNamespace = identity.SourceNamespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } },
            "authority" => identity with { AuthorityId = Guid.NewGuid().ToString("N") },
            "signer" => identity with { SignerKeyId = "other-signer" },
            "public-key" => identity with { PublicKeySha256 = new string('a', 64) },
            "commitment" => identity with { CommitmentSha256 = new string('b', 64) },
            _ => identity
        };
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        Assert.Throws<CredentialException>(() => new FoundationCredentialScopedRecoveryJointReviewCoordinator(f.Store, f.Checkpoints, wrong,
            new(f.Destination, f.Cell.Clock), change == "database-instance" ? Guid.NewGuid().ToString("N") : f.Store.GetCellInstanceIdentity().DatabaseInstanceId));
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal(0, f.JointAdmissions);
    }

    [Theory]
    [InlineData("prepared")] [InlineData("request-expiry")] [InlineData("requester")]
    [InlineData("executor")] [InlineData("approver")] [InlineData("policy")]
    public async Task Stale_or_missing_existing_approval_authority_is_denied_before_joint_admission(string change)
    {
        using var f = new JointFixture(); await f.Initialize();
        var review = change == "prepared" ? await f.Legacy().PrepareAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare),
            f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic not yet approved stage", f.Cell.Clock.Now.AddMinutes(5)) : await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var coordinator = f.Coordinator();
        if (change == "request-expiry") f.Cell.Clock.Now = review.ExpiresAt;
        if (change == "requester") f.Grant("dddddddd-dddd-dddd-dddd-dddddddddddd", "Operator", false);
        if (change == "executor") f.Grant(IdentityTestIssuer.Subject, "Operator", false);
        if (change == "approver") f.Grant(IdentityTestIssuer.OtherSubject, "Approver", false);
        if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, review, coordinator, contexts));
        Assert.Equal(0, f.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal(0, Count(f, "CredentialScopedRecoveryJointAdmitted"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Supplying_the_newest_head_cannot_retarget_an_approved_older_stage_even_if_approval_followed_the_advance(bool advanceBeforeApproval)
    {
        using var f = new JointFixture(); await f.Initialize();
        var review = await f.Legacy().PrepareAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare), f.Plan, f.Plan.Sha256,
            f.Receipt.Sha256, "Synthetic exact stage approval remains independent of checkpoint custody", f.Cell.Clock.Now.AddMinutes(5));
        using var next = await f.Capture(2); CredentialScopedRecoverySetCheckpoint latest;
        if (advanceBeforeApproval) latest = await f.Accept(next);
        else latest = f.Head;
        review = f.Store.ApproveCredentialScopedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        Assert.Equal("Approved", review.State); // No claim that v9 approval observed or approved checkpoint currency.
        if (!advanceBeforeApproval) latest = await f.Accept(next);
        var coordinator = f.Coordinator(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var before = f.ProtectedSnapshot(); f.ClearKeys();
        using (var stage = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<CredentialException>(() => coordinator.ExecuteApprovedAsync(contexts, review.Id, review.Digest,
                f.Plan, f.Plan.Sha256, f.Receipt.Sha256, latest.Sequence, latest.Sha256));
            Assert.Equal("credential-scoped-recovery-joint-review-checkpoint-selection-mismatch", error.Code);
        }
        Assert.Equal(0, f.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.Null((await f.Read(review.Id)).Execution);
    }

    [Theory]
    [InlineData("requester")] [InlineData("executor")] [InlineData("policy")] [InlineData("instance")] [InlineData("expiry")]
    public async Task Authority_drift_during_destination_wait_consumes_both_unknown_without_success_or_replay(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Destination);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var coordinator = f.Coordinator(paused); var before = f.ProtectedSnapshot();
        var operation = Execute(f, review, coordinator, contexts);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions);
            if (change == "requester") f.Grant("dddddddd-dddd-dddd-dddd-dddddddddddd", "Operator", false);
            if (change == "executor") f.Grant(IdentityTestIssuer.Subject, "Operator", false);
            if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
            if (change == "instance") f.Sql("UPDATE cell SET database_instance_id='" + Guid.NewGuid().ToString("N") + "' WHERE singleton=1");
            if (change == "expiry") f.Cell.Clock.Now = review.ExpiresAt;
            paused.Release.TrySetResult(); var result = await operation; AssertUnknown(result);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions); Assert.Single(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
        Assert.Equal(0, Count(f, "CredentialScopedRecoveryJointCompleted")); Assert.Equal(0, Count(f, "CredentialScopedRecoveryReviewCompleted"));
        Assert.Equal(2, f.Number("SELECT count(*) FROM (SELECT state FROM credential_scoped_recovery_joint_executions UNION ALL SELECT state FROM credential_scoped_recovery_stage_review_executions) WHERE state='OutcomeUnknown'"));
        Assert.Equal(before, f.ProtectedSnapshot()); AssertBothGuardsReleased(f);
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, review, coordinator, contexts)); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ordinary_and_joint_execution_share_one_consumption_boundary_in_either_winning_order(bool jointFirst)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        if (jointFirst) Assert.Equal("Succeeded", (await f.Execute(review)).State);
        else Assert.Equal("Succeeded", (await f.Legacy().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256)).Execution!.State);
        f.ClearKeys();
        if (jointFirst) await Assert.ThrowsAnyAsync<Exception>(() => f.Legacy().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        else await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review));
        Assert.Equal(1, f.Admissions); Assert.Equal(jointFirst ? 1 : 0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Concurrent_ordinary_and_joint_coordinators_have_one_actual_destination_verification_winner()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.ClearKeys();
        async Task<bool> Joint() { await start.Task; try { await Execute(f, review, f.Coordinator(), contexts); return true; } catch (Exception) { return false; } }
        async Task<bool> Ordinary() { await start.Task; try { await f.Legacy().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256); return true; } catch (Exception) { return false; } }
        var joint = Task.Run(Joint); var ordinary = Task.Run(Ordinary); start.TrySetResult(); var results = await Task.WhenAll(joint, ordinary);
        Assert.Single(results, result => result); Assert.Single(results, result => !result); Assert.Equal(1, f.Admissions); Assert.InRange(f.JointAdmissions, 0, 1);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal("Succeeded", (await f.Read(review.Id)).Execution!.State);
    }

    [Theory]
    [InlineData("CredentialScopedRecoveryReviewAdmitted")] [InlineData("CredentialScopedRecoveryJointAdmitted")]
    public async Task Either_admission_audit_failure_rolls_back_both_rows_and_all_admission_prefix_events(string action)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.ProtectedSnapshot();
        f.Sql("CREATE TRIGGER reject_joint_admission BEFORE INSERT ON audit WHEN NEW.action='" + action + "' BEGIN SELECT RAISE(ABORT,'synthetic admission audit refusal'); END;");
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review));
        Assert.Equal(0, f.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Equal(0, Count(f, "CredentialScopedRecoveryReviewAdmitted")); Assert.Equal(0, Count(f, "CredentialScopedRecoveryJointAdmitted"));
        Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.ProtectedSnapshot()); AssertBothGuardsReleased(f);
    }

    [Theory]
    [InlineData("CredentialScopedRecoveryReviewCompleted")] [InlineData("CredentialScopedRecoveryJointCompleted")]
    public async Task Either_completion_audit_failure_never_exposes_a_single_succeeded_side(string action)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.ProtectedSnapshot();
        f.Sql("CREATE TRIGGER reject_joint_completion BEFORE INSERT ON audit WHEN NEW.action='" + action + "' BEGIN SELECT RAISE(ABORT,'synthetic completion audit refusal'); END;");
        var result = await f.Execute(review); AssertUnknown(result); Assert.Equal("OutcomeUnknown", (await f.Read(review.Id)).Execution!.State);
        Assert.Equal(0, Count(f, "CredentialScopedRecoveryReviewCompleted")); Assert.Equal(0, Count(f, "CredentialScopedRecoveryJointCompleted"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_executions WHERE state='Succeeded'"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_joint_executions WHERE state='Succeeded'"));
        Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions); Assert.Equal(before, f.ProtectedSnapshot()); AssertBothGuardsReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Both_guards_remain_retained_after_verification_and_while_database_completion_waits_for_its_writer()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); using var next = await f.Capture(2);
        var paused = new PausedKeys(f.Destination); var coordinator = f.Coordinator(paused); var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Fault(coordinator, phase => { if (phase == "AfterVerification") verified.TrySetResult(); });
        var operation = f.Execute(review, coordinator); SqliteConnection? writer = null; SqliteTransaction? transaction = null;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); writer = f.Open(); transaction = writer.BeginTransaction(deferred: false);
            paused.Release.TrySetResult(); await verified.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(operation.IsCompleted); Assert.Throws<IOException>(() => new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Assert.Equal("CredentialScopedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            transaction.Commit(); transaction.Dispose(); transaction = null; writer.Dispose(); writer = null;
            Assert.Equal("Succeeded", (await operation.WaitAsync(TimeSpan.FromSeconds(15))).State);
        }
        finally { paused.Release.TrySetResult(); transaction?.Dispose(); writer?.Dispose(); }
        AssertBothGuardsReleased(f); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Restore_preserves_both_consumed_histories_and_only_unfinished_review_is_quarantined(bool succeeded, bool repeated)
    {
        using var f = new JointFixture(); await f.Initialize(absenceOnly: true); var review = await f.Approved(); var coordinator = f.Coordinator();
        if (!succeeded) Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("synthetic interrupted joint observation"); });
        var original = await f.Execute(review, coordinator); var ordinary = await f.Read(review.Id); var before = f.ProtectedSnapshot();
        var snapshotPath = Path.Combine(f.Cell.Root, "joint-backup.sqlite"); var restoredPath = Path.Combine(f.Cell.Root, "joint-restored.sqlite");
        var snapshot = f.Store.CreateBackup(snapshotPath); var source = File.ReadAllBytes(snapshotPath);
        var restored = FoundationStore.RestoreBackup(snapshot, restoredPath, f.Cell.Clock);
        if (repeated)
        {
            var second = restored.CreateBackup(Path.Combine(f.Cell.Root, "joint-second-backup.sqlite"));
            restored = FoundationStore.RestoreBackup(second, Path.Combine(f.Cell.Root, "joint-second-restored.sqlite"), f.Cell.Clock);
        }
        var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        var read = restored.GetCredentialScopedRecoveryJointVerification(readers, review.Id); var oldReview = restored.GetCredentialScopedRecoveryStageReview(readers, review.Id);
        Assert.Equal(original.State, read.State); Assert.Equal(original.Result, read.Result); Assert.Equal(original.ResultSha256, read.ResultSha256); Assert.Equal(original.Digest, read.Digest);
        Assert.Equal(JsonSerializer.Serialize(original.Target), JsonSerializer.Serialize(read.Target)); Assert.Equal(ordinary.Execution, oldReview.Execution);
        Assert.Equal(!succeeded, read.Quarantined); Assert.Equal(succeeded ? "Approved" : "Quarantined", oldReview.State);
        Assert.NotEqual(original.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId); f.ClearKeys();
        var stale = new FoundationCredentialScopedRecoveryJointReviewCoordinator(restored, f.Checkpoints, f.Identity, new(f.Destination, f.Cell.Clock), restored.GetCellInstanceIdentity().DatabaseInstanceId);
        var executors = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, review, stale, executors));
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal(source, File.ReadAllBytes(snapshotPath));
    }

    [WindowsCredentialFact]
    public async Task Restored_previously_approved_unconsumed_review_cannot_gain_joint_admission_even_at_the_same_current_checkpoint()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var before = f.ProtectedSnapshot();
        var snapshot = f.Store.CreateBackup(Path.Combine(f.Cell.Root, "unconsumed-backup.sqlite"));
        var restoredPath = Path.Combine(f.Cell.Root, "unconsumed-restored.sqlite"); var restored = FoundationStore.RestoreBackup(snapshot, restoredPath, f.Cell.Clock);
        var restoredReview = restored.GetCredentialScopedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead), review.Id);
        Assert.Equal("Quarantined", restoredReview.State); Assert.Null(restoredReview.Execution); Assert.Equal(f.Head, f.Checkpoints.ReadCurrent());
        Assert.NotEqual(review.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId); f.ClearKeys();
        var coordinator = new FoundationCredentialScopedRecoveryJointReviewCoordinator(restored, f.Checkpoints, f.Identity, new(f.Destination, f.Cell.Clock), restored.GetCellInstanceIdentity().DatabaseInstanceId);
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(f, review, coordinator, contexts));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredPath, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM credential_scoped_recovery_joint_executions"; Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar())); }
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot());
    }

    [WindowsCredentialFact]
    public async Task Valid_full_audit_snapshot_cannot_publish_partial_restored_joint_authority_when_recovery_audit_has_no_capacity()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("synthetic interrupted joint observation"); });
        AssertUnknown(await f.Execute(review, coordinator)); var before = f.ProtectedSnapshot();
        var snapshot = FoundationTestAuditCapacity.FillSnapshot(f.Store.CreateBackup(Path.Combine(f.Cell.Root, "joint-capacity-backup.sqlite")), f.Cell.Clock);
        using var signer = RSA.Create(2048); var signed = FoundationTestAuditCapacity.Sign(snapshot, signer, f.Cell.Clock);
        _ = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), signer, f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, f.Cell.Clock);
        var original = File.ReadAllBytes(snapshot.DatabasePath); var target = Path.Combine(f.Cell.Root, "must-not-exist.sqlite"); f.ClearKeys();
        Assert.Equal("AuditCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, target, f.Cell.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.False(File.Exists(target + "-wal")); Assert.False(File.Exists(target + "-shm"));
        Assert.Equal(original, File.ReadAllBytes(snapshot.DatabasePath)); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
    }

    static Task<FoundationCredentialScopedRecoveryJointVerification> Execute(JointFixture f, FoundationCredentialScopedRecoveryStageReview review,
        FoundationCredentialScopedRecoveryJointReviewCoordinator coordinator, IReadOnlyList<CustomerContext> contexts) =>
        coordinator.ExecuteApprovedAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256);
    static void AssertUnknown(FoundationCredentialScopedRecoveryJointVerification value)
    { Assert.Equal("OutcomeUnknown", value.State); Assert.Null(value.Result); Assert.Null(value.ResultSha256); Assert.Null(value.CompletedAt); }
    static long Count(JointFixture f, string action) => f.Number("SELECT count(*) FROM audit WHERE action='" + action + "'");
    static void Fault(FoundationCredentialScopedRecoveryJointReviewCoordinator coordinator, Action<string> callback) => typeof(FoundationCredentialScopedRecoveryJointReviewCoordinator)
        .GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, callback);
    static void AssertBothGuardsReleased(JointFixture f)
    {
        using (var checkpoint = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (var stage = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
    static void Corrupt(List<CustomerContext> contexts, string attack, DateTimeOffset now)
    {
        if (attack == "null-list") return;
        if (attack == "empty") { contexts.Clear(); return; }
        if (attack == "null-item") { contexts[1] = null!; return; }
        if (attack == "missing") { contexts.RemoveAt(1); return; }
        if (attack == "duplicate") { contexts[1] = contexts[0]; return; }
        var c = contexts[1]; contexts[1] = (CustomerContext)typeof(CustomerContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            attack == "customer" ? Guid.NewGuid().ToString() : c.CustomerId, attack == "cell" ? Guid.NewGuid().ToString() : c.CellId,
            attack == "actor" ? "user:" + IdentityTestIssuer.Tenant + ":dddddddd-dddd-dddd-dddd-dddddddddddd" : c.ActorKey,
            attack == "extra" ? "unapproved-project" : c.ProjectId, c.EnvironmentId, attack == "capability" ? FoundationCapabilities.CredentialRecoveryRead : c.Capability,
            c.RoleVersion + (attack == "version" ? 1 : 0), attack == "workload" ? PrincipalKind.Workload : c.Kind,
            attack == "expired" ? now : c.ExpiresUtc, null, attack == "support" ? "Synthetic support-only context" : null, null]);
    }
    sealed class PausedKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customer, cell, key, cancellationToken); }
    }
}
