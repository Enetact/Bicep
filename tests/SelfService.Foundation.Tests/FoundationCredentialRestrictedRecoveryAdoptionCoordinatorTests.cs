using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryAdoptionCoordinatorTests
{
    const string Intents = "credential_restricted_recovery_adoption_intents";
    const string Approvals = "credential_restricted_recovery_adoption_approvals";

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "mixed")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "mixed")]
    [InlineData(PipelineRequestFactory.GitHub, "positive")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "positive")]
    [InlineData(PipelineRequestFactory.GitHub, "absence")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "absence")]
    public async Task Actual_bound_source_restore_gets_fresh_authenticated_prepare_and_distinct_approval_without_changing_effects(string provider, string mode)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(provider, mode); await f.Initialize();
        f.Stage.RemoveSources(); f.Cell.Keys.Values.Clear(); f.ClearKeys(); var protectedBefore = f.ProtectedSnapshot();
        var hold = await f.Hold(); var prepared = await f.Prepare(); Assert.Single(f.Destination.Requests); Assert.Equal("Prepared", prepared.State);
        f.Clock.Now = f.Clock.Now.AddSeconds(1); var approved = await f.Approve(prepared); Assert.Equal(2, f.Destination.Requests.Count);
        Assert.Equal("Approved", approved.State); Assert.NotEqual(prepared.PreparedVerificationSha256, approved.Approval!.StageVerificationSha256);
        Assert.NotEqual(prepared.RequesterKey, approved.Approval.ApproverKey); Assert.Equal(f.SourceBinding, approved.Target.SourceRuntimeBinding);
        Assert.Equal(hold.Hold, approved.Target.RecoveryHold); Assert.Equal(f.Instance, approved.Target.DatabaseInstanceId); Assert.True(approved.Target.RuntimeBindingRequired);
        Assert.Null(approved.Target.CurrentRuntimeBindingSha256); Assert.Equal(f.Identity, approved.Target.RecoveryTarget.CheckpointAuthority); Assert.Equal(f.Head, approved.Target.RecoveryTarget.Checkpoint);
        Assert.Equal(f.Receipt.CompletionProofSha256, approved.Target.RecoveryTarget.StageTarget.CompletionProofSha256);
        Assert.Equal(f.ActivationIdentity, approved.Target.Selection.ActivationAuthority); Assert.Equal(f.ActivationHead.Generation, approved.Target.Selection.ActivationGeneration);
        Assert.Equal(f.ActivationHead.Sha256, approved.Target.Selection.ActivationBindingSha256); Assert.Equal(prepared.TargetSha256, approved.TargetSha256);
        AssertLimits(approved, f); Assert.Equal(protectedBefore, f.ProtectedSnapshot()); Assert.Equal(hold, await f.Hold()); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_executions")); Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_joint_executions"));
        Assert.False(Directory.Exists(f.FutureVault)); Assert.False(Directory.Exists(f.FutureRollback)); Released(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Every_destination_await_retains_real_activation_checkpoint_stage_guards_without_an_open_SQL_transaction(bool approve)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null;
        var paused = new PausedKeys(f.Destination); var coordinator = f.Coordinator(paused); f.ClearKeys(); var operation = Operation(f, approve, intent, coordinator);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Held(f);
            using var db = f.Open(); using var tx = db.BeginTransaction(deferred: false); tx.Commit();
            Assert.Equal(approve ? 1 : 0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
            Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => f.Activation.Activate(f.Review.Database, f.SourceInstance, f.ActivationHead.Generation, f.ActivationHead.Sha256)).Code);
            paused.Release.TrySetResult(); Assert.Equal(approve ? "Approved" : "Prepared", (await operation).State);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Single(f.Destination.Requests); Released(f);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Cancellation_keeps_all_real_guards_until_uncooperative_dependency_returns_without_persisting_new_authority(bool approve, bool uncooperative)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null;
        var before = f.ProtectedSnapshot(); var paused = new PausedKeys(f.Destination, !uncooperative); using var cancellation = new CancellationTokenSource();
        var operation = Operation(f, approve, intent, f.Coordinator(paused), cancellation.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancellation.Cancel();
            if (uncooperative) { Assert.False(operation.IsCompleted); Held(f); }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        NoEffect(f, approve); Assert.Equal(before, f.ProtectedSnapshot()); Released(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cooperative_deadline_does_not_leave_a_new_intent_or_approval(bool approve)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null; var paused = new PausedKeys(f.Destination);
        var operation = Operation(f, approve, intent, f.Coordinator(paused, TimeSpan.FromSeconds(3)));
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); var error = await Assert.ThrowsAsync<CredentialException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15))); Assert.Equal("credential-restricted-recovery-adoption-timeout", error.Code); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        NoEffect(f, approve); Released(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Both_review_phases_reauthenticate_the_destination_key_and_refuse_key_loss(bool approve)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null; f.ClearKeys(); f.Destination.Values.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => Operation(f, approve, intent, f.Coordinator())); NoEffect(f, approve); Released(f);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Single(f.Destination.Requests);
    }

    [Theory]
    [InlineData("verifier-activation")] [InlineData("checkpoint-activation")]
    [InlineData("activation-checkpoint")] [InlineData("activation-stage")]
    public async Task Late_cross_authority_clock_effects_are_refused_before_first_destination_lookup(string change)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize();
        var mutation = change.EndsWith("activation", StringComparison.Ordinal) ? f.ActivationRecord : change.EndsWith("checkpoint", StringComparison.Ordinal) ? f.Joint.CheckpointPath : Path.Combine(f.Joint.StageRoot, "completion.json");
        var clock = new EffectClock(f.Clock, () => File.AppendAllText(mutation, "!"), skip: change.StartsWith("activation", StringComparison.Ordinal) ? 1 : 2);
        var activation = change.StartsWith("activation", StringComparison.Ordinal) ? new FoundationCellActivationAuthority(f.ActivationRoot, f.Review.Customer, f.Review.CellId, clock) : f.Activation;
        var checkpoints = change.StartsWith("checkpoint", StringComparison.Ordinal) ? f.Joint.Reopen(clock) : f.Checkpoints;
        var coordinator = f.Coordinator(activation: activation, checkpoints: checkpoints, verifierClock: change.StartsWith("verifier", StringComparison.Ordinal) ? clock : null);
        Fault(coordinator, phase => { if (phase == "BeforeVerification") clock.Arm(); }); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(coordinator)); Assert.True(clock.Triggered); Assert.Empty(f.Destination.Requests); NoEffect(f, false); Released(f);
    }

    [Theory]
    [InlineData("vault")] [InlineData("rollback")]
    public async Task A_late_activation_clock_cannot_reserve_a_proposed_root_then_allow_the_first_key_lookup(string target)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var path = target == "vault" ? f.FutureVault : f.FutureRollback;
        var clock = new EffectClock(f.Clock, () => Directory.CreateDirectory(path), skip: 1);
        var activation = new FoundationCellActivationAuthority(f.ActivationRoot, f.Review.Customer, f.Review.CellId, clock);
        var coordinator = f.Coordinator(activation: activation); Fault(coordinator, phase => { if (phase == "BeforeVerification") clock.Arm(); }); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(coordinator)); Assert.True(clock.Triggered); Assert.True(Directory.Exists(path)); Assert.Empty(f.Destination.Requests); NoEffect(f, false); Released(f);
    }

    [Theory]
    [InlineData("vault")] [InlineData("rollback")]
    public async Task Existing_proposed_root_is_preserved_and_refused_without_key_or_intent_effect(string target)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var path = target == "vault" ? f.FutureVault : f.FutureRollback; Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare()); Assert.True(Directory.Exists(path)); Assert.Empty(Directory.GetFileSystemEntries(path)); Assert.Empty(f.Destination.Requests); NoEffect(f, false);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Proposed_root_overlap_or_containment_cannot_become_a_selection(bool nested)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var coordinator = f.Coordinator(futureRollback: nested ? Path.Combine(f.FutureVault, "child") : f.FutureVault);
        Assert.Equal("credential-restricted-recovery-adoption-future-roots-overlap", (await Assert.ThrowsAsync<CredentialException>(() => f.Prepare(coordinator))).Code);
        Assert.Empty(f.Destination.Requests); NoEffect(f, false); Assert.False(Directory.Exists(f.FutureVault)); Assert.False(Directory.Exists(f.FutureRollback));
    }

    [Theory]
    [InlineData(false, "activation")] [InlineData(true, "activation")]
    [InlineData(false, "stage")] [InlineData(true, "stage")]
    [InlineData(false, "future-root")] [InlineData(true, "future-root")]
    public async Task Final_SQL_clock_effects_cannot_publish_a_record_after_custody_or_freshness_changed(bool approve, string change)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null;
        var clock = new EffectClock(f.Clock, () => { if (change == "future-root") Directory.CreateDirectory(f.FutureVault); else File.AppendAllText(change == "activation" ? f.ActivationRecord : Path.Combine(f.Joint.StageRoot, "completion.json"), "!"); });
        var store = new FoundationStore(f.Database, f.Review.CellId, f.Review.Customer, clock); var coordinator = f.Coordinator(store: store);
        Fault(coordinator, phase => { if (phase == "AfterVerification") clock.Arm(); }); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => Operation(f, approve, intent, coordinator)); Assert.True(clock.Triggered); NoEffect(f, approve); Released(f); Assert.Single(f.Destination.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Final_SQL_fence_calls_no_activation_checkpoint_or_verifier_clock(bool approve)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null;
        var activationClock = new RejectingClock(f.Clock); var cpClock = new RejectingClock(f.Clock); var verifierClock = new RejectingClock(f.Clock);
        var storeClock = new EffectClock(f.Clock, () => { activationClock.Reject = true; cpClock.Reject = true; verifierClock.Reject = true; });
        var store = new FoundationStore(f.Database, f.Review.CellId, f.Review.Customer, storeClock);
        var coordinator = f.Coordinator(store: store, activation: new(f.ActivationRoot, f.Review.Customer, f.Review.CellId, activationClock), checkpoints: f.Joint.Reopen(cpClock), verifierClock: verifierClock);
        Fault(coordinator, phase => { if (phase == "AfterVerification") storeClock.Arm(); });
        Assert.Equal(approve ? "Approved" : "Prepared", (await Operation(f, approve, intent, coordinator)).State); Assert.True(storeClock.Triggered);
        Assert.Equal(0, activationClock.RejectedReads); Assert.Equal(0, cpClock.RejectedReads); Assert.Equal(0, verifierClock.RejectedReads); Released(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Caller_cancellation_at_final_SQL_cutoff_rolls_back_preparation_or_approval(bool approve)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approve ? await f.Prepare() : null; using var cancel = new CancellationTokenSource();
        var clock = new EffectClock(f.Clock, cancel.Cancel); var store = new FoundationStore(f.Database, f.Review.CellId, f.Review.Customer, clock); var coordinator = f.Coordinator(store: store);
        Fault(coordinator, phase => { if (phase == "AfterVerification") clock.Arm(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operation(f, approve, intent, coordinator, cancel.Token)); Assert.True(clock.Triggered); NoEffect(f, approve); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Copying_a_real_v1_activation_authority_does_not_copy_its_actual_custody_selection()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var copy = f.Review.Child("copied-adoption-activation-" + Guid.NewGuid().ToString("N")); ActivationProvisionFixtureRoots.Create(copy);
        foreach (var file in Directory.EnumerateFiles(f.ActivationRoot)) f.Review.PrivateFile(Path.Combine(copy, Path.GetFileName(file)), File.ReadAllBytes(file));
        var copied = new FoundationCellActivationAuthority(copy, f.Review.Customer, f.Review.CellId, f.Clock); Assert.Equal(f.ActivationIdentity.AuthorityId, copied.DescribeIdentity().AuthorityId);
        Assert.NotEqual(f.ActivationIdentity.CustodyCommitmentSha256, copied.DescribeIdentity().CustodyCommitmentSha256);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(f.Coordinator(activation: copied))); Assert.Empty(f.Destination.Requests); NoEffect(f, false); Released(f);
    }

    [WindowsCredentialFact]
    public async Task Equal_windows_case_aliases_produce_the_same_future_path_selection_without_creating_a_root()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare();
        var approved = await f.Approve(intent, f.Coordinator(futureVault: f.FutureVault.ToLowerInvariant(), futureRollback: f.FutureRollback.ToUpperInvariant()));
        Assert.Equal(intent.Target.Selection, approved.Target.Selection); Assert.Equal("Approved", approved.State); Assert.False(Directory.Exists(f.FutureVault)); Assert.False(Directory.Exists(f.FutureRollback));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task An_equal_result_from_another_real_three_guard_session_or_a_reconstructed_value_cannot_persist_an_intent(bool reconstructed)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var one = Open(f); using var leaseOne = (IDisposable)one; var first = await Verify(one);
        var two = Open(f); using var leaseTwo = (IDisposable)two; var second = await Verify(two); Assert.Equal(first.Sha256, second.Sha256);
        var borrowed = reconstructed ? (CredentialRestrictedRecoveryStageVerification)Activator.CreateInstance(typeof(CredentialRestrictedRecoveryStageVerification),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [first.Receipt, first.ObservedUtc], null)! : second;
        Assert.Equal(first.Sha256, borrowed.Sha256);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var method = typeof(FoundationStore).GetMethod("PrepareCredentialRestrictedRecoveryAdoptionIntent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(f.RestoredStore, [contexts, f.Plan, borrowed, one, "Rejected borrowed session result", f.Clock.Now.AddMinutes(5), CancellationToken.None]));
        if (reconstructed) Assert.Equal("credential-restricted-recovery-stage-verified-session-mismatch", Assert.IsType<CredentialException>(error.InnerException).Code);
        else Assert.Equal("RestrictedRecoveryAdoptionVerificationMismatch", Assert.IsType<FoundationStoreException>(error.InnerException).Code);
        NoEffect(f, false); Held(f);
    }

    [WindowsCredentialFact]
    public async Task Historical_source_expiry_and_missing_source_database_do_not_convert_intent_review_into_fresh_staging()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); f.Stage.RemoveSources(); File.Move(f.Review.Database, f.Review.Child("preserved-unavailable-source.db"));
        f.Clock.Now = f.Clock.Now.AddHours(2); f.Cell.Keys.Values.Clear(); f.ClearKeys(); var prepared = await f.Prepare();
        Assert.Equal("Approved", (await f.Approve(prepared)).State); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(2, f.Destination.Requests.Count); Released(f);
    }

    static Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> Operation(RestrictedRecoveryAdoptionFixture f, bool approve,
        FoundationCredentialRestrictedRecoveryAdoptionIntent? intent, FoundationCredentialRestrictedRecoveryAdoptionCoordinator coordinator, CancellationToken token = default) =>
        approve ? f.Approve(intent!, coordinator, token: token) : f.Prepare(coordinator, token: token);
    static object Open(RestrictedRecoveryAdoptionFixture f) => typeof(FoundationStore).Assembly.GetType("SelfService.Foundation.CredentialRestrictedRecoveryAdoptionSession")!
        .GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [f.Activation, f.ActivationIdentity, f.ActivationHead.Generation, f.ActivationHead.Sha256,
            f.Checkpoints, f.Identity, new CredentialRestrictedRecoveryStageVerifier(f.Destination, f.Clock), f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256, f.FutureVault, f.FutureRollback])!;
    static Task<CredentialRestrictedRecoveryStageVerification> Verify(object held) => (Task<CredentialRestrictedRecoveryStageVerification>)held.GetType().GetMethod("VerifyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(held, [CancellationToken.None])!;
    static void Fault(FoundationCredentialRestrictedRecoveryAdoptionCoordinator coordinator, Action<string> action) => typeof(FoundationCredentialRestrictedRecoveryAdoptionCoordinator).GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, action);
    static void NoEffect(RestrictedRecoveryAdoptionFixture f, bool approve) { Assert.Equal(approve ? 1 : 0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_executions")); Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_joint_executions")); }
    static void Held(RestrictedRecoveryAdoptionFixture f) { foreach (var path in new[] { f.ActivationGuard, f.Joint.CheckpointGuard, f.Joint.StageGuard }) Assert.Throws<IOException>(() => { using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }); }
    static void Released(RestrictedRecoveryAdoptionFixture f) { foreach (var path in new[] { f.ActivationGuard, f.Joint.CheckpointGuard, f.Joint.StageGuard }) { using var guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } }
    static void AssertLimits(FoundationCredentialRestrictedRecoveryAdoptionIntent intent, RestrictedRecoveryAdoptionFixture f)
    {
        Assert.Equal("FoundationBoundDispatchOnly", intent.AccessProfile); Assert.Equal("ProvisionFreshRestrictedNamespacesForRestoredInstance", intent.Operation);
        Assert.False(intent.ExecutionAdmissionAvailable); Assert.False(intent.AdoptionAuthorized); Assert.False(intent.ActivationPerformed); Assert.False(intent.RecoveryHoldReleased);
        Assert.False(intent.FreshDestinationReservationPerformed); Assert.False(intent.SourceHostFenced); Assert.False(intent.ExternalExecutionAuthorized); Assert.False(intent.IndependentKeyBackupVerified);
        var json = JsonSerializer.Serialize(intent); foreach (var secret in new[] { f.Cell.Root, CredentialFixture.Token, f.Stage.Positive.AuthProfileId, "\"Tag\"", "\"Ciphertext\"" }) Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
    }
    static async Task Observe(Task task) { try { await task; } catch { } }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class EffectClock(TimeProvider inner, Action effect, int skip = 0) : TimeProvider
    {
        bool armed; int remaining; internal bool Triggered { get; private set; } internal void Arm() { armed = true; remaining = skip; }
        public override DateTimeOffset GetUtcNow() { if (armed && remaining-- == 0) { armed = false; Triggered = true; effect(); } return inner.GetUtcNow(); }
    }
    sealed class RejectingClock(TimeProvider inner) : TimeProvider
    {
        internal bool Reject { get; set; } internal int RejectedReads { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (Reject) { RejectedReads++; throw new IOException("Unexpected late adoption clock callback."); } return inner.GetUtcNow(); }
    }
}
