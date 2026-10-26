using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryReviewExecutionTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "mixed", 1, 1)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "mixed", 1, 1)]
    [InlineData(PipelineRequestFactory.GitHub, "positive", 1, 0)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "positive", 1, 0)]
    [InlineData(PipelineRequestFactory.GitHub, "absence", 0, 1)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "absence", 0, 1)]
    public async Task Exact_complete_scope_human_review_admits_before_destination_key_and_records_only_restricted_held_evidence(string provider, string mode, int profiles, int absences)
    {
        using var f = new RestrictedRecoveryReviewFixture(provider, mode); await f.Initialize(); f.Stage.RemoveSources(); var before = f.StageAndSourceSnapshot(); f.ClearKeys();
        var review = await f.Approved(); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(profiles, review.Target.ProfileCount); Assert.Equal(absences, review.Target.AbsenceProofCount); Assert.Equal(f.Receipt.CompletionProofSha256, review.Target.CompletionProofSha256);
        var guardHeld = false; var transactionAvailable = false; long admitted = 0;
        f.DestinationKeys.OnGet = () =>
        {
            admitted = f.Admissions;
            try { using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { guardHeld = true; }
            using var db = f.Open(); using var tx = db.BeginTransaction(deferred: false); transactionAvailable = true; tx.Commit();
        };
        var result = await f.Execute(review);
        Assert.Equal(1, admitted); Assert.True(guardHeld); Assert.True(transactionAvailable); Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.Equal("HeldRestrictedStageVerified", result.Execution.Result!.Status);
        Assert.Equal(f.Receipt.CompletionProofSha256, result.Execution.Result.CompletionProofSha256); Assert.True(result.Execution.Result.DestinationCompletionProofAuthenticated);
        Assert.Equal(f.Plan.AffectedScopes.Count, f.Count("CredentialRestrictedRecoveryReviewCompleted")); AssertLimits(result, f); Assert.Equal(before, f.StageAndSourceSnapshot());
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Preparation_authorizes_all_scopes_before_private_stage_access_and_never_resolves_keys()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead); f.ClearKeys();
        using (var held = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<FoundationStoreException>(() => f.Coordinator().PrepareAsync(readers, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Rejected private metadata read", f.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_requests")); Assert.Empty(f.DestinationKeys.Requests);
        Assert.Equal("Approved", (await f.Approved()).State); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Empty_stage_is_inspectable_but_cannot_create_scope_authority()
    {
        using var f = new RestrictedRecoveryReviewFixture(mode: "empty"); await f.Initialize(); f.ClearKeys(); var before = f.StageAndSourceSnapshot();
        Assert.Equal("RestrictedRecoveryStageReviewEmptySource", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare())).Code);
        Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_requests")); Assert.Equal(before, f.StageAndSourceSnapshot());
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor-role")] [InlineData("executor-version")]
    [InlineData("context-expiry")] [InlineData("policy")] [InlineData("lifecycle")] [InlineData("instance")]
    public async Task Current_authority_drift_during_destination_await_leaves_one_consumed_unknown(string change)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: change == "context-expiry" ? TimeSpan.FromMinutes(1) : null);
        var paused = new PausedKeys(f.DestinationKeys); var before = f.StageAndSourceSnapshot(); f.ClearKeys(); var operation = f.Execute(review, f.Coordinator(paused), contexts);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(1, f.Admissions);
            if (change == "requester") f.Grant(RestrictedRecoveryReviewFixture.Requester, ["Operator"], enabled: false);
            else if (change == "approver") f.Grant(RestrictedRecoveryReviewFixture.Approver, ["Approver"], enabled: false);
            else if (change == "executor-role") f.Grant(RestrictedRecoveryReviewFixture.Executor, ["Operator"], enabled: false);
            else if (change == "executor-version") f.Grant(RestrictedRecoveryReviewFixture.Executor, ["Operator"]);
            else if (change == "context-expiry") f.Clock.Now = f.Clock.Now.AddMinutes(2);
            else if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
            else if (change == "lifecycle") f.Sql("UPDATE customer_lifecycle SET state='Offboarded',version=version+1 WHERE singleton=1;UPDATE cell SET enabled=0 WHERE singleton=1;");
            else f.Sql("UPDATE cell SET database_instance_id='" + Guid.NewGuid().ToString("N") + "' WHERE singleton=1");
            paused.Release.TrySetResult(); AssertUnknown(await operation);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndSourceSnapshot()); Assert.Equal(1, f.Admissions);
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor")]
    public async Task Revoking_only_the_absence_scope_grant_prevents_success_after_actual_authentication(string actor)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.DestinationKeys); var operation = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var key = actor == "requester" ? RestrictedRecoveryReviewFixture.Requester : actor == "approver" ? RestrictedRecoveryReviewFixture.Approver : RestrictedRecoveryReviewFixture.Executor;
            f.Sql("UPDATE memberships SET enabled=0 WHERE actor_key='" + key + "' AND project_id='other'");
            paused.Release.TrySetResult(); AssertUnknown(await operation);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Replacing_the_callers_context_list_during_await_does_not_extend_the_frozen_executor_expiry()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var supplied = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromMinutes(1))).ToList();
        var paused = new PausedKeys(f.DestinationKeys); var operation = f.Execute(review, f.Coordinator(paused), supplied);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); f.Clock.Now = f.Clock.Now.AddMinutes(2); supplied.Clear(); supplied.AddRange(await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)); paused.Release.TrySetResult(); AssertUnknown(await operation); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted"));
    }

    [Theory]
    [InlineData("digest")] [InlineData("plan-pin")] [InlineData("receipt-pin")] [InlineData("stage")]
    public async Task Exact_binding_or_stage_refusal_before_admission_has_no_destination_lookup(string change)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); f.ClearKeys();
        if (change == "stage") f.PrivateFile(Path.Combine(f.StageRoot, "unexpected.json"), "{}"u8.ToArray()); var before = f.StageAndSourceSnapshot();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, change == "digest" ? new string('a', 64) : review.Digest,
            f.Plan, change == "plan-pin" ? new string('b', 64) : f.Plan.Sha256, change == "receipt-pin" ? new string('c', 64) : f.Receipt.Sha256));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.StageAndSourceSnapshot());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_before_admission_has_no_claim_or_key_read()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); f.ClearKeys();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Execute(review, contexts: contexts, token: cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_retains_an_uncooperative_key_and_stage_guard_until_the_actual_await_returns(bool uncooperative)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndSourceSnapshot();
        var paused = new PausedKeys(f.DestinationKeys, !uncooperative); using var cancel = new CancellationTokenSource(); var operation = f.Execute(review, f.Coordinator(paused), token: cancel.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancel.Cancel();
            if (uncooperative) { Assert.False(operation.IsCompleted); AssertGuardHeld(f); using var db = f.Open(); using var tx = db.BeginTransaction(deferred: false); tx.Commit(); }
            paused.Release.TrySetResult(); var result = await operation; AssertUnknown(result); Assert.Equal("CallerCancelled", result.Execution!.Reason);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(before, f.StageAndSourceSnapshot()); Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted")); AssertGuardReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Cooperative_deadline_consumes_unknown_without_detaching_or_mutating_stage()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndSourceSnapshot(); var paused = new PausedKeys(f.DestinationKeys);
        var operation = f.Execute(review, f.Coordinator(paused, TimeSpan.FromSeconds(3)));
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(paused.Release.Task.IsCompleted); AssertUnknown(await operation.WaitAsync(TimeSpan.FromSeconds(15))); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(1, f.Admissions); Assert.Equal(before, f.StageAndSourceSnapshot()); AssertGuardReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Concurrent_execution_has_one_durable_admission_and_one_destination_lookup()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); f.ClearKeys(); var paused = new PausedKeys(f.DestinationKeys); var operation = f.Execute(review, f.Coordinator(paused));
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests); paused.Release.TrySetResult(); Assert.Equal("Succeeded", (await operation).Execution!.State); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(1, f.Admissions); Assert.Single(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")]
    public async Task Interruption_preserves_consumed_unknown_and_redacts_private_details(string phase)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator(); var before = f.StageAndSourceSnapshot(); f.ClearKeys();
        Fault(coordinator, observed => { if (observed == phase) { AssertGuardHeld(f); throw new IOException(f.Cell.Root + CredentialFixture.Token); } });
        var unknown = await f.Execute(review, coordinator); AssertUnknown(unknown); Assert.Equal(phase == "AfterAdmission" ? 0 : 1, f.DestinationKeys.Requests.Count); AssertLimits(unknown, f);
        Assert.Equal(before, f.StageAndSourceSnapshot()); Assert.Equal(1, f.Admissions); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); AssertGuardReleased(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Completion_or_unknown_audit_failure_cannot_leave_a_successful_result(bool failUnknown)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndSourceSnapshot();
        f.Sql("CREATE TRIGGER reject_restricted_complete BEFORE INSERT ON audit WHEN NEW.action='CredentialRestrictedRecoveryReviewCompleted' BEGIN SELECT RAISE(ABORT,'synthetic rejection'); END;");
        if (failUnknown) f.Sql("CREATE TRIGGER reject_restricted_unknown BEFORE INSERT ON audit WHEN NEW.action='CredentialRestrictedRecoveryReviewUnknown' BEGIN SELECT RAISE(ABORT,'synthetic rejection'); END;");
        if (failUnknown) Assert.Equal("credential-restricted-recovery-review-outcome-unknown", (await Assert.ThrowsAsync<CredentialException>(() => f.Execute(review))).Code);
        else AssertUnknown(await f.Execute(review));
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndSourceSnapshot()); AssertGuardReleased(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_wrong_destination_key_leaves_no_authenticated_result(bool wrong)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndSourceSnapshot();
        if (wrong) f.DestinationKeys.Values["restricted-destination"] = new byte[32]; else f.DestinationKeys.Values.Remove("restricted-destination");
        AssertUnknown(await f.Execute(review)); Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndSourceSnapshot()); AssertGuardReleased(f);
    }

    [Theory]
    [InlineData("stage")] [InlineData("cancel")] [InlineData("expiry")]
    public async Task Verifier_clock_effects_after_key_lookup_cannot_publish_success(string effect)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); using var cancel = new CancellationTokenSource();
        var clock = new EffectClock(f.Clock, () =>
        {
            if (effect == "stage") File.AppendAllText(Path.Combine(f.StageRoot, "completion.json"), " ");
            else if (effect == "cancel") cancel.Cancel(); else f.Clock.Now = f.Clock.Now.AddHours(2);
        });
        f.DestinationKeys.OnGet = clock.Arm;
        AssertUnknown(await f.Execute(review, f.Coordinator(verifierClock: clock), token: cancel.Token)); Assert.True(clock.Triggered);
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted"));
        if (effect == "stage") Assert.EndsWith(" ", File.ReadAllText(Path.Combine(f.StageRoot, "completion.json")));
        AssertGuardReleased(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Store_final_time_or_cancellation_cutoff_cannot_publish_success(bool cancelAtCutoff)
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromSeconds(40));
        using var caller = new CancellationTokenSource(); var clock = new EffectClock(f.Clock, () => { if (cancelAtCutoff) caller.Cancel(); }, cancelAtCutoff ? null : contexts.Min(p => p.ExpiresUtc).AddMilliseconds(1));
        var store = new FoundationStore(f.Database, f.CellId, f.Customer, clock); var coordinator = f.Coordinator(store: store);
        Fault(coordinator, phase => { if (phase == "AfterVerification") clock.Arm(); });
        AssertUnknown(await f.Execute(review, coordinator, contexts, caller.Token)); Assert.True(clock.Triggered);
        Assert.Equal(1, f.Admissions); Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewCompleted")); AssertGuardReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Final_concrete_session_fence_never_calls_verifier_clock_after_store_time_authentication()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var review = await f.Approved(); var verifierClock = new RejectingClock(f.Clock);
        var storeClock = new EffectClock(f.Clock, () => verifierClock.Reject = true); var store = new FoundationStore(f.Database, f.CellId, f.Customer, storeClock);
        var coordinator = f.Coordinator(store: store, verifierClock: verifierClock); Fault(coordinator, phase => { if (phase == "AfterVerification") storeClock.Arm(); });
        Assert.Equal("Succeeded", (await f.Execute(review, coordinator)).Execution!.State); Assert.True(storeClock.Triggered); Assert.Equal(0, verifierClock.RejectedReads); AssertGuardReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Concrete_session_rejects_a_byte_identical_result_produced_by_a_different_session()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var verifier = new CredentialRestrictedRecoveryStageVerifier(f.DestinationKeys, f.Clock);
        var held = typeof(CredentialRestrictedRecoveryStageVerifier).GetMethod("OpenPrepared", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(verifier, [f.Plan, f.Plan.Sha256, f.Receipt.Sha256])!;
        using var lease = (IDisposable)held; var method = held.GetType().GetMethod("VerifyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var genuine = await (Task<CredentialRestrictedRecoveryStageVerification>)method.Invoke(held, [CancellationToken.None])!;
        var other = await verifier.VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256); Assert.Equal(genuine.Sha256, other.Sha256);
        var assert = held.GetType().GetMethod("AssertVerifiedCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => assert.Invoke(held, [other, CancellationToken.None]));
        Assert.Equal("credential-restricted-recovery-stage-verified-session-mismatch", Assert.IsType<CredentialException>(error.InnerException).Code);
        assert.Invoke(held, [genuine, CancellationToken.None]); Assert.Equal(0, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Context_snapshot_uses_bounded_indices_without_enumerating_untrusted_input()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare); f.ClearKeys();
        var input = new IndexedContexts(contexts); var prepared = await f.Coordinator().PrepareAsync(input, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic bounded contexts", f.Clock.Now.AddMinutes(5));
        Assert.Equal("Prepared", prepared.State); Assert.False(input.Enumerated); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Context_count_drift_is_refused_before_private_files_or_store_mutation()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare); f.ClearKeys();
        using var held = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("credential-restricted-recovery-review-contexts-invalid", (await Assert.ThrowsAsync<CredentialException>(() => f.Coordinator().PrepareAsync(new IndexedContexts(contexts, drift: true), f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic changed list", f.Clock.Now.AddMinutes(5)))).Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_requests")); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Historical_completed_stage_can_receive_fresh_review_after_source_and_plan_expiry()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); f.Stage.RemoveSources(); f.Clock.Now = f.Clock.Now.AddHours(2); f.ClearKeys(); var review = await f.Approved();
        Assert.Equal("Succeeded", (await f.Execute(review)).Execution!.State); Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Wrong_database_instance_cannot_construct_an_execution_composition()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); f.ClearKeys();
        Assert.Equal("credential-restricted-recovery-review-instance-binding-invalid", Assert.Throws<CredentialException>(() => new FoundationCredentialRestrictedRecoveryStageReviewCoordinator(f.Store, new(f.DestinationKeys, f.Clock), Guid.NewGuid().ToString("N"))).Code);
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests);
    }

    static void AssertUnknown(FoundationCredentialRestrictedRecoveryStageReview result)
    { Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result); Assert.Null(result.Execution.ResultSha256); }
    static void AssertLimits(FoundationCredentialRestrictedRecoveryStageReview result, RestrictedRecoveryReviewFixture f)
    {
        Assert.Equal("FoundationBoundDispatchOnly", result.Target.AccessProfile);
        if (result.Execution?.Result is { } value) { Assert.Equal("FoundationBoundDispatchOnly", value.AccessProfile); Assert.True(value.InactiveRecoveryHold); Assert.False(value.PromotionAuthorized); Assert.False(value.LiveAdoptionAuthorized); Assert.False(value.ActivationPerformed); Assert.False(value.ExternalExecutionAuthorized); Assert.Equal("NotAssessed", value.IndependentBackupCoverage); }
        var json = JsonSerializer.Serialize(result); foreach (var excluded in new[] { f.Cell.Root, CredentialFixture.Token, f.Stage.Positive.AuthProfileId, f.Stage.Absent.AuthProfileId, "\"Tag\"", "\"Ciphertext\"" }) Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
    }
    static void Fault(FoundationCredentialRestrictedRecoveryStageReviewCoordinator coordinator, Action<string> action) => typeof(FoundationCredentialRestrictedRecoveryStageReviewCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, action);
    static void AssertGuardHeld(RestrictedRecoveryReviewFixture f) => Assert.Throws<IOException>(() => { using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
    static void AssertGuardReleased(RestrictedRecoveryReviewFixture f) { using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    static async Task Observe(Task task) { try { await task; } catch { } }
    sealed class PausedKeys(ICredentialKeyProvider source, bool cooperative = true) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await source.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class EffectClock(TimeProvider inner, Action action, DateTimeOffset? replacement = null) : TimeProvider
    {
        bool armed; internal bool Triggered { get; private set; }
        internal void Arm() { armed = true; }
        public override DateTimeOffset GetUtcNow() { if (armed) { armed = false; Triggered = true; action(); } return Triggered && replacement is not null ? replacement.Value : inner.GetUtcNow(); }
    }
    sealed class RejectingClock(TimeProvider inner) : TimeProvider
    {
        internal bool Reject { get; set; } internal int RejectedReads { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (Reject) { RejectedReads++; throw new IOException("Synthetic late clock callback."); } return inner.GetUtcNow(); }
    }
    sealed class IndexedContexts(IReadOnlyList<CustomerContext> source, bool drift = false) : IReadOnlyList<CustomerContext>
    {
        int counts; internal bool Enumerated { get; private set; }
        public int Count => drift && ++counts > 1 ? source.Count + 1 : source.Count;
        public CustomerContext this[int index] => source[index];
        public IEnumerator<CustomerContext> GetEnumerator() { Enumerated = true; throw new IOException("Synthetic unbounded enumeration refused."); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
