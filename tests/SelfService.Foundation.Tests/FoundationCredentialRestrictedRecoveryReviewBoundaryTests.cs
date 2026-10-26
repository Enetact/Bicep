using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryReviewBoundaryTests
{
    const string Requests = "credential_restricted_recovery_stage_review_requests";
    const string Approvals = "credential_restricted_recovery_stage_review_approvals";
    const string Executions = "credential_restricted_recovery_stage_review_executions";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Exact_present_and_absence_scope_review_is_immutable_private_metadata_and_never_a_promotion_grant()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var before = f.StageSnapshot(); f.ClearKeys();
        var prepared = f.Store.PrepareCredentialRestrictedRecoveryStageReview((await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).Reverse().ToArray(),
            f.Plan, f.Receipt, BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5));
        var approved = await f.Approve(prepared); var read = await f.Read(prepared.Id);
        Assert.Equal("Prepared", prepared.State); Assert.Equal("Approved", approved.State);
        Assert.Equal(JsonSerializer.Serialize(approved, Json), JsonSerializer.Serialize(read, Json));
        Assert.Equal(new[] { "default/dev", "other/qa" }, read.Target.Scopes.Select(s => s.ProjectId + "/" + s.EnvironmentId));
        Assert.Equal("FoundationBoundDispatchOnly", read.Target.AccessProfile);
        Assert.Equal(f.Receipt.CompletionProofSha256, read.Target.CompletionProofSha256);
        Assert.Equal(f.Plan.Sha256, read.Target.PlanSha256); Assert.Equal(f.Receipt.Sha256, read.Target.ReceiptSha256);
        Assert.Throws<NotSupportedException>(() => ((IList<FoundationCredentialRecoveryScope>)read.Target.Scopes).Clear());
        var metadata = JsonSerializer.Serialize(read, Json);
        foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.Stage.Positive.AuthProfileId, f.Stage.Absent.AuthProfileId, "\"ciphertext\":", "\"tag\":", "\"protectedKey\":" })
            Assert.DoesNotContain(forbidden, metadata, StringComparison.OrdinalIgnoreCase);
        foreach (var scope in read.Target.Scopes)
        {
            var audit = f.Store.ListAudit(f.Customer, BoundaryFixture.Auditor, scope.ProjectId, scope.EnvironmentId, 1000).Where(e => e.TargetId == read.Id).ToArray();
            Assert.Contains(audit, e => e.Action == "CredentialRestrictedRecoveryReviewPrepared");
            Assert.Contains(audit, e => e.Action == "CredentialRestrictedRecoveryReviewApproved");
            Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, BoundaryFixture.Auditor, scope.ProjectId, scope.EnvironmentId));
        }
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Stage.Destination.Requests); Assert.Equal(before, f.StageSnapshot());
    }

    [Theory]
    [InlineData("missing")] [InlineData("extra")] [InlineData("duplicate")] [InlineData("empty")] [InlineData("too-many")]
    [InlineData("actor")] [InlineData("customer")] [InlineData("cell")] [InlineData("capability")] [InlineData("version")]
    [InlineData("expired")] [InlineData("workload")] [InlineData("support")] [InlineData("project-case")] [InlineData("environment")]
    public async Task Preparation_requires_one_exact_current_human_context_for_every_authenticated_scope(string attack)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToList();
        Corrupt(contexts, attack, f); var before = f.StageSnapshot(); var audits = f.Number("SELECT count(*) FROM audit"); f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRestrictedRecoveryStageReview(contexts, f.Plan, f.Receipt, BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Requests)); Assert.Equal(audits, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(before, f.StageSnapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Stage.Destination.Requests);
    }

    [Theory]
    [InlineData(false, "missing")] [InlineData(false, "duplicate")] [InlineData(false, "actor")] [InlineData(false, "workload")]
    [InlineData(true, "missing")] [InlineData(true, "duplicate")] [InlineData(true, "actor")] [InlineData(true, "workload")]
    public async Task Approval_and_execution_cannot_omit_the_absence_only_scope_or_mix_human_authority(bool execution, string attack)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = execution ? await f.Approved() : await f.Prepare();
        var contexts = (await f.Contexts(execution ? FoundationCapabilities.CredentialRecoveryVerify : FoundationCapabilities.CredentialRecoveryApprove)).ToList();
        Corrupt(contexts, attack, f); var before = f.StageSnapshot(); f.ClearKeys();
        if (execution) await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        else Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRestrictedRecoveryStageReview(contexts, review.Id, review.Digest));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Executions)); Assert.Equal(before, f.StageSnapshot()); Assert.Empty(f.Stage.Destination.Requests);
        if (!execution) Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
    }

    [Theory]
    [InlineData("requester-disabled")] [InlineData("requester-role")] [InlineData("requester-version")]
    [InlineData("approver-disabled")] [InlineData("approver-role")] [InlineData("approver-version")]
    public async Task A_changed_grant_in_just_the_absence_scope_invalidates_the_entire_approval(string attack)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Prepare(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        var actor = attack.StartsWith("requester", StringComparison.Ordinal) ? BoundaryFixture.Operator : BoundaryFixture.Approver;
        var role = actor == BoundaryFixture.Operator ? "Operator" : "Approver";
        f.Store.SetMembership(f.Customer, "other-admin", actor, "other", "qa", [attack.EndsWith("role", StringComparison.Ordinal) ? "Viewer" : role], !attack.EndsWith("disabled", StringComparison.Ordinal));
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRestrictedRecoveryStageReview(contexts, review.Id, review.Digest));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); Assert.Equal("Prepared", (await f.Read(review.Id)).State);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("support-only")] [InlineData("support-mixed")]
    public async Task Raw_role_json_cannot_expand_an_earlier_valid_context(string attack)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var roles = attack switch { "unknown" => "[\"Operator\",\"UnknownRole\"]", "duplicate" => "[\"Operator\",\"Operator\"]", "support-only" => "[\"SupportDelegate\"]", _ => "[\"Operator\",\"SupportDelegate\"]" };
        f.Sql("UPDATE memberships SET roles=$roles WHERE actor_key=$actor AND project_id='other'", ("$roles", roles), ("$actor", BoundaryFixture.Operator));
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRestrictedRecoveryStageReview(contexts, f.Plan, f.Receipt, BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Requests));
    }

    [Theory]
    [InlineData("Operator", FoundationCapabilities.CredentialRecoveryApprove)]
    [InlineData("Approver", FoundationCapabilities.CredentialRecoveryPrepare)]
    [InlineData("Viewer", FoundationCapabilities.CredentialRecoveryRead)]
    [InlineData("CustomerAdmin", FoundationCapabilities.CredentialRecoveryVerify)]
    public async Task Signed_roles_and_current_membership_must_both_authorize_the_requested_capability(string signedRole, string capability)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); f.Grant(BoundaryFixture.Operator, ["Operator", "Approver", "Auditor", "CustomerAdmin"]);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => f.Contexts(capability, IdentityTestIssuer.Subject, [signedRole]));
    }

    [WindowsCredentialFact]
    public async Task A_human_with_both_roles_cannot_self_approve_or_execute_as_the_approver()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); f.Grant(BoundaryFixture.Operator, ["Operator", "Approver"]);
        var prepared = await f.Prepare(); var own = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, IdentityTestIssuer.Subject, ["Operator", "Approver"]);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRestrictedRecoveryStageReview(own, prepared.Id, prepared.Digest)).Code);
        f.Grant(BoundaryFixture.Approver, ["Approver", "Operator"]); var approved = await f.Approve(prepared);
        var executor = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, IdentityTestIssuer.OtherSubject, ["Approver", "Operator"]); f.ClearKeys();
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Coordinator().ExecuteAsync(executor, approved.Id, approved.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Executions)); Assert.Empty(f.Stage.Destination.Requests);
    }

    [Theory]
    [InlineData("policy")] [InlineData("instance")] [InlineData("expiry")]
    public async Task An_exact_approval_cannot_revive_a_stale_request(string changed)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Prepare();
        if (changed == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1");
        if (changed == "instance") f.Sql("UPDATE cell SET database_instance_id=$id", ("$id", Guid.NewGuid().ToString("N")));
        if (changed == "expiry") f.Cell.Clock.Now = review.ExpiresAt;
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRestrictedRecoveryStageReview(contexts, review.Id, review.Digest));
        Assert.Equal("Prepared", (await f.Read(review.Id)).State); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
    }

    [WindowsCredentialFact]
    public async Task Request_expiry_is_bounded_by_the_shortest_context_and_approval_has_its_own_pinned_window()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToArray();
        contexts[1] = Clone(contexts[1], expiry: f.Cell.Clock.Now.AddMinutes(2));
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRestrictedRecoveryStageReview(contexts, f.Plan, f.Receipt, BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(3)));
        var review = f.Store.PrepareCredentialRestrictedRecoveryStageReview(contexts, f.Plan, f.Receipt, BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(1));
        Assert.Equal(contexts[1].ExpiresUtc, review.RequesterExpiresAt);
        var approval = f.Store.ApproveCredentialRestrictedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, lifetime: TimeSpan.FromSeconds(40)), review.Id, review.Digest);
        Assert.Equal(f.Cell.Clock.Now.AddSeconds(40), approval.ApproverExpiresAt);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("disabled-scope")] [InlineData("stale-version")]
    public async Task Partial_or_revoked_read_authority_cannot_leak_the_other_scope_history(string attack)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Prepare(); var readers = (await f.Contexts(FoundationCapabilities.CredentialRecoveryRead)).ToList();
        if (attack == "missing") readers.RemoveAt(1); else if (attack == "duplicate") readers.Add(readers[0]);
        else f.Store.SetMembership(f.Customer, "other-admin", BoundaryFixture.Auditor, "other", "qa", ["Auditor"], attack != "disabled-scope");
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRestrictedRecoveryStageReview(readers, review.Id));
        if (attack == "stale-version") Assert.Equal(review.Id, (await f.Read(review.Id)).Id);
    }

    [WindowsCredentialFact]
    public async Task Historical_read_requires_current_reader_grants_but_does_not_restore_old_actor_authority()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Approved();
        f.Grant(BoundaryFixture.Operator, ["Viewer"]); f.Grant(BoundaryFixture.Approver, ["Viewer"]); f.Cell.Clock.Now = review.ExpiresAt.AddMinutes(1); f.ClearKeys();
        var history = await f.Read(review.Id); Assert.Equal(review.Digest, history.Digest); Assert.Equal("Approved", history.State); Assert.Null(history.Execution);
        Assert.Equal(review.ApproverKey, history.ApproverKey); Assert.Empty(f.Stage.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("plan")] [InlineData("receipt")]
    public async Task A_second_genuine_restricted_stage_is_not_a_substitute_for_the_exact_reviewed_pair(string changed)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var selected = CredentialRestrictedRecoverySets.Verify(f.Stage.Set, CredentialRestrictedRecoverySets.Authority(f.Stage.Set), f.Stage.Signer, f.Plan.SourceNamespace, f.Cell.Clock);
        var other = CredentialRestrictedRecoveryStaging.PreparePlan(selected, CredentialRestrictedRecoverySets.Authority(f.Stage.Set), f.Plan.SourceNamespace,
            f.Child("other-real-stage"), f.Plan.DestinationKey, "Different exact owner selection", f.Cell.Clock.Now.AddMinutes(15), f.Cell.Clock);
        var otherReceipt = await new CredentialRestrictedRecoveryStageExecutor(f.Cell.Keys, f.Stage.Destination, f.Cell.Clock).StageAsync(f.Stage.Set, other, other.Sha256, f.Stage.Signer);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare); f.ClearKeys(); var before = f.StageSnapshot();
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRestrictedRecoveryStageReview(contexts, changed == "plan" ? other : f.Plan, changed == "receipt" ? otherReceipt : f.Receipt,
            BoundaryFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Requests)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Stage.Destination.Requests); Assert.Equal(before, f.StageSnapshot());
    }

    [Theory]
    [InlineData("accessProfile")] [InlineData("completionProofSha256")] [InlineData("scopes")] [InlineData("destinationKey")]
    public async Task A_rehashed_public_target_cannot_replace_the_original_request_digest(string changed)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Prepare();
        var node = JsonNode.Parse(f.Text("SELECT target FROM " + Requests))!.AsObject();
        if (changed == "accessProfile") node.Remove(changed);
        else if (changed == "completionProofSha256") node[changed] = new string('f', 64);
        else if (changed == "scopes") node[changed]!.AsArray().RemoveAt(1);
        else node[changed]!["commitmentSha256"] = new string('e', 64);
        var json = node.ToJsonString(Json); var sha = Hash(Encoding.UTF8.GetBytes(json));
        var guard = f.Text("SELECT sql FROM sqlite_master WHERE name='credential_restricted_recovery_review_requests_immutable'");
        f.Sql("DROP TRIGGER credential_restricted_recovery_review_requests_immutable;UPDATE " + Requests + " SET target=$target,target_sha256=$sha WHERE id=$id;" + guard, ("$target", json), ("$sha", sha), ("$id", review.Id));
        f.ClearKeys(); var readers = await f.ReaderContexts(); Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRestrictedRecoveryStageReview(readers, review.Id));
        Assert.Empty(f.Stage.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Executions));
    }

    [Theory]
    [InlineData("ParseRecoveryTarget")] [InlineData("ParseScopedRecoveryTarget")]
    public async Task Existing_general_review_decoders_do_not_adopt_restricted_target_documents(string decoder)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Prepare(); var text = JsonSerializer.Serialize(review.Target, Json); f.ClearKeys();
        var failure = Assert.Throws<TargetInvocationException>(() => typeof(FoundationStore).GetMethod(decoder, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text]));
        Assert.IsType<FoundationStoreException>(failure.InnerException); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Stage.Destination.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_requests"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_requests"));
    }

    [WindowsCredentialFact]
    public async Task Absence_only_union_is_real_authority_while_empty_stages_cannot_gain_vacuous_review()
    {
        using var absent = new BoundaryFixture("absence"); await absent.Initialize(); var review = await absent.Approved();
        Assert.Equal(0, review.Target.ProfileCount); Assert.Equal(1, review.Target.AbsenceProofCount); Assert.Equal("other", Assert.Single(review.Target.Scopes).ProjectId);
        var result = await absent.Execute(review); Assert.Equal("Succeeded", result.Execution!.State); Assert.Equal("HeldRestrictedStageVerified", result.Execution.Result!.Status);
        Assert.False(result.Execution.Result.PromotionAuthorized); Assert.False(result.Execution.Result.LiveAdoptionAuthorized); Assert.False(result.Execution.Result.ActivationPerformed);
        using var empty = new BoundaryFixture("empty"); await empty.Initialize(); empty.ClearKeys();
        var context = await empty.Contexts(FoundationCapabilities.CredentialRecoveryPrepare, allFixtureScopes: true);
        Assert.Equal("RestrictedRecoveryStageReviewEmptySource", Assert.Throws<FoundationStoreException>(() => empty.Store.PrepareCredentialRestrictedRecoveryStageReview(context,
            empty.Plan, empty.Receipt, BoundaryFixture.Purpose, empty.Cell.Clock.Now.AddMinutes(5))).Code);
        Assert.Equal(0, empty.Number("SELECT count(*) FROM " + Requests)); Assert.Empty(empty.Stage.Destination.Requests);
    }

    [Theory]
    [InlineData("Prepared")] [InlineData("Approved")] [InlineData("OutcomeUnknown")] [InlineData("Succeeded")]
    public async Task Restore_retains_exact_history_and_quarantines_every_unfinished_review_without_key_use(string state)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = state == "Prepared" ? await f.Prepare() : await f.Approved();
        if (state == "OutcomeUnknown") { var coordinator = f.Coordinator(); Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("Synthetic bounded interruption."); }); review = await f.Execute(review, coordinator); }
        if (state == "Succeeded") review = await f.Execute(review);
        var executions = f.Rows(Executions); var approvals = f.Rows(Approvals); var backup = f.Store.CreateBackup(f.Child("review-backup.sqlite"));
        var source = File.ReadAllBytes(backup.DatabasePath); var stage = f.StageSnapshot(); f.ClearKeys();
        var restoredPath = f.Child("restored-review.sqlite"); var restored = FoundationStore.RestoreBackup(backup, restoredPath, f.Cell.Clock);
        var history = restored.GetCredentialRestrictedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: restored), review.Id);
        Assert.Equal(state == "Succeeded" ? "Approved" : "Quarantined", history.State); Assert.Equal(review.Execution, history.Execution);
        Assert.NotEqual(review.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal(executions, f.Rows(Executions, restoredPath)); Assert.Equal(approvals, f.Rows(Approvals, restoredPath));
        var executors = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, store: restored);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator(restored).ExecuteAsync(executors, history.Id, history.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(source, File.ReadAllBytes(backup.DatabasePath)); Assert.Equal(stage, f.StageSnapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Stage.Destination.Requests);
        foreach (var scope in history.Target.Scopes) Assert.True(restored.VerifyAuditIntegrity(f.Customer, BoundaryFixture.Auditor, scope.ProjectId, scope.EnvironmentId));
        Assert.Equal(1, f.Number("SELECT count(*) FROM recovery_execution_holds WHERE database_instance_id=(SELECT database_instance_id FROM cell WHERE singleton=1)", restoredPath));
    }

    [WindowsCredentialFact]
    public async Task Repeated_restore_never_reauthorizes_old_approval_even_when_destination_material_still_matches()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Approved();
        var first = FoundationStore.RestoreBackup(f.Store.CreateBackup(f.Child("first-backup.sqlite")), f.Child("first-restore.sqlite"), f.Cell.Clock);
        var second = FoundationStore.RestoreBackup(first.CreateBackup(f.Child("second-backup.sqlite")), f.Child("second-restore.sqlite"), f.Cell.Clock);
        Assert.NotEqual(first.GetCellInstanceIdentity().DatabaseInstanceId, second.GetCellInstanceIdentity().DatabaseInstanceId); f.ClearKeys();
        var read = second.GetCredentialRestrictedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: second), review.Id);
        Assert.Equal("Quarantined", read.State);
        var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, store: second);
        Assert.Throws<FoundationStoreException>(() => second.ApproveCredentialRestrictedRecoveryStageReview(approvers, read.Id, read.Digest));
        Assert.Empty(f.Stage.Destination.Requests); Assert.Equal("Approved", (await f.Read(review.Id)).State);
    }

    [WindowsCredentialFact]
    public async Task Genuine_audit_capacity_failure_during_restore_preserves_source_and_creates_no_recovered_target()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); await f.Prepare();
        var backup = FoundationTestAuditCapacity.FillSnapshot(f.Store.CreateBackup(f.Child("capacity-backup.sqlite")), f.Cell.Clock, availableAuditEvents: 1);
        var original = File.ReadAllBytes(backup.DatabasePath); var before = f.StageSnapshot(); var target = f.Child("capacity-restore.sqlite"); f.ClearKeys();
        Assert.Equal("AuditCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup, target, f.Cell.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.Equal(original, File.ReadAllBytes(backup.DatabasePath)); Assert.Equal(before, f.StageSnapshot()); Assert.Empty(f.Stage.Destination.Requests);
    }

    [Theory]
    [InlineData("table")] [InlineData("index")] [InlineData("trigger")]
    public async Task Correct_byte_pins_cannot_repair_missing_schema13_authority_before_restore_creation(string missing)
    {
        using var f = new BoundaryFixture(); await f.Initialize(); await f.Prepare(); var backup = f.Store.CreateBackup(f.Child("malformed-backup.sqlite"));
        using (var db = Open(backup.DatabasePath))
        { using var c = db.CreateCommand(); c.CommandText = missing switch { "table" => "DROP TABLE " + Executions, "index" => "DROP INDEX credential_restricted_recovery_review_admission", _ => "DROP TRIGGER credential_restricted_recovery_review_success_immutable" }; c.ExecuteNonQuery(); }
        var bytes = File.ReadAllBytes(backup.DatabasePath); backup = backup with { Sha256 = Hash(bytes), Length = bytes.Length }; var target = f.Child("malformed-restore.sqlite");
        Assert.Equal("BackupIntegrityFailed", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup, target, f.Cell.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.Equal(bytes, File.ReadAllBytes(backup.DatabasePath));
    }

    [WindowsCredentialFact]
    public async Task A_store_clock_side_effect_after_verification_cannot_publish_success_against_changed_completion_bytes()
    {
        using var f = new BoundaryFixture(); await f.Initialize(); var review = await f.Approved(); var clock = new CallbackClock(f.Cell.Clock.Now);
        var store = new FoundationStore(f.Database, f.Cell.Scope.CellId, f.Customer, clock); var coordinator = f.Coordinator(store);
        var completion = Path.Combine(f.Stage.StageRoot, "completion.json"); var old = File.ReadAllBytes(completion); var changed = false;
        Fault(coordinator, phase => { if (phase == "AfterVerification") clock.Next = () => { File.AppendAllText(completion, " "); changed = true; }; });
        f.ClearKeys(); var result = await f.Execute(review, coordinator);
        Assert.True(changed); Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result);
        Assert.Equal(0, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryReviewCompleted'"));
        Assert.Equal(old.Concat(new byte[] { 32 }).ToArray(), File.ReadAllBytes(completion)); Assert.Single(f.Stage.Destination.Requests);
    }

    static void Corrupt(List<CustomerContext> contexts, string attack, BoundaryFixture f)
    {
        if (attack == "missing") { contexts.RemoveAt(1); return; }
        if (attack == "empty") { contexts.Clear(); return; }
        if (attack == "extra") { contexts.Add(Clone(contexts[0], project: "not-reviewed")); return; }
        if (attack == "duplicate") { contexts.Add(contexts[0]); return; }
        if (attack == "too-many") { var first = contexts[0]; contexts.Clear(); contexts.AddRange(Enumerable.Repeat(first, 257)); return; }
        contexts[1] = attack switch
        {
            "actor" => Clone(contexts[1], actor: BoundaryFixture.Auditor), "customer" => Clone(contexts[1], customer: Guid.NewGuid().ToString()),
            "cell" => Clone(contexts[1], cell: Guid.NewGuid().ToString()), "capability" => Clone(contexts[1], capability: FoundationCapabilities.CredentialMaintenancePrepare),
            "version" => Clone(contexts[1], version: contexts[1].RoleVersion + 1), "expired" => Clone(contexts[1], expiry: f.Cell.Clock.Now),
            "workload" => Clone(contexts[1], kind: PrincipalKind.Workload), "support" => Clone(contexts[1], support: "synthetic-support"),
            "environment" => Clone(contexts[1], environment: "prod"), _ => Clone(contexts[1], project: "OTHER")
        };
    }
    static CustomerContext Clone(CustomerContext original, string? actor = null, string? customer = null, string? cell = null, string? capability = null,
        string? project = null, string? environment = null, long? version = null, DateTimeOffset? expiry = null, PrincipalKind? kind = null, string? support = null) =>
        (CustomerContext)Activator.CreateInstance(typeof(CustomerContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [customer ?? original.CustomerId, cell ?? original.CellId, actor ?? original.ActorKey, project ?? original.ProjectId, environment ?? original.EnvironmentId,
                capability ?? original.Capability, version ?? original.RoleVersion, kind ?? original.Kind, expiry ?? original.ExpiresUtc, null, null, support], null)!;
    static void Fault(FoundationCredentialRestrictedRecoveryStageReviewCoordinator coordinator, Action<string> action) =>
        typeof(FoundationCredentialRestrictedRecoveryStageReviewCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator, action);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static SqliteConnection Open(string path)
    { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    sealed class CallbackClock(DateTimeOffset now) : TimeProvider
    { public Action? Next { get; set; } public override DateTimeOffset GetUtcNow() { var action = Next; Next = null; action?.Invoke(); return now; } }

    sealed class BoundaryFixture : IDisposable
    {
        internal const string Purpose = "Synthetic exact restricted held-stage review";
        internal const string Operator = MaintenanceExecutorFixture.Operator, Approver = MaintenanceExecutorFixture.Approver, Auditor = MaintenanceExecutorFixture.Auditor;
        readonly IdentityTestIssuer issuer = new();
        internal RestrictedStageFixture Stage { get; }
        internal CredentialFixture Cell => Stage.Cell;
        internal CredentialRestrictedRecoveryStagePlan Plan => Stage.Plan;
        internal CredentialRestrictedRecoveryStageReceipt Receipt => Stage.Receipt;
        internal FoundationStore Store { get; }
        internal string Customer => Cell.Scope.CustomerId;
        internal string Database => Child("restricted-review.sqlite");
        internal BoundaryFixture(string mode = "mixed")
        {
            Stage = new(mode: mode); Store = new(Database, Cell.Scope.CellId, Customer, Cell.Clock); Store.BootstrapAdministrator("admin");
            Store.SetMembership(Customer, "admin", "approver", "default", "dev", ["Approver"]);
            var onboarding = Store.PrepareScopeOnboarding(Customer, "admin", "other", "qa", "other-admin", Purpose);
            Store.ApproveAdministrativePlan(Customer, "approver", onboarding.Id, onboarding.Digest); Store.ExecuteScopeOnboarding(Customer, "admin", onboarding.Id, onboarding.Digest);
            Grant(Operator, ["Operator"]); Grant(Approver, ["Approver"]); Grant(Auditor, ["Auditor"]);
        }
        internal Task Initialize() => Stage.Initialize();
        internal string Child(string name) => Path.Combine(Cell.Root, name);
        internal void Grant(string actor, string[] roles)
        { Store.SetMembership(Customer, "admin", actor, "default", "dev", roles); Store.SetMembership(Customer, "other-admin", actor, "other", "qa", roles); }
        internal async Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? subject = null, string[]? signedRoles = null, TimeSpan? lifetime = null,
            FoundationStore? store = null, bool allFixtureScopes = false)
        {
            store ??= Store; var role = capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator";
            subject ??= role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject); claims["roles"] = signedRoles ?? [role];
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims)); var resolver = new CustomerContextResolver(options, new Memberships(store), Cell.Clock);
            var scopes = allFixtureScopes ? new[] { new FoundationCredentialRecoveryScope("default", "dev"), new("other", "qa") } : Plan.AffectedScopes;
            return scopes.Select(s => resolver.Require(principal, Customer, s.ProjectId, s.EnvironmentId, capability)).ToArray();
        }
        internal Task<IReadOnlyList<CustomerContext>> ReaderContexts() => Contexts(FoundationCapabilities.CredentialRecoveryRead);
        internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Prepare() => Store.PrepareCredentialRestrictedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Receipt, Purpose, Cell.Clock.Now.AddMinutes(5));
        internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Approve(FoundationCredentialRestrictedRecoveryStageReview review) => Store.ApproveCredentialRestrictedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Approved() => await Approve(await Prepare());
        internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Read(string id) => Store.GetCredentialRestrictedRecoveryStageReview(await ReaderContexts(), id);
        internal FoundationCredentialRestrictedRecoveryStageReviewCoordinator Coordinator(FoundationStore? store = null)
        { store ??= Store; return new(store, new CredentialRestrictedRecoveryStageVerifier(Stage.Destination, Cell.Clock), store.GetCellInstanceIdentity().DatabaseInstanceId); }
        internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Execute(FoundationCredentialRestrictedRecoveryStageReview review, FoundationCredentialRestrictedRecoveryStageReviewCoordinator? coordinator = null) =>
            await (coordinator ?? Coordinator()).ExecuteAsync(await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest, Plan, Plan.Sha256, Receipt.Sha256);
        internal void ClearKeys() { Cell.Keys.Requests.Clear(); Stage.Destination.Requests.Clear(); }
        internal long Number(string sql, string? path = null) { using var db = Open(path ?? Database); using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
        internal string Text(string sql) { using var db = Open(Database); using var c = db.CreateCommand(); c.CommandText = sql; return (string)c.ExecuteScalar()!; }
        internal void Sql(string sql, params (string Name, object Value)[] parameters) { using var db = Open(Database); using var c = db.CreateCommand(); c.CommandText = sql; foreach (var p in parameters) c.Parameters.AddWithValue(p.Name, p.Value); c.ExecuteNonQuery(); }
        internal string Rows(string table, string? path = null)
        { using var db = Open(path ?? Database); using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = c.ExecuteReader(); var rows = new List<object[]>(); while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return JsonSerializer.Serialize(rows, Json); }
        internal string[] StageSnapshot() => Directory.EnumerateFileSystemEntries(Stage.StageRoot, "*", SearchOption.AllDirectories).Prepend(Stage.StageRoot).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Stage.StageRoot, p) + ":" + Hash((Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorBinaryForm()) + ":" + (Directory.Exists(p) ? "directory" : Hash(File.ReadAllBytes(p)))).ToArray();
        public void Dispose() { issuer.Dispose(); Stage.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
