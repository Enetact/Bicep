using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryJointCoordinatorTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "mixed")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "mixed")]
    [InlineData(PipelineRequestFactory.GitHub, "positive")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "positive")]
    [InlineData(PipelineRequestFactory.GitHub, "absence")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "absence")]
    public async Task Current_checkpoint_and_exact_human_approved_stage_publish_only_readonly_restricted_evidence(string provider, string mode)
    {
        using var f = new RestrictedRecoveryJointFixture(provider, mode); await f.Initialize(); var review = await f.Approved();
        f.Stage.RemoveSources(); f.ClearKeys(); var before = f.Snapshot(); var admitted = false;
        f.Destination.OnGet = () => { admitted = f.Admissions == 1 && f.JointAdmissions == 1; Held(f); using var db = f.Review.Open(); using var tx = db.BeginTransaction(deferred: false); tx.Commit(); };
        var result = await f.Execute(review); Assert.True(admitted); Assert.Equal("Succeeded", result.State); Assert.Equal("Succeeded", (await f.Review.Read(review.Id)).Execution!.State);
        Assert.Equal(f.Identity, result.Target.CheckpointAuthority); Assert.Equal(f.Head, result.Target.Checkpoint); Assert.Equal(f.Receipt.CompletionProofSha256, result.Result!.CompletionProofSha256);
        Assert.Equal("CurrentCheckpointHeldRestrictedStageVerified", result.Result.Status); Assert.True(result.Result.DestinationCompletionProofAuthenticated);
        Assert.True(result.Result.CheckpointReadLeaseRetainedDuringVerification); Assert.True(result.Result.InactiveRecoveryHold);
        Assert.False(result.Result.PromotionAuthorized); Assert.False(result.Result.LiveAdoptionAuthorized); Assert.False(result.Result.ActivationPerformed); Assert.False(result.Result.ExternalExecutionAuthorized);
        Assert.Equal("FoundationBoundDispatchOnly", result.AccessProfile); Assert.Equal("NotAssessed", result.Result.IndependentBackupCoverage);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
        var json = JsonSerializer.Serialize(result); foreach (var excluded in new[] { f.Cell.Root, CredentialFixture.Token, f.Stage.Positive.AuthProfileId, f.Stage.Absent.AuthProfileId, "\"Tag\"", "\"Ciphertext\"" }) Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Review.Execute(review));
        Assert.Empty(f.Destination.Requests); Assert.Equal(1, f.Admissions); Released(f);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor-role")] [InlineData("executor-version")]
    [InlineData("context-expiry")] [InlineData("policy")] [InlineData("lifecycle")] [InlineData("instance")]
    public async Task Current_authority_changes_during_key_await_leave_both_consumed_unknown(string change)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: change == "context-expiry" ? TimeSpan.FromMinutes(1) : null);
        var paused = new PausedKeys(f.Destination); var before = f.Snapshot(); var operation = f.Execute(review, f.Coordinator(paused), contexts);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Held(f); Assert.Equal(1, f.JointAdmissions);
            if (change == "requester") f.Review.Grant(RestrictedRecoveryReviewFixture.Requester, ["Operator"], enabled: false);
            else if (change == "approver") f.Review.Grant(RestrictedRecoveryReviewFixture.Approver, ["Approver"], enabled: false);
            else if (change == "executor-role") f.Review.Grant(RestrictedRecoveryReviewFixture.Executor, ["Operator"], enabled: false);
            else if (change == "executor-version") f.Review.Grant(RestrictedRecoveryReviewFixture.Executor, ["Operator"]);
            else if (change == "context-expiry") f.Clock.Now = f.Clock.Now.AddMinutes(2);
            else if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
            else if (change == "lifecycle") f.Sql("UPDATE customer_lifecycle SET state='Offboarded',version=version+1 WHERE singleton=1;UPDATE cell SET enabled=0 WHERE singleton=1");
            else f.Sql("UPDATE cell SET database_instance_id='" + Guid.NewGuid().ToString("N") + "' WHERE singleton=1");
            paused.Release.TrySetResult(); Unknown(await operation);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(before, f.Snapshot()); NoSuccess(f); Released(f); f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor")]
    public async Task Absence_scope_revocation_during_await_cannot_publish_success(string who)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Destination); var operation = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var actor = who == "requester" ? RestrictedRecoveryReviewFixture.Requester : who == "approver" ? RestrictedRecoveryReviewFixture.Approver : RestrictedRecoveryReviewFixture.Executor;
            f.Sql("UPDATE memberships SET enabled=0 WHERE actor_key='" + actor + "' AND project_id='other'"); paused.Release.TrySetResult(); Unknown(await operation);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        NoSuccess(f); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Actual_checkpoint_append_is_busy_through_key_wait_and_completion_fault_callbacks()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); using var next = await f.Capture(); var review = await f.Approved(); f.ClearKeys();
        var paused = new PausedKeys(f.Destination); var coordinator = f.Coordinator(paused); var callback = false;
        Fault(coordinator, phase => { if (phase == "AfterVerification") { Held(f); callback = true; Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", Assert.Throws<FoundationStoreException>(() => f.Accept(next).GetAwaiter().GetResult()).Code); } });
        var operation = f.Execute(review, coordinator);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code); Assert.Empty(f.Cell.Keys.Requests); paused.Release.TrySetResult(); Assert.Equal("Succeeded", (await operation).State); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.True(callback); Assert.Equal(2, (await f.Accept(next)).Sequence); Released(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Caller_cancellation_keeps_both_guards_until_uncooperative_dependency_really_returns(bool uncooperative)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.Snapshot();
        var paused = new PausedKeys(f.Destination, !uncooperative); using var cancel = new CancellationTokenSource(); var operation = f.Execute(review, f.Coordinator(paused), token: cancel.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancel.Cancel();
            if (uncooperative) { Assert.False(operation.IsCompleted); Held(f); using var db = f.Review.Open(); using var tx = db.BeginTransaction(deferred: false); tx.Commit(); }
            paused.Release.TrySetResult(); var result = await operation; Unknown(result); Assert.Equal("CallerCancelled", result.Reason);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        NoSuccess(f); Assert.Equal(before, f.Snapshot()); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Cooperative_deadline_keeps_one_consumed_dual_admission_and_releases_guards_after_cancellation()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Destination);
        var operation = f.Execute(review, f.Coordinator(paused, TimeSpan.FromSeconds(3)));
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(paused.Release.Task.IsCompleted); Unknown(await operation.WaitAsync(TimeSpan.FromSeconds(15))); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        NoSuccess(f); Released(f);
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")]
    public async Task Interruption_retains_both_guards_through_unknown_audit_and_cannot_replay(string phase)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); f.ClearKeys(); var coordinator = f.Coordinator(); var before = f.Snapshot();
        Fault(coordinator, observed => { if (observed == phase) { Held(f); throw new IOException(f.Cell.Root + CredentialFixture.Token); } });
        var result = await f.Execute(review, coordinator); Unknown(result); Assert.Equal(phase == "AfterAdmission" ? 0 : 1, f.Destination.Requests.Count);
        Assert.Equal(before, f.Snapshot()); Assert.DoesNotContain(f.Cell.Root, JsonSerializer.Serialize(result)); NoSuccess(f); Released(f);
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Review.Execute(review)); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Destination_key_absence_or_substitution_preserves_both_unknown_outcomes(bool wrong)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.Snapshot();
        if (wrong) f.Destination.Values["restricted-destination"] = new byte[32]; else f.Destination.Values.Remove("restricted-destination");
        Unknown(await f.Execute(review)); NoSuccess(f); Assert.Equal(before, f.Snapshot()); Released(f);
    }

    [Theory]
    [InlineData("verifier-mutates-checkpoint")] [InlineData("checkpoint-mutates-stage")]
    public async Task Concrete_dual_prekey_fence_rechecks_effects_of_both_clocks_before_first_destination_lookup(string change)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved();
        var clock = new EffectClock(f.Clock, () => File.AppendAllText(change == "verifier-mutates-checkpoint" ? f.CheckpointPath : Path.Combine(f.StageRoot, "completion.json"), change == "verifier-mutates-checkpoint" ? "!" : " "), skip: 1);
        var coordinator = change == "verifier-mutates-checkpoint" ? f.Coordinator(verifierClock: clock) : f.Coordinator(authority: f.Reopen(clock));
        Fault(coordinator, phase => { if (phase == "AfterAdmission") clock.Arm(); }); f.ClearKeys();
        Unknown(await f.Execute(review, coordinator)); Assert.True(clock.Triggered); Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); NoSuccess(f); Released(f);
    }

    [Theory]
    [InlineData("checkpoint")] [InlineData("stage")] [InlineData("cancel")] [InlineData("expiry")]
    public async Task Verifier_clock_effects_after_destination_lookup_cannot_publish_dual_success(string change)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); using var caller = new CancellationTokenSource();
        var clock = new EffectClock(f.Clock, () =>
        {
            if (change is "checkpoint" or "stage") File.AppendAllText(change == "checkpoint" ? f.CheckpointPath : Path.Combine(f.StageRoot, "completion.json"), change == "checkpoint" ? "!" : " ");
            else if (change == "cancel") caller.Cancel(); else f.Clock.Now = f.Clock.Now.AddHours(2);
        });
        f.Destination.OnGet = clock.Arm; Unknown(await f.Execute(review, f.Coordinator(verifierClock: clock), token: caller.Token));
        Assert.True(clock.Triggered); Assert.Single(f.Destination.Requests); NoSuccess(f); Released(f);
    }

    [Theory]
    [InlineData("checkpoint")] [InlineData("stage")]
    public async Task Actual_key_provider_mutation_is_detected_after_await_before_authenticated_result(string change)
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved();
        f.Destination.OnGet = () => File.AppendAllText(change == "checkpoint" ? f.CheckpointPath : Path.Combine(f.StageRoot, "completion.json"), change == "checkpoint" ? "!" : " ");
        Unknown(await f.Execute(review)); Assert.Single(f.Destination.Requests); NoSuccess(f); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Final_concrete_dual_snapshot_has_no_checkpoint_or_verifier_clock_after_store_authority_cutoff()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved();
        var cpClock = new RejectingClock(f.Clock); var stageClock = new RejectingClock(f.Clock); var storeClock = new EffectClock(f.Clock, () => { cpClock.Reject = true; stageClock.Reject = true; });
        var store = new FoundationStore(f.Review.Database, f.Review.CellId, f.Review.Customer, storeClock);
        var coordinator = f.Coordinator(store: store, verifierClock: stageClock, authority: f.Reopen(cpClock));
        Fault(coordinator, phase => { if (phase == "AfterVerification") storeClock.Arm(); });
        Assert.Equal("Succeeded", (await f.Execute(review, coordinator)).State); Assert.True(storeClock.Triggered); Assert.Equal(0, cpClock.RejectedReads); Assert.Equal(0, stageClock.RejectedReads); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Historical_completed_stage_and_current_checkpoint_can_receive_fresh_human_review_after_source_expiry()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); f.Stage.RemoveSources(); f.Clock.Now = f.Clock.Now.AddHours(2); f.ClearKeys(); var review = await f.Approved();
        Assert.Equal("Succeeded", (await f.Execute(review)).State); Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Released(f);
    }

    [WindowsCredentialFact]
    public async Task A_real_second_checkpoint_session_cannot_complete_the_first_authority_admission_for_the_same_stage()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved();
        var other = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(f.Review.Child("second-real-checkpoint"), f.Plan.SourceNamespace, "restricted-stage-signer", f.SignerSha, f.Clock);
        var otherHead = await other.AcceptNextAsync(f.Stage.Set, f.Stage.Set.Sha256, f.Stage.Signer, f.Cell.Keys, 0, null);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        var first = Open(f, f.Checkpoints, f.Head); using var firstLease = (IDisposable)first;
        var target = CallStore("CredentialRestrictedRecoveryJointTarget", null, f.Plan, Receipt(first), f.Identity, f.Head);
        var admission = CallStore("AdmitCredentialRestrictedRecoveryJointVerification", f.Store, contexts, review.Id, review.Digest, target);
        var second = Open(f, other, otherHead); using var secondLease = (IDisposable)second;
        var result = await Verify(second);
        var error = Assert.Throws<TargetInvocationException>(() => CallStore("CompleteCredentialRestrictedRecoveryJointVerification", f.Store, admission, contexts, target, result, second, CancellationToken.None));
        Assert.Equal("RestrictedRecoveryJointVerificationMismatch", Assert.IsType<FoundationStoreException>(error.InnerException).Code);
        Unknown((FoundationCredentialRestrictedRecoveryJointVerification)CallStore("RecordCredentialRestrictedRecoveryJointUnknown", f.Store, admission, "ReviewCompletionUnavailable")); NoSuccess(f);
    }

    [WindowsCredentialFact]
    public async Task Equal_but_separately_produced_stage_result_cannot_satisfy_the_actual_joint_session()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var held = Open(f, f.Checkpoints, f.Head); using var lease = (IDisposable)held;
        var first = await Verify(held); var other = await new CredentialRestrictedRecoveryStageVerifier(f.Destination, f.Clock).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        Assert.Equal(first.Sha256, other.Sha256);
        var method = held.GetType().GetMethod("AssertVerifiedCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(held, [other, f.Clock.Now, CancellationToken.None]));
        Assert.Equal("credential-restricted-recovery-stage-verified-session-mismatch", Assert.IsType<CredentialException>(error.InnerException).Code);
        method.Invoke(held, [first, f.Clock.Now, CancellationToken.None]); Assert.Equal(0, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Unknown_audit_failure_preserves_default_both_unknown_admissions_and_releases_custody()
    {
        using var f = new RestrictedRecoveryJointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        f.Sql("CREATE TRIGGER reject_joint_unknown BEFORE INSERT ON audit WHEN NEW.action='CredentialRestrictedRecoveryJointUnknown' BEGIN SELECT RAISE(ABORT,'synthetic refusal'); END;");
        Fault(coordinator, phase => { if (phase == "AfterAdmission") { Held(f); throw new IOException(f.Cell.Root); } });
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(review, coordinator)); Assert.Equal("credential-restricted-recovery-joint-review-outcome-unknown", error.Code); Assert.DoesNotContain(f.Cell.Root, error.ToString());
        Unknown(await f.Read(review.Id)); NoSuccess(f); Assert.Empty(f.Destination.Requests); Released(f);
    }

    static object CallStore(string name, object? instance, params object[] args) => typeof(FoundationStore).GetMethod(name, BindingFlags.NonPublic | (instance is null ? BindingFlags.Static : BindingFlags.Instance))!.Invoke(instance, args)!;
    static object Open(RestrictedRecoveryJointFixture f, CredentialRestrictedRecoverySetCheckpointAuthority authority, CredentialRestrictedRecoverySetCheckpoint head) =>
        typeof(FoundationStore).Assembly.GetType("SelfService.Foundation.CredentialRestrictedRecoveryJointSession")!.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [authority, authority.Identity, new CredentialRestrictedRecoveryStageVerifier(f.Destination, f.Clock), f.Plan, f.Plan.Sha256, f.Receipt.Sha256, head.Sequence, head.Sha256])!;
    static CredentialRestrictedRecoveryStageReceipt Receipt(object held) => (CredentialRestrictedRecoveryStageReceipt)held.GetType().GetProperty("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(held)!;
    static Task<CredentialRestrictedRecoveryStageVerification> Verify(object held) => (Task<CredentialRestrictedRecoveryStageVerification>)held.GetType().GetMethod("VerifyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(held, [CancellationToken.None])!;
    static void Fault(FoundationCredentialRestrictedRecoveryJointReviewCoordinator coordinator, Action<string> action) => typeof(FoundationCredentialRestrictedRecoveryJointReviewCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, action);
    static void Unknown(FoundationCredentialRestrictedRecoveryJointVerification result) { Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.Result); Assert.Null(result.ResultSha256); Assert.Null(result.CompletedAt); }
    static void NoSuccess(RestrictedRecoveryJointFixture f) { Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions); Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryJointCompleted")); }
    static void Held(RestrictedRecoveryJointFixture f)
    {
        foreach (var path in new[] { f.StageGuard, f.CheckpointGuard }) Assert.Throws<IOException>(() => { using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
    }
    static void Released(RestrictedRecoveryJointFixture f) { foreach (var path in new[] { f.StageGuard, f.CheckpointGuard }) { using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } }
    static async Task Observe(Task task) { try { await task; } catch { } }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class EffectClock(TimeProvider inner, Action effect, int skip = 0) : TimeProvider
    {
        bool armed; int remaining;
        internal bool Triggered { get; private set; }
        internal void Arm() { armed = true; remaining = skip; }
        public override DateTimeOffset GetUtcNow() { if (armed && remaining-- == 0) { armed = false; Triggered = true; effect(); } return inner.GetUtcNow(); }
    }
    sealed class RejectingClock(TimeProvider inner) : TimeProvider
    {
        internal bool Reject { get; set; } internal int RejectedReads { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (Reject) { RejectedReads++; throw new IOException("Synthetic forbidden late clock."); } return inner.GetUtcNow(); }
    }
}
