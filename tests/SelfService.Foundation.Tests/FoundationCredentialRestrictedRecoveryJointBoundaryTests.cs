using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryJointBoundaryTests
{
    const string Ordinary = "credential_restricted_recovery_stage_review_executions";
    const string Joint = "credential_restricted_recovery_joint_executions";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("null-list")] [InlineData("empty")] [InlineData("null-item")] [InlineData("missing")]
    [InlineData("duplicate")] [InlineData("extra")] [InlineData("actor")] [InlineData("version")]
    [InlineData("customer")] [InlineData("cell")] [InlineData("workload")] [InlineData("support")]
    [InlineData("capability")] [InlineData("expired")]
    public async Task Every_scope_must_authorize_before_private_guards_keys_or_either_admission(string attack)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var coordinator = f.Coordinator();
        var contexts = (await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify)).ToList(); Corrupt(contexts, attack, f.Cell.Clock.Now);
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        using (var checkpoint = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var stage = new FileStream(f.Review.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review, coordinator, attack == "null-list" ? null! : contexts, explicitContexts: true));
        Assert.Equal(0, f.Review.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.Null((await f.Review.Read(review.Id)).Execution);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("source")] [InlineData("authority")]
    [InlineData("signer")] [InlineData("public-key")] [InlineData("commitment")] [InlineData("profile")] [InlineData("database-instance")]
    public async Task Incoming_identity_metadata_cannot_replace_the_configured_actual_authority(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var identity = f.Identity;
        var wrong = change switch
        {
            "customer" => identity with { CustomerId = Guid.NewGuid().ToString() }, "cell" => identity with { CellId = Guid.NewGuid().ToString() },
            "source" => identity with { SourceNamespace = identity.SourceNamespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } },
            "authority" => identity with { AuthorityId = Guid.NewGuid().ToString("N") }, "signer" => identity with { SignerKeyId = "another-signer" },
            "public-key" => identity with { PublicKeySha256 = new string('a', 64) }, "commitment" => identity with { CommitmentSha256 = new string('b', 64) },
            "profile" => identity with { AccessProfile = "General" }, _ => identity
        };
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        Assert.Throws<CredentialException>(() => new FoundationCredentialRestrictedRecoveryJointReviewCoordinator(f.Review.Store, f.Checkpoints, wrong,
            new(f.Destination, f.Cell.Clock), change == "database-instance" ? Guid.NewGuid().ToString("N") : f.Review.Instance));
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal(0, f.JointAdmissions);
    }

    [WindowsCredentialFact]
    public async Task Copying_a_real_checkpoint_header_and_head_to_another_private_root_does_not_copy_its_custody_identity()
    {
        using var f = new JointFixture(); await f.Initialize(); var before = f.ProtectedSnapshot(); var copy = f.Review.Child("copied-joint-checkpoints-" + Guid.NewGuid().ToString("N"));
        ActivationProvisionFixtureRoots.Create(copy);
        foreach (var file in Directory.EnumerateFiles(f.CheckpointRoot)) f.Review.PrivateFile(Path.Combine(copy, Path.GetFileName(file)!), File.ReadAllBytes(file));
        f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => new CredentialRestrictedRecoverySetCheckpointAuthority(copy, f.Identity.SourceNamespace,
            f.Identity.SignerKeyId, f.Identity.PublicKeySha256, f.Cell.Clock));
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal(0, f.JointAdmissions);
    }

    [Theory]
    [InlineData("sequence")] [InlineData("head")] [InlineData("digest")] [InlineData("plan")] [InlineData("receipt")]
    public async Task Exact_review_stage_and_current_head_pins_are_checked_before_destination_access(string attack)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var contexts = await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteApprovedAsync(contexts, review.Id, attack == "digest" ? new string('f', 64) : review.Digest,
            f.Review.Plan, attack == "plan" ? new string('e', 64) : f.Review.Plan.Sha256, attack == "receipt" ? new string('d', 64) : f.Review.Receipt.Sha256,
            attack == "sequence" ? f.Head.Sequence + 1 : f.Head.Sequence, attack == "head" ? new string('c', 64) : f.Head.Sha256));
        Assert.Equal(0, f.Review.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.ProtectedSnapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_new_current_head_before_or_after_approval_refuses_the_old_exact_stage_without_consuming_review(bool advanceBeforeApproval)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Prepare();
        if (!advanceBeforeApproval) review = f.Review.Store.ApproveCredentialRestrictedRecoveryStageReview(await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        using var next = await f.Capture(2); var newer = await f.Accept(next);
        if (advanceBeforeApproval) review = f.Review.Store.ApproveCredentialRestrictedRecoveryStageReview(await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        f.ClearKeys(); var contexts = await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteApprovedAsync(contexts, review.Id, review.Digest, f.Review.Plan, f.Review.Plan.Sha256,
            f.Review.Receipt.Sha256, newer.Sequence, newer.Sha256));
        Assert.Equal(0, f.Review.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests);
        Assert.Equal("Approved", (await f.Review.Read(review.Id)).State); Assert.Equal(newer.Sha256, f.Checkpoints.ReadCurrent()!.Sha256);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor")] [InlineData("policy")] [InlineData("instance")] [InlineData("expiry")]
    public async Task Current_scope_grants_policy_instance_and_human_lifetime_remain_authority(string changed)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var contexts = await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        if (changed is "requester" or "approver" or "executor")
        {
            var actor = changed == "requester" ? RestrictedRecoveryReviewFixture.Requester : changed == "approver" ? RestrictedRecoveryReviewFixture.Approver : RestrictedRecoveryReviewFixture.Executor;
            f.Review.Grant(actor, ["Viewer"], new("other", "qa"));
        }
        if (changed == "policy") f.Review.Sql("UPDATE cell SET policy_version=policy_version+1");
        if (changed == "instance") f.Review.Sql("UPDATE cell SET database_instance_id='eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'");
        if (changed == "expiry") f.Cell.Clock.Now = review.ExpiresAt;
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review, contexts: contexts));
        Assert.Equal(0, f.Review.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Ordinary_and_joint_execution_share_the_same_consumed_admission_in_both_orders(bool jointFirst, bool succeed)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); f.ClearKeys();
        if (jointFirst)
        {
            var coordinator = f.Coordinator(); if (!succeed) Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("Synthetic joint interruption."); });
            var result = await f.Execute(review, coordinator); Assert.Equal(succeed ? "Succeeded" : "OutcomeUnknown", result.State);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Review.Execute(review)); Assert.Equal(1, f.JointAdmissions);
        }
        else
        {
            var coordinator = f.Review.Coordinator(); if (!succeed) typeof(FoundationCredentialRestrictedRecoveryStageReviewCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator,
                new Action<string>(phase => { if (phase == "AfterAdmission") throw new IOException("Synthetic ordinary interruption."); }));
            var result = await f.Review.Execute(review, coordinator); Assert.Equal(succeed ? "Succeeded" : "OutcomeUnknown", result.Execution!.State);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Equal(0, f.JointAdmissions);
        }
        Assert.Equal(1, f.Review.Admissions); Assert.Equal(succeed ? 1 : 0, f.Destination.Requests.Count); AssertBothGuardsReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Concurrent_ordinary_and_joint_attempts_have_one_actual_destination_lookup_and_one_admission_winner()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); f.ClearKeys(); var paused = new PausedKeys(f.Destination);
        var contexts = await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> OrdinaryAttempt() { await start.Task; try { await f.Review.Execute(review, f.Review.Coordinator(paused), contexts); return null; } catch (Exception error) { return error; } }
        async Task<Exception?> JointAttempt() { await start.Task; try { await f.Execute(review, f.Coordinator(paused), contexts); return null; } catch (Exception error) { return error; } }
        var ordinary = Task.Run(OrdinaryAttempt); var joint = Task.Run(JointAttempt); start.SetResult();
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); var losing = await Task.WhenAny(ordinary, joint).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsType<FoundationStoreException>(await losing); Assert.Equal(1, f.Review.Admissions); Assert.Equal(1, paused.Requests);
            Assert.InRange(f.JointAdmissions, 0, 1); Assert.Equal("OutcomeUnknown", f.Text("SELECT state FROM " + Ordinary));
        }
        finally { paused.Release.TrySetResult(); await Task.WhenAll(ordinary, joint); }
        var results = await Task.WhenAll(ordinary, joint); Assert.Single(results, error => error is null); Assert.Single(results, error => error is FoundationStoreException);
        Assert.Equal("Succeeded", (await f.Review.Read(review.Id)).Execution!.State); Assert.Equal(1, paused.Requests);
        if (f.JointAdmissions == 1) Assert.Equal("Succeeded", (await f.ReadJoint(review.Id)).State);
    }

    [Theory]
    [InlineData("CredentialRestrictedRecoveryReviewAdmitted")] [InlineData("CredentialRestrictedRecoveryJointAdmitted")]
    public async Task Failure_of_either_admission_audit_rolls_back_the_pair_before_keys(string action)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var coordinator = f.Coordinator(); var before = f.ProtectedSnapshot();
        f.Review.Sql("CREATE TRIGGER synthetic_joint_admission_audit BEFORE INSERT ON audit WHEN NEW.action='" + action + "' BEGIN SELECT RAISE(ABORT,'synthetic unavailable audit'); END;");
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review, coordinator));
        Assert.Equal(0, f.Review.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryReviewAdmitted"));
        Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryJointAdmitted")); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.ProtectedSnapshot()); AssertBothGuardsReleased(f);
    }

    [Theory]
    [InlineData("CredentialRestrictedRecoveryReviewCompleted")] [InlineData("CredentialRestrictedRecoveryJointCompleted")]
    public async Task Failure_of_either_completion_audit_cannot_expose_one_successful_side(string action)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var coordinator = f.Coordinator(); var before = f.ProtectedSnapshot();
        f.Review.Sql("CREATE TRIGGER synthetic_joint_completion_audit BEFORE INSERT ON audit WHEN NEW.action='" + action + "' BEGIN SELECT RAISE(ABORT,'synthetic unavailable audit'); END;");
        var result = await f.Execute(review, coordinator); AssertUnknown(result); Assert.Equal("OutcomeUnknown", (await f.Review.Read(review.Id)).Execution!.State);
        Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryJointCompleted"));
        Assert.Equal(1, f.Review.Admissions); Assert.Equal(1, f.JointAdmissions); Assert.Equal(before, f.ProtectedSnapshot()); AssertBothGuardsReleased(f);
    }

    [WindowsCredentialFact]
    public async Task Both_guards_remain_actual_custody_while_final_SQL_waits_for_an_independent_writer()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); using var next = await f.Capture(2);
        var paused = new PausedKeys(f.Destination); var coordinator = f.Coordinator(paused); var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Fault(coordinator, phase => { if (phase == "AfterVerification") verified.TrySetResult(); });
        var operation = f.Execute(review, coordinator); SqliteConnection? writer = null; SqliteTransaction? transaction = null;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); writer = f.Review.Open(); transaction = writer.BeginTransaction(deferred: false);
            paused.Release.TrySetResult(); await verified.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(operation.IsCompleted);
            Assert.Throws<IOException>(() => new FileStream(f.Review.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            transaction.Commit(); transaction.Dispose(); transaction = null; writer.Dispose(); writer = null;
            Assert.Equal("Succeeded", (await operation).State);
        }
        finally { paused.Release.TrySetResult(); transaction?.Dispose(); writer?.Dispose(); try { await operation; } catch { } }
        AssertBothGuardsReleased(f);
    }

    [Theory]
    [InlineData("stage")] [InlineData("checkpoint")]
    public async Task A_final_SQL_clock_callback_cannot_change_either_snapshot_and_publish_dual_success(string changed)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var clock = new CallbackClock(f.Cell.Clock.Now);
        var store = new FoundationStore(f.Review.Database, f.Review.CellId, f.Review.Customer, clock); var coordinator = f.Coordinator(store: store);
        var path = changed == "stage" ? Path.Combine(f.Review.StageRoot, "completion.json") : Path.Combine(f.CheckpointRoot, "checkpoint-0001.json");
        var original = File.ReadAllBytes(path); var observed = false;
        // Stage custody pins exact bytes; checkpoint custody revalidates its canonical, authenticated chain.
        var appended = changed == "stage" ? " " : "!";
        Fault(coordinator, phase => { if (phase == "AfterVerification") clock.Next = () => { File.AppendAllText(path, appended); observed = true; }; });
        f.ClearKeys(); var result = await f.Execute(review, coordinator); Assert.True(observed); AssertUnknown(result);
        Assert.Equal("OutcomeUnknown", (await f.Review.Read(review.Id)).Execution!.State);
        Assert.Equal(original.Concat(new[] { (byte)appended[0] }).ToArray(), File.ReadAllBytes(path)); Assert.Single(f.Destination.Requests);
        Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryReviewCompleted")); Assert.Equal(0, f.Review.Count("CredentialRestrictedRecoveryJointCompleted"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("revoked")]
    public async Task Joint_history_read_requires_current_authority_for_every_scope(string attack)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); await f.Execute(review);
        var contexts = (await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryRead)).ToList();
        if (attack == "missing") contexts.RemoveAt(1); else if (attack == "duplicate") contexts.Add(contexts[0]);
        else f.Review.Grant(MaintenanceExecutorFixture.Auditor, ["Viewer"], new("other", "qa"));
        Assert.Throws<FoundationStoreException>(() => f.Review.Store.GetCredentialRestrictedRecoveryJointVerification(contexts, review.Id));
    }

    [WindowsCredentialFact]
    public async Task Successful_history_does_not_claim_the_old_head_is_still_current_or_revive_expired_human_authority()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var original = await f.Execute(review);
        using var next = await f.Capture(2); var current = await f.Accept(next);
        f.Cell.Clock.Now = review.ExpiresAt.AddMinutes(1); f.Review.Grant(RestrictedRecoveryReviewFixture.Requester, ["Viewer"]); f.Review.Grant(RestrictedRecoveryReviewFixture.Approver, ["Viewer"]); f.ClearKeys();
        var history = await f.ReadJoint(review.Id); Assert.Equal("Succeeded", history.State); Assert.Equal(original.Result, history.Result);
        Assert.Equal(f.Head.Sha256, history.Target.Checkpoint.Sha256); Assert.Equal(current.Sha256, f.Checkpoints.ReadCurrent()!.Sha256); Assert.NotEqual(current.Sha256, history.Target.Checkpoint.Sha256);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.Destination.Requests); Assert.Equal(1, f.JointAdmissions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Restore_quarantines_unknown_joint_authority_and_retains_successful_dual_history_without_replay(bool succeed)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var coordinator = f.Coordinator();
        if (!succeed) Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("Synthetic joint recovery interruption."); });
        var original = await f.Execute(review, coordinator); var ordinary = await f.Review.Read(review.Id);
        var jointRows = f.Rows(Joint); var ordinaryRows = f.Rows(Ordinary); var snapshot = f.Review.Store.CreateBackup(f.Review.Child("joint-backup.sqlite"));
        var source = File.ReadAllBytes(snapshot.DatabasePath); var protectedBefore = f.ProtectedSnapshot(); f.ClearKeys();
        var target = f.Review.Child("joint-restored.sqlite"); var restored = FoundationStore.RestoreBackup(snapshot, target, f.Cell.Clock);
        var contexts = await f.Review.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: restored); var history = restored.GetCredentialRestrictedRecoveryJointVerification(contexts, review.Id);
        Assert.Equal(succeed ? "Approved" : "Quarantined", history.ReviewState); Assert.Equal(!succeed, history.Quarantined);
        Assert.Equal(JsonSerializer.Serialize(original.Result, Json), JsonSerializer.Serialize(history.Result, Json)); Assert.Equal(original.Digest, history.Digest);
        Assert.Equal(ordinaryRows, f.Rows(Ordinary, target)); Assert.Equal(jointRows, f.Rows(Joint, target));
        var restoredOrdinary = restored.GetCredentialRestrictedRecoveryStageReview(contexts, review.Id); Assert.Equal(ordinary.Execution, restoredOrdinary.Execution);
        Assert.NotEqual(f.Review.Instance, restored.GetCellInstanceIdentity().DatabaseInstanceId);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review, f.Coordinator(store: restored)));
        Assert.Equal(source, File.ReadAllBytes(snapshot.DatabasePath)); Assert.Equal(protectedBefore, f.ProtectedSnapshot()); Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task A_genuine_audit_capacity_failure_cannot_leave_a_partially_held_joint_restore()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var coordinator = f.Coordinator();
        Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("Synthetic joint unknown."); }); await f.Execute(review, coordinator);
        var backup = FoundationTestAuditCapacity.FillSnapshot(f.Review.Store.CreateBackup(f.Review.Child("joint-full-audit.sqlite")), f.Cell.Clock);
        var before = File.ReadAllBytes(backup.DatabasePath); var stage = f.ProtectedSnapshot(); var target = f.Review.Child("joint-no-partial.sqlite"); f.ClearKeys();
        Assert.Equal("AuditCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup, target, f.Cell.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.Equal(before, File.ReadAllBytes(backup.DatabasePath)); Assert.Equal(stage, f.ProtectedSnapshot()); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Existing_scoped_joint_decoder_refuses_the_new_restricted_target_wire_shape()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Review.Approved(); var result = await f.Execute(review); var metadata = JsonSerializer.Serialize(result, Json);
        foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.Review.Stage.Positive.AuthProfileId, "\"tag\":", "\"ciphertext\":" }) Assert.DoesNotContain(forbidden, metadata, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("FoundationBoundDispatchOnly", result.Target.AccessProfile); Assert.Equal(f.Review.Receipt.CompletionProofSha256, result.Result!.CompletionProofSha256);
        Assert.True(result.Result.DestinationCompletionProofAuthenticated); Assert.False(result.Result.PromotionAuthorized); Assert.False(result.Result.LiveAdoptionAuthorized); Assert.False(result.Result.ActivationPerformed);
        var exception = Assert.Throws<TargetInvocationException>(() => typeof(FoundationStore).GetMethod("ParseScopedRecoveryJointTarget", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [JsonSerializer.Serialize(result.Target, Json)]));
        Assert.IsType<FoundationStoreException>(exception.InnerException);
    }

    static void AssertUnknown(FoundationCredentialRestrictedRecoveryJointVerification result)
    { Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.Result); Assert.Null(result.ResultSha256); Assert.Null(result.CompletedAt); }
    static void AssertBothGuardsReleased(JointFixture f)
    { using var cp = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); using var stage = new FileStream(f.Review.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    static void Fault(FoundationCredentialRestrictedRecoveryJointReviewCoordinator coordinator, Action<string> action) =>
        typeof(FoundationCredentialRestrictedRecoveryJointReviewCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, action);
    static void Corrupt(List<CustomerContext> contexts, string attack, DateTimeOffset now)
    {
        if (attack is "null-list" or "empty") { contexts.Clear(); return; }
        if (attack == "missing") { contexts.RemoveAt(1); return; }
        if (attack == "duplicate") { contexts.Add(contexts[0]); return; }
        if (attack == "extra") { contexts.Add(Clone(contexts[0], project: "not-reviewed")); return; }
        if (attack == "null-item") { contexts[0] = null!; return; }
        contexts[1] = attack switch
        {
            "actor" => Clone(contexts[1], actor: RestrictedRecoveryReviewFixture.Requester), "version" => Clone(contexts[1], version: contexts[1].RoleVersion + 1),
            "customer" => Clone(contexts[1], customer: Guid.NewGuid().ToString()), "cell" => Clone(contexts[1], cell: Guid.NewGuid().ToString()),
            "workload" => Clone(contexts[1], kind: PrincipalKind.Workload), "support" => Clone(contexts[1], support: "synthetic-support"),
            "capability" => Clone(contexts[1], capability: FoundationCapabilities.CredentialRecoveryRead), _ => Clone(contexts[1], expiry: now)
        };
    }
    static CustomerContext Clone(CustomerContext c, string? actor = null, string? customer = null, string? cell = null, string? project = null,
        long? version = null, PrincipalKind? kind = null, string? support = null, string? capability = null, DateTimeOffset? expiry = null) =>
        (CustomerContext)Activator.CreateInstance(typeof(CustomerContext), BindingFlags.NonPublic | BindingFlags.Instance, null,
            [customer ?? c.CustomerId, cell ?? c.CellId, actor ?? c.ActorKey, project ?? c.ProjectId, c.EnvironmentId, capability ?? c.Capability,
                version ?? c.RoleVersion, kind ?? c.Kind, expiry ?? c.ExpiresUtc, null, null, support], null)!;
    sealed class CallbackClock(DateTimeOffset now) : TimeProvider
    { internal Action? Next; public override DateTimeOffset GetUtcNow() { var action = Next; Next = null; action?.Invoke(); return now; } }
    sealed class PausedKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string keyId, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Requests); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customer, cell, keyId, cancellationToken); }
    }

    sealed class JointFixture : IDisposable
    {
        internal RestrictedRecoveryReviewFixture Review { get; } = new();
        internal CredentialFixture Cell => Review.Cell;
        internal CredentialTestKeys Destination => Review.DestinationKeys;
        internal string CheckpointRoot => Review.Child("restricted-joint-checkpoints");
        internal string CheckpointGuard => Path.Combine(CheckpointRoot, ".credential-recovery-checkpoint.guard");
        internal CredentialRestrictedRecoverySetCheckpointAuthority Checkpoints { get; private set; } = null!;
        internal CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity { get; private set; } = null!;
        internal CredentialRestrictedRecoverySetCheckpoint Head { get; private set; } = null!;
        internal long JointAdmissions => Review.Number("SELECT count(*) FROM " + Joint);
        internal async Task Initialize()
        {
            await Review.Initialize(); Checkpoints = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(CheckpointRoot, Review.Plan.SourceNamespace,
                Review.Stage.Set.Signature.KeyId, FoundationAuditAnchors.PublicKeyFingerprint(Review.Stage.Signer), Cell.Clock);
            Identity = Checkpoints.DescribeIdentity(); Head = await Checkpoints.AcceptNextAsync(Review.Stage.Set, Review.Stage.Set.Sha256, Review.Stage.Signer, Cell.Keys, 0, null);
        }
        internal FoundationCredentialRestrictedRecoveryJointReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null, FoundationStore? store = null) =>
            new(store ?? Review.Store, Checkpoints, Identity, new(keys ?? Destination, Cell.Clock), (store ?? Review.Store).GetCellInstanceIdentity().DatabaseInstanceId);
        internal async Task<FoundationCredentialRestrictedRecoveryJointVerification> Execute(FoundationCredentialRestrictedRecoveryStageReview review,
            FoundationCredentialRestrictedRecoveryJointReviewCoordinator? coordinator = null, IReadOnlyList<CustomerContext>? contexts = null, bool explicitContexts = false) =>
            await (coordinator ?? Coordinator()).ExecuteApprovedAsync(explicitContexts ? contexts! : contexts ?? await Review.Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest,
                Review.Plan, Review.Plan.Sha256, Review.Receipt.Sha256, Head.Sequence, Head.Sha256);
        internal Task<CredentialRestrictedRecoverySet> Capture(long generation) => Review.Stage.Source.CaptureRestrictedSignedRecoverySetAsync(Review.Plan.SourceNamespace, Review.Stage.Signer,
            Review.Stage.Set.Signature.KeyId, generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        internal Task<CredentialRestrictedRecoverySetCheckpoint> Accept(CredentialRestrictedRecoverySet set) => Checkpoints.AcceptNextAsync(set, set.Sha256, Review.Stage.Signer, Cell.Keys, Head.Sequence, Head.Sha256);
        internal async Task<FoundationCredentialRestrictedRecoveryJointVerification> ReadJoint(string id) => Review.Store.GetCredentialRestrictedRecoveryJointVerification(await Review.Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        internal void ClearKeys() => Review.ClearKeys();
        internal string Text(string sql) { using var db = Review.Open(); using var c = db.CreateCommand(); c.CommandText = sql; return (string)c.ExecuteScalar()!; }
        internal string Rows(string table, string? path = null)
        { using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Review.Database, Pooling = false }.ToString()); db.Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = c.ExecuteReader(); var rows = new List<object[]>(); while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return JsonSerializer.Serialize(rows, Json); }
        internal string[] ProtectedSnapshot() => Review.StageAndSourceSnapshot().Concat(Directory.EnumerateFileSystemEntries(CheckpointRoot, "*", SearchOption.AllDirectories).Prepend(CheckpointRoot).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData((Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm())) + ":" +
                (Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))))).ToArray();
        public void Dispose() => Review.Dispose();
    }
}
