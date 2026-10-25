using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialScopedRecoveryReviewExecutionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task All_scope_review_admits_before_key_then_records_only_held_evidence_without_replay(bool multiple)
    {
        using var f = new ReviewFixture(multiple); await f.Initialize(); var before = f.StageAndKeys(); f.ClearKeys(); var review = await f.Prepare();
        Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        review = f.Store.ApproveCredentialScopedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        Assert.Empty(f.DestinationKeys.Requests); long admittedBeforeLookup = 0; var guardHeld = false; var transactionAvailable = false;
        f.DestinationKeys.OnGet = () =>
        {
            admittedBeforeLookup = f.Admissions;
            try { using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { guardHeld = true; }
            using var db = f.Open(); using var tx = db.BeginTransaction(deferred: false); transactionAvailable = true; tx.Commit();
        };
        var completed = await f.Execute(review); Assert.Equal("Succeeded", completed.Execution!.State); Assert.Equal("HeldStageVerified", completed.Execution.Result!.Status);
        Assert.True(completed.Execution.Result.InactiveRecoveryHold); Assert.False(completed.Execution.Result.PromotionAuthorized); Assert.False(completed.Execution.Result.LiveAdoptionAuthorized);
        Assert.False(completed.Execution.Result.ActivationPerformed); Assert.False(completed.Execution.Result.ExternalExecutionAuthorized);
        Assert.Equal(1, admittedBeforeLookup); Assert.True(guardHeld); Assert.True(transactionAvailable); Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(multiple ? 2 : 1, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Preparation_checks_current_capability_before_opening_private_stage_and_never_reads_keys()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead); f.ClearKeys();
        using (var held = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<FoundationStoreException>(() => f.Coordinator().PrepareAsync(readers, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic rejected preparation", f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_requests")); Assert.Empty(f.DestinationKeys.Requests);
        var approved = await f.Approved(); Assert.Equal("Approved", approved.State); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor-role")] [InlineData("executor-version")]
    [InlineData("context-expiry")] [InlineData("policy")] [InlineData("lifecycle")] [InlineData("instance")]
    public async Task Current_authority_drift_during_key_await_keeps_admission_consumed_unknown(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify,
            lifetime: change == "context-expiry" ? TimeSpan.FromMinutes(1) : null); var paused = new PausedKeys(f.Keys); var coordinator = f.Coordinator(paused); var before = f.StageAndKeys();
        var executing = coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id));
            if (change == "requester") f.Grant(ReviewFixture.Requester, ["Operator"], false);
            else if (change == "approver") f.Grant(ReviewFixture.Approver, ["Approver"], false);
            else if (change == "executor-role") f.Grant(ReviewFixture.Executor, ["Operator"], false);
            else if (change == "executor-version") f.Grant(ReviewFixture.Executor, ["Operator"]);
            else if (change == "context-expiry") f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(2);
            else if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
            else if (change == "lifecycle") f.Sql("UPDATE customer_lifecycle SET state='Offboarded',version=version+1 WHERE singleton=1;UPDATE cell SET enabled=0 WHERE singleton=1;");
            else f.Sql("UPDATE cell SET database_instance_id='" + Guid.NewGuid().ToString("N") + "' WHERE singleton=1");
            paused.Release.TrySetResult(); AssertUnknown(await executing);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256)); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Caller_context_list_replacement_during_await_cannot_extend_original_executor_expiry()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var supplied = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromMinutes(1))).ToList();
        var paused = new PausedKeys(f.Keys); var executing = f.Coordinator(paused).ExecuteAsync(supplied, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(2); supplied.Clear(); supplied.AddRange(await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)); paused.Release.TrySetResult(); AssertUnknown(await executing); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Completion_clock_crossing_only_executor_context_expiry_cannot_publish_success()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromSeconds(40));
        var clock = new CompletionBoundaryClock(f.Cell.Clock.Now, contexts.Min(context => context.ExpiresUtc).AddMilliseconds(1));
        var store = new FoundationStore(f.Database, f.CellId, f.Customer, clock);
        var coordinator = new FoundationCredentialScopedRecoveryStageReviewCoordinator(store, new(f.Keys, f.Cell.Clock), f.Instance);
        var before = f.StageAndKeys(); f.ClearKeys(); f.DestinationKeys.OnGet = clock.Arm;
        AssertUnknown(await coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.True(clock.ObservedCutoff); Assert.Single(f.DestinationKeys.Requests); Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id));
        Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("stage")]
    public async Task Change_after_AES_verification_is_rechecked_before_durable_success(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator(); var before = f.StageAndKeys();
        Fault(coordinator, phase =>
        {
            if (phase != "AfterVerification") return;
            if (change == "requester") f.Grant(ReviewFixture.Requester, ["Operator"], false);
            else if (change == "approver") f.Grant(ReviewFixture.Approver, ["Approver"], false);
            else f.PrivateFile(Path.Combine(f.StageRoot, "unexpected.json"), "{}"u8.ToArray());
        });
        AssertUnknown(await f.Execute(review, coordinator)); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(1, f.Admissions);
        if (change != "stage") Assert.Equal(before, f.StageAndKeys());
        else Assert.True(File.Exists(Path.Combine(f.StageRoot, "unexpected.json"))); // No repair or deletion of owner interference.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Completion_audit_failure_cannot_publish_success_or_release_guard_before_unknown_recording(bool failUnknownAudit)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndKeys();
        f.Sql("CREATE TRIGGER reject_review_completed BEFORE INSERT ON audit WHEN NEW.action='CredentialScopedRecoveryReviewCompleted' BEGIN SELECT RAISE(ABORT,'synthetic completion rejection'); END;");
        if (failUnknownAudit) f.Sql("CREATE TRIGGER reject_review_unknown BEFORE INSERT ON audit WHEN NEW.action='CredentialScopedRecoveryReviewUnknown' BEGIN SELECT RAISE(ABORT,'synthetic unknown rejection'); END;");
        var executing = f.Execute(review);
        if (failUnknownAudit) { var error = await Assert.ThrowsAsync<CredentialException>(() => executing); Assert.Equal("credential-scoped-recovery-review-outcome-unknown", error.Code); }
        else AssertUnknown(await executing);
        Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        using var released = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("digest")] [InlineData("plan-pin")] [InlineData("receipt-pin")] [InlineData("stage")]
    public async Task Rejected_before_admission_never_reads_keys_or_creates_execution(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); f.ClearKeys();
        if (change == "stage") f.PrivateFile(Path.Combine(f.StageRoot, "unexpected.json"), "{}"u8.ToArray()); var before = f.StageAndKeys();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, change == "digest" ? new string('f', 64) : review.Digest,
            f.Plan, change == "plan-pin" ? new string('e', 64) : f.Plan.Sha256, change == "receipt-pin" ? new string('d', 64) : f.Receipt.Sha256));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_before_admission_has_no_claim_while_after_admission_is_consumed_unknown()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); f.ClearKeys();
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, canceled.Token)); }
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); var before = f.StageAndKeys();
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.Keys);
        var executing = f.Coordinator(paused).ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); AssertUnknown(await executing); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(before, f.StageAndKeys()); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Cooperative_operation_deadline_leaves_one_consumed_unknown_and_releases_held_guard()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Keys); var before = f.StageAndKeys();
        // Leave native metadata/admission preflight a realistic budget before testing cancellation at the paused provider.
        var executing = f.Execute(review, f.Coordinator(paused, TimeSpan.FromSeconds(3)));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(paused.Release.Task.IsCompleted); // Only the operation deadline can release the cooperative await.
            var unknown = await executing.WaitAsync(TimeSpan.FromSeconds(15)); AssertUnknown(unknown);
            Assert.Equal("StageVerificationInterrupted", unknown.Execution!.Reason);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(before, f.StageAndKeys()); using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_key_provider_is_retained_with_guard_until_actual_return_then_cancellation_is_unknown()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var before = f.StageAndKeys();
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.Keys, cooperative: false);
        var executing = f.Coordinator(paused).ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); await Task.Delay(50); Assert.False(executing.IsCompleted);
            Assert.Throws<IOException>(() => new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            using (var db = f.Open()) using (var tx = db.BeginTransaction(deferred: false)) tx.Commit();
            Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); paused.Release.TrySetResult(); AssertUnknown(await executing);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.StageAndKeys()); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Concurrent_execution_claims_one_admission_and_resolves_only_one_destination_key()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Keys); f.ClearKeys(); var first = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests);
            paused.Release.TrySetResult(); Assert.Equal("Succeeded", (await first).Execution!.State);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Single(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")]
    public async Task Interrupted_execution_returns_unknown_without_stage_writes_or_automatic_replay(string phase)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndKeys(); f.ClearKeys(); var coordinator = f.Coordinator();
        Fault(coordinator, checkpoint => { if (checkpoint == phase) throw new IOException("synthetic interrupted private path " + f.StageRoot); });
        var unknown = await f.Execute(review, coordinator); AssertUnknown(unknown); Assert.DoesNotContain(f.StageRoot, JsonSerializer.Serialize(unknown)); Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(unknown));
        Assert.Equal(phase == "AfterAdmission" ? 0 : 1, f.DestinationKeys.Requests.Count); Assert.Equal(before, f.StageAndKeys());
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Instance_pin_refuses_foreign_composition_without_new_execution_or_key_access()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var before = f.StageAndKeys(); f.ClearKeys();
        Assert.Throws<CredentialException>(() => new FoundationCredentialScopedRecoveryStageReviewCoordinator(f.Store, new(f.Keys, f.Cell.Clock), Guid.NewGuid().ToString("N")));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [WindowsCredentialFact]
    public async Task Absence_only_stage_has_real_complete_scope_authority_and_can_record_authenticated_held_evidence()
    {
        using var f = new ReviewFixture(); await f.Initialize(includePresent: false, absenceOnly: true); f.ClearKeys();
        var review = await f.Approved(); Assert.Equal(0, review.Target.ProfileCount); Assert.Equal(2, review.Target.AbsenceProofCount);
        Assert.Equal(new[] { "default/dev", "review-other/qa" }, review.Target.Scopes.Select(scope => scope.ProjectId + "/" + scope.EnvironmentId));
        Assert.Empty(f.DestinationKeys.Requests); var before = f.StageAndKeys(); var result = await f.Execute(review);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, f.StageAndKeys()); Assert.Equal(2, f.Count("CredentialScopedRecoveryReviewCompleted"));
        var json = JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] { f.Cell.Root, CredentialFixture.Token, f.Cell.Scope.AuthProfileId, "\"tag\":", "\"scope\":" }) Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsCredentialFact]
    public async Task Empty_stage_remains_inspectable_but_has_no_review_authority_or_key_access()
    {
        using var f = new ReviewFixture(false); await f.Initialize(includePresent: false); f.ClearKeys(); var before = f.StageAndKeys();
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare());
        Assert.Equal("ScopedRecoveryStageReviewEmptySource", error.Code); Assert.Empty(f.DestinationKeys.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_requests")); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor")]
    public async Task Removing_only_the_absence_scope_grant_during_key_wait_prevents_success(string actor)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Keys);
        var operation = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var key = actor == "requester" ? ReviewFixture.Requester : actor == "approver" ? ReviewFixture.Approver : ReviewFixture.Executor;
            // Model storage-level stale membership while retaining the original global actor version and positive-scope grant.
            f.Sql("UPDATE memberships SET enabled=0 WHERE actor_key='" + key + "' AND project_id='review-other'");
            paused.Release.TrySetResult(); AssertUnknown(await operation);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialScopedRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Distinct_human_approval_and_complete_history_read_are_required_for_absence_scope_union()
    {
        using var f = new ReviewFixture(); await f.Initialize(); f.Grant(ReviewFixture.Requester, ["Operator", "Approver"]);
        var review = await f.Prepare(); f.ClearKeys();
        var same = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, ReviewFixture.RequesterSubject);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialScopedRecoveryStageReview(same, review.Id, review.Digest)).Code);
        var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialScopedRecoveryStageReview(readers.Take(1).ToArray(), review.Id));
        Assert.Equal(review.Id, f.Store.GetCredentialScopedRecoveryStageReview(readers, review.Id).Id); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Historical_completed_stage_can_receive_fresh_review_after_source_and_plan_expiry_without_renewal()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var before = f.StageAndKeys(); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2); f.ClearKeys();
        var review = await f.Approved(); var result = await f.Execute(review);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.Empty(f.Cell.Keys.Requests); Assert.Single(f.DestinationKeys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("extra")] [InlineData("actor")] [InlineData("version")]
    [InlineData("customer")] [InlineData("cell")] [InlineData("workload")] [InlineData("support")] [InlineData("capability")] [InlineData("expired")]
    public async Task Preparation_requires_one_current_human_with_exact_present_and_absence_scope_set(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); f.ClearKeys(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToList();
        Corrupt(contexts, attack, f.Cell.Clock.Now); var before = f.StageAndKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().PrepareAsync(contexts, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic rejected complete scope", f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_requests")); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData(false, "missing")] [InlineData(false, "duplicate")] [InlineData(false, "actor")] [InlineData(false, "workload")]
    [InlineData(true, "missing")] [InlineData(true, "duplicate")] [InlineData(true, "actor")] [InlineData(true, "workload")]
    public async Task Approval_and_execution_cannot_drop_absence_scope_or_mix_actor_authority(bool execute, string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = execute ? await f.Approved() : await f.Prepare(); f.ClearKeys(); var before = f.StageAndKeys();
        var contexts = (await f.Contexts(execute ? FoundationCapabilities.CredentialRecoveryVerify : FoundationCapabilities.CredentialRecoveryApprove)).ToList(); Corrupt(contexts, attack, f.Cell.Clock.Now);
        if (execute) await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        else Assert.ThrowsAny<Exception>(() => f.Store.ApproveCredentialScopedRecoveryStageReview(contexts, review.Id, review.Digest));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("Prepared")] [InlineData("Approved")] [InlineData("OutcomeUnknown")] [InlineData("Succeeded")]
    public async Task Recovery_preserves_scoped_history_but_quarantines_unfinished_authority_and_rotates_instance(string state)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = state == "Prepared" ? await f.Prepare() : await f.Approved();
        if (state == "OutcomeUnknown") { var coordinator = f.Coordinator(); Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("synthetic review stop"); }); review = await f.Execute(review, coordinator); }
        if (state == "Succeeded") review = await f.Execute(review);
        var backup = f.Store.CreateBackup(f.Child("review-snapshot.sqlite")); var restored = FoundationStore.RestoreBackup(backup, f.Child("review-restored.sqlite"), f.Cell.Clock);
        var read = restored.GetCredentialScopedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead), review.Id);
        Assert.Equal(state == "Succeeded" ? "Approved" : "Quarantined", read.State); Assert.Equal(review.Execution, read.Execution);
        Assert.NotEqual(f.Instance, restored.GetCellInstanceIdentity().DatabaseInstanceId); f.ClearKeys();
        var stale = new FoundationCredentialScopedRecoveryStageReviewCoordinator(restored, new(f.Keys, f.Cell.Clock), restored.GetCellInstanceIdentity().DatabaseInstanceId);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => stale.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
    }

    static void Corrupt(List<CustomerContext> contexts, string attack, DateTimeOffset now)
    {
        var c = contexts[1];
        if (attack == "missing") { contexts.RemoveAt(1); return; }
        if (attack == "duplicate") { contexts[1] = contexts[0]; return; }
        contexts[1] = (CustomerContext)typeof(CustomerContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            attack == "customer" ? "foreign" : c.CustomerId, attack == "cell" ? "foreign" : c.CellId,
            attack == "actor" ? (c.ActorKey == ReviewFixture.Requester ? ReviewFixture.Executor : ReviewFixture.Requester) : c.ActorKey, attack == "extra" ? "unreviewed" : c.ProjectId, c.EnvironmentId,
            attack == "capability" ? FoundationCapabilities.CredentialRecoveryRead : c.Capability, c.RoleVersion + (attack == "version" ? 1 : 0),
            attack == "workload" ? PrincipalKind.Workload : c.Kind, attack == "expired" ? now : c.ExpiresUtc,
            null, attack == "support" ? "Synthetic support delegation" : null, null]);
    }

    static void Fault(FoundationCredentialScopedRecoveryStageReviewCoordinator coordinator, Action<string> callback) => typeof(FoundationCredentialScopedRecoveryStageReviewCoordinator)
        .GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, callback);
    static void AssertUnknown(FoundationCredentialScopedRecoveryStageReview result)
    { Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result); Assert.Null(result.Execution.ResultSha256); }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class CompletionBoundaryClock(DateTimeOffset initial, DateTimeOffset expired) : TimeProvider
    {
        bool armed; int samples;
        public bool ObservedCutoff { get; private set; }
        public void Arm() { armed = true; samples = 0; }
        public override DateTimeOffset GetUtcNow()
        {
            // After the sole destination await, completion checks contexts and standing authority,
            // then samples its publication instant. Cross only the shorter executor lifetime there.
            if (armed && ++samples >= 3) { ObservedCutoff = true; return expired; }
            return initial;
        }
    }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(cancellationToken); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? cancellationToken : CancellationToken.None); }
    }
    sealed class ReviewFixture : IDisposable
    {
        public const string RequesterSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        public const string Requester = "user:" + IdentityTestIssuer.Tenant + ":" + RequesterSubject;
        public const string Executor = MaintenanceExecutorFixture.Operator, Approver = MaintenanceExecutorFixture.Approver;
        public CredentialFixture Cell { get; } = new();
        public CredentialVault SourceVault { get; }
        readonly IdentityTestIssuer issuer = new();
        public RSA Signer { get; } = RSA.Create(2048);
        public FoundationStore Store { get; }
        public CredentialTestKeys DestinationKeys { get; } = new();
        public CredentialWindowsUserKeyProvider? NativeKeys { get; }
        public ICredentialKeyProvider Keys => NativeKeys is null ? DestinationKeys : NativeKeys;
        public CredentialScopedRecoverySet Set { get; private set; } = null!;
        public CredentialScopedRecoveryStagePlan Plan { get; private set; } = null!;
        public CredentialScopedRecoveryStageReceipt Receipt { get; private set; } = null!;
        public string Customer => Cell.Scope.CustomerId;
        public string CellId => Cell.Scope.CellId;
        public string Database => Child("cell.db");
        public string StageRoot => Child("reviewed-held-stage");
        public string Guard => Path.Combine(StageRoot, ".credential-recovery-stage.guard");
        public string Instance { get; }
        public bool MultipleScopes { get; }
        public string Child(string name) => Path.Combine(Cell.Root, name);
        public ReviewFixture(bool multipleScopes = true, bool native = false)
        {
            MultipleScopes = multipleScopes; Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Store = new(Database, CellId, Customer, Cell.Clock);
            SourceVault = CredentialVault.ProvisionStrongScoped(Cell.Options with
            {
                RootPath = Child("scoped-review-source"),
                RollbackProtection = new(Child("scoped-review-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 }
            }, Cell.Keys, Cell.Clock);
            if (multipleScopes)
            {
                var onboarding = Store.PrepareScopeOnboarding(Customer, "admin", "review-other", "qa", "other-admin", "Synthetic review scope onboarding");
                Store.ApproveAdministrativePlan(Customer, "approver", onboarding.Id, onboarding.Digest); Store.ExecuteScopeOnboarding(Customer, "admin", onboarding.Id, onboarding.Digest);
            }
            Grant(Requester, ["Operator"]); Grant(Executor, ["Operator"]); Grant(Approver, ["Approver"]); Grant(MaintenanceExecutorFixture.Auditor, ["Auditor"]);
            DestinationKeys.Values.Clear(); DestinationKeys.Values["review-destination"] = RandomNumberGenerator.GetBytes(32);
            if (native) NativeKeys = Cell.ProvisionNativeKeys("review-destination-keys");
            Instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
        }
        public void Grant(string actor, string[] roles, bool enabled = true)
        {
            Store.SetMembership(Customer, "admin", actor, "default", "dev", roles, enabled);
            if (MultipleScopes) Store.SetMembership(Customer, "other-admin", actor, "review-other", "qa", roles, enabled);
        }
        public async Task Initialize(bool includePresent = true, bool absenceOnly = false)
        {
            if (NativeKeys is not null) using (await NativeKeys.ProvisionKeyAsync(Customer, CellId, "review-destination")) { }
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            if (includePresent) await SourceVault.PutAsync(Cell.Scope, secret, Cell.Clock.Now.AddHours(1));
            else if (absenceOnly) await Abandon(Cell.Scope);
            if (MultipleScopes) await Abandon(Cell.Scope with { ProjectId = "review-other", EnvironmentId = "qa", AuthProfileId = "profile/review-other" });
            Set = await SourceVault.CaptureScopedSignedRecoverySetAsync(SourceVault.DescribeNamespace(), Signer, "review-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
            var authority = CredentialScopedRecoverySets.Authority(Set); var selected = CredentialScopedRecoverySets.Verify(Set, authority, Signer, SourceVault.DescribeNamespace(), Cell.Clock);
            var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Customer, CellId, "review-destination", Keys);
            Plan = CredentialScopedRecoveryStaging.PreparePlan(selected, authority, SourceVault.DescribeNamespace(), StageRoot, key, "Synthetic durable held-stage review", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
            Receipt = await new CredentialScopedRecoveryStageExecutor(Cell.Keys, Keys, Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, Signer);
        }
        public async Task Abandon(CredentialScope scope)
        {
            var property = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
            property.SetValue(SourceVault, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic scoped pending interruption"); }));
            try { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Assert.ThrowsAsync<IOException>(() => SourceVault.PutAsync(scope, secret, Cell.Clock.Now.AddHours(1))); }
            finally { property.SetValue(SourceVault, null); }
            var pending = await SourceVault.InspectRollbackStateAsync(scope.AuthProfileId);
            await SourceVault.ReconcilePendingWriteAsync(scope.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        }
        public async Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? subject = null, TimeSpan? lifetime = null)
        {
            var role = capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator";
            subject ??= capability == FoundationCapabilities.CredentialRecoveryPrepare ? RequesterSubject : role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, CellId); var claims = issuer.UserClaims(role, subject);
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims)); var resolver = new CustomerContextResolver(options, new Memberships(Store), Cell.Clock);
            var contexts = new List<CustomerContext> { resolver.Require(principal, Customer, "default", "dev", capability) };
            if (MultipleScopes) contexts.Add(resolver.Require(principal, Customer, "review-other", "qa", capability)); return contexts;
        }
        public FoundationCredentialScopedRecoveryStageReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null, TimeSpan? timeout = null) =>
            new(Store, new CredentialScopedRecoveryStageVerifier(keys ?? Keys, Cell.Clock), Instance, timeout);
        public async Task<FoundationCredentialScopedRecoveryStageReview> Prepare() => await Coordinator().PrepareAsync(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Plan.Sha256, Receipt.Sha256, "Synthetic exact stage review", Cell.Clock.Now.AddMinutes(5));
        public async Task<FoundationCredentialScopedRecoveryStageReview> Approved() { var review = await Prepare(); return Store.ApproveCredentialScopedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest); }
        public async Task<FoundationCredentialScopedRecoveryStageReview> Execute(FoundationCredentialScopedRecoveryStageReview review, FoundationCredentialScopedRecoveryStageReviewCoordinator? coordinator = null) =>
            await (coordinator ?? Coordinator()).ExecuteAsync(await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest, Plan, Plan.Sha256, Receipt.Sha256);
        public async Task<FoundationCredentialScopedRecoveryStageReview> Read(string id) => Store.GetCredentialScopedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        public void ClearKeys() { Cell.Keys.Requests.Clear(); DestinationKeys.Requests.Clear(); }
        public long Count(string action) => Number("SELECT count(*) FROM audit WHERE action='" + action + "'");
        public long Admissions => Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_executions");
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string ExecutionState(string id) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT state FROM credential_scoped_recovery_stage_review_executions WHERE request_id=$id"; command.Parameters.AddWithValue("$id", id); return (string)command.ExecuteScalar()!; }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); return db; }
        public string[] StageAndKeys() => new[] { StageRoot, Child("scoped-review-source"), Child("scoped-review-authority"), NativeKeys is null ? null : Child("review-destination-keys") }.Where(p => p is not null)
            .SelectMany(p => Directory.EnumerateFileSystemEntries(p!, "*", SearchOption.AllDirectories).Prepend(p!)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Hash((Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm()) + ":" +
                (Directory.Exists(path) ? "directory" : new FileInfo(path).Length == 0 ? Hash([]) : Hash(File.ReadAllBytes(path)))).ToArray();
        public void PrivateFile(string path, byte[] bytes)
        {
            using var identity = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(identity.User!); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security); file.Write(bytes); file.Flush(true);
        }
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); issuer.Dispose(); Cell.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
