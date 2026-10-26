using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryAdoptionBoundaryTests
{
    const string Intents = "credential_restricted_recovery_adoption_intents";
    const string Approvals = "credential_restricted_recovery_adoption_approvals";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Approved_intent_pins_exact_restored_lineage_and_complete_union_without_creating_any_changing_authority()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var before = f.ProtectedSnapshot();
        var bindings = Rows(f.Database, "credential_runtime_bindings"); var hold = (await f.Hold()).Hold!; var activation = f.Activation.ReadCurrent(); f.ClearKeys();
        var prepared = await f.Prepare(); var approved = await f.Approve(prepared); var read = await f.Read(prepared.Id);
        Assert.Equal("Prepared", prepared.State); Assert.Equal("Approved", approved.State); Assert.Equal(JsonSerializer.Serialize(approved, Json), JsonSerializer.Serialize(read, Json));
        Assert.Equal("FoundationBoundDispatchOnly", read.AccessProfile); Assert.Equal("ProvisionFreshRestrictedNamespacesForRestoredInstance", read.Operation);
        Assert.Equal(new[] { "default/dev", "other/qa" }, read.Target.RecoveryTarget.StageTarget.Scopes.Select(s => s.ProjectId + "/" + s.EnvironmentId));
        Assert.Equal(f.SourceBinding, read.Target.SourceRuntimeBinding); Assert.Equal(hold, read.Target.RecoveryHold);
        Assert.Equal(f.SourceInstance, read.Target.RecoveryHold.SourceDatabaseInstanceId); Assert.Equal(f.Instance, read.DatabaseInstanceId);
        Assert.True(read.Target.RuntimeBindingRequired); Assert.Null(read.Target.CurrentRuntimeBindingSha256); Assert.Equal(1, read.Target.RuntimeBindingHistoryCount);
        Assert.Equal(f.Receipt.CompletionProofSha256, read.Target.RecoveryTarget.StageTarget.CompletionProofSha256);
        Assert.NotEqual(read.RequesterKey, read.Approval!.ApproverKey); Assert.Equal(read.Digest, read.Approval.IntentDigest);
        Assert.False(read.ExecutionAdmissionAvailable); Assert.False(read.AdoptionAuthorized); Assert.False(read.ActivationPerformed); Assert.False(read.RecoveryHoldReleased);
        Assert.False(read.FreshDestinationReservationPerformed); Assert.False(read.SourceHostFenced); Assert.False(read.ExternalExecutionAuthorized); Assert.False(read.IndependentKeyBackupVerified);
        Assert.False(read.Approval.ExecutionAdmissionAvailable); Assert.False(read.Approval.AdoptionAuthorized); Assert.False(read.Target.Selection.FreshDestinationReservationPerformed);
        Assert.False(Directory.Exists(f.FutureVault)); Assert.False(Directory.Exists(f.FutureRollback)); Assert.Equal(activation, f.Activation.ReadCurrent());
        Assert.Equal(bindings, Rows(f.Database, "credential_runtime_bindings")); Assert.Equal(hold, (await f.Hold()).Hold); Assert.Equal(before, f.ProtectedSnapshot());
        Assert.Equal(2, f.Destination.Requests.Count); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Throws<NotSupportedException>(() => ((IList<FoundationCredentialRecoveryScope>)read.Target.RecoveryTarget.StageTarget.Scopes).Clear());
        var metadata = JsonSerializer.Serialize(read, Json);
        foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.FutureVault, f.FutureRollback, f.Stage.Positive.AuthProfileId, "\"tag\":", "\"ciphertext\":" })
            Assert.DoesNotContain(forbidden, metadata, StringComparison.OrdinalIgnoreCase);
        foreach (var scope in read.Target.RecoveryTarget.StageTarget.Scopes)
            Assert.True(f.RestoredStore.VerifyAuditIntegrity(f.Review.Customer, MaintenanceExecutorFixture.Auditor, scope.ProjectId, scope.EnvironmentId));
    }

    [Theory]
    [InlineData("null-list")] [InlineData("null-item")] [InlineData("empty")] [InlineData("missing")]
    [InlineData("extra")] [InlineData("duplicate")] [InlineData("actor")] [InlineData("version")]
    [InlineData("customer")] [InlineData("cell")] [InlineData("workload")] [InlineData("support")]
    [InlineData("capability")] [InlineData("expired")]
    public async Task Preparation_requires_the_exact_current_human_union_before_private_key_access_or_rows(string attack)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToList();
        Corrupt(contexts, attack, f.Clock.Now); var before = f.ProtectedSnapshot(); var audits = f.Number("SELECT count(*) FROM audit"); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().PrepareAsync(attack == "null-list" ? null! : contexts, f.Plan, f.Plan.Sha256, f.Receipt.Sha256,
            f.Head.Sequence, f.Head.Sha256, "Synthetic rejected intent", f.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Equal(audits, f.Number("SELECT count(*) FROM audit"));
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("actor")] [InlineData("version")] [InlineData("workload")] [InlineData("capability")]
    public async Task Approval_cannot_drop_an_absence_scope_or_mix_principals(string attack)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove)).ToList();
        Corrupt(contexts, attack, f.Clock.Now); var before = f.ProtectedSnapshot(); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent, contexts: contexts));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); Assert.Equal("Prepared", (await f.Read(intent.Id)).State);
        Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.ProtectedSnapshot()); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("requester-disabled")] [InlineData("requester-role")] [InlineData("requester-version")]
    [InlineData("approver-disabled")] [InlineData("approver-role")] [InlineData("approver-version")]
    public async Task A_changed_grant_in_the_absence_scope_invalidates_the_whole_approval(string change)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        var actor = change.StartsWith("requester", StringComparison.Ordinal) ? RestrictedRecoveryReviewFixture.Requester : RestrictedRecoveryReviewFixture.Approver;
        var role = actor == RestrictedRecoveryReviewFixture.Requester ? "Operator" : "Approver";
        f.RestoredStore.SetMembership(f.Review.Customer, "other-admin", actor, "other", "qa", [change.EndsWith("role", StringComparison.Ordinal) ? "Viewer" : role], !change.EndsWith("disabled", StringComparison.Ordinal));
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent, contexts: contexts));
        Assert.Null((await f.Read(intent.Id)).Approval); Assert.Empty(f.Destination.Requests); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("support-only")] [InlineData("support-mixed")]
    public async Task Raw_membership_roles_cannot_expand_previously_issued_human_authority(string attack)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var roles = attack switch { "unknown" => "[\"Operator\",\"Unrecognized\"]", "duplicate" => "[\"Operator\",\"Operator\"]", "support-only" => "[\"SupportDelegate\"]", _ => "[\"Operator\",\"SupportDelegate\"]" };
        Sql(f.Database, "UPDATE memberships SET roles=$roles WHERE actor_key=$actor AND project_id='other'", ("$roles", roles), ("$actor", RestrictedRecoveryReviewFixture.Requester));
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(contexts: contexts)); Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents));
    }

    [WindowsCredentialFact]
    public async Task Same_human_with_both_signed_roles_still_cannot_self_approve_the_intent()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); Grant(f, RestrictedRecoveryReviewFixture.Requester, ["Operator", "Approver"]);
        var intent = await f.Prepare(); var own = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, actor: RestrictedRecoveryReviewFixture.Requester, signedRole: "Approver");
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent, contexts: own)); Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
    }

    [Theory]
    [InlineData("Operator", FoundationCapabilities.CredentialRecoveryApprove)]
    [InlineData("Approver", FoundationCapabilities.CredentialRecoveryPrepare)]
    [InlineData("Viewer", FoundationCapabilities.CredentialRecoveryRead)]
    public async Task Current_database_roles_do_not_override_the_signed_role_intersection(string signedRole, string capability)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); Grant(f, RestrictedRecoveryReviewFixture.Requester, ["Operator", "Approver", "Auditor"]);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => f.Contexts(capability, actor: RestrictedRecoveryReviewFixture.Requester, signedRole: signedRole));
    }

    [WindowsCredentialFact]
    public async Task Never_bound_restore_has_a_real_hold_but_cannot_invent_a_source_runtime_lineage()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(bindSource: false); await f.Initialize(); var hold = (await f.Hold()).Hold!; Assert.Equal("RestoredSnapshot", hold.Origin);
        Assert.NotNull(hold.SourceDatabaseInstanceId); Assert.False(f.RestoredStore.GetCredentialRuntimeBindingStatus().BindingRequired); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare()); Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents)); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("absence")] [InlineData("empty")]
    public async Task Absence_only_intent_needs_real_scope_authority_and_empty_union_cannot_gain_an_approval(string mode)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(mode: mode); await f.Initialize(); f.ClearKeys();
        if (mode == "empty")
        { await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare()); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Empty(f.Destination.Requests); }
        else
        {
            var approved = await f.Approve(await f.Prepare()); Assert.Equal("Approved", approved.State);
            var target = approved.Target.RecoveryTarget.StageTarget; Assert.Equal(0, target.ProfileCount); Assert.Equal(1, target.AbsenceProofCount);
            Assert.Equal("other", Assert.Single(target.Scopes).ProjectId); Assert.False(approved.AdoptionAuthorized); Assert.Equal(2, f.Destination.Requests.Count);
        }
        AssertNoDestination(f);
    }

    [WindowsCredentialFact]
    public async Task Genuine_same_customer_stage_from_another_namespace_cannot_replace_the_historical_source_binding()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); using var other = new RestrictedStageFixture(); await other.Initialize();
        var cp = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(f.Review.Child("other-source-checkpoint"), other.Plan.SourceNamespace, other.Set.Signature.KeyId,
            FoundationAuditAnchors.PublicKeyFingerprint(other.Signer), other.Cell.Clock);
        var head = await cp.AcceptNextAsync(other.Set, other.Set.Sha256, other.Signer, other.Cell.Keys, 0, null);
        var coordinator = new FoundationCredentialRestrictedRecoveryAdoptionCoordinator(f.RestoredStore, f.Activation, f.ActivationIdentity, f.ActivationHead.Generation, f.ActivationHead.Sha256,
            cp, cp.DescribeIdentity(), new(other.Destination, other.Cell.Clock), f.Instance, f.FutureVault, f.FutureRollback);
        Assert.Equal(f.Plan.SourceNamespace.CustomerId, other.Plan.SourceNamespace.CustomerId); Assert.NotEqual(f.SourceBinding!.Namespace, other.Plan.SourceNamespace);
        f.ClearKeys(); other.Destination.Requests.Clear();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.PrepareAsync(contexts, other.Plan, other.Plan.Sha256,
            other.Receipt.Sha256, head.Sequence, head.Sha256, "Synthetic wrong genuine lineage", f.Clock.Now.AddMinutes(5)));
        Assert.Empty(f.Destination.Requests); Assert.Empty(other.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents));
    }

    [WindowsCredentialFact]
    public async Task Replacing_only_unrelated_valid_history_at_the_same_count_invalidates_the_exact_intent()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); using var first = new RestrictedRecoveryAdoptionFixture(); using var second = new RestrictedRecoveryAdoptionFixture();
        await f.Initialize(); await first.Initialize(); await second.Initialize(); InsertBinding(f, first.SourceBinding!);
        var intent = await f.Prepare(); Assert.Equal(2, intent.Target.RuntimeBindingHistoryCount); Assert.Equal(f.SourceBinding, intent.Target.SourceRuntimeBinding);
        var guard = Text(f.Database, "SELECT sql FROM sqlite_master WHERE name='credential_runtime_bindings_no_delete'");
        Sql(f.Database, "DROP TRIGGER credential_runtime_bindings_no_delete;DELETE FROM credential_runtime_bindings WHERE id=$id;" + guard, ("$id", first.SourceBinding!.Id));
        InsertBinding(f, second.SourceBinding!); Assert.Equal(2, f.Number("SELECT count(*) FROM credential_runtime_bindings"));
        var before = f.ProtectedSnapshot(); f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); Assert.Equal("Prepared", (await f.Read(intent.Id)).State); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Empty(f.Destination.Requests);
        Assert.Equal(f.SourceBinding!.Sha256, Text(f.Database, "SELECT digest FROM credential_runtime_bindings WHERE id='" + f.SourceBinding.Id + "'")); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("policy")] [InlineData("expiry")] [InlineData("checkpoint")] [InlineData("activation")] [InlineData("selection")]
    public async Task Approval_requires_current_exact_policy_lifetime_heads_and_proposed_path_selection(string changed)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); var coordinator = f.Coordinator();
        if (changed == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1");
        if (changed == "expiry") f.Clock.Now = intent.ExpiresAt;
        if (changed == "checkpoint") { using var set = await f.Stage.Source.CaptureRestrictedSignedRecoverySetAsync(f.Plan.SourceNamespace, f.Stage.Signer, f.Stage.Set.Signature.KeyId, 2, f.Clock.Now, f.Clock.Now.AddHours(1)); await f.Checkpoints.AcceptNextAsync(set, set.Sha256, f.Stage.Signer, f.Cell.Keys, f.Head.Sequence, f.Head.Sha256); }
        if (changed == "activation") f.Activation.Activate(f.Database, f.Instance, f.ActivationHead.Generation, f.ActivationHead.Sha256);
        if (changed == "selection") coordinator = f.Coordinator(futureVault: f.Review.Child("different-proposed-vault"));
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent, coordinator)); Assert.Null((await f.Read(intent.Id)).Approval); AssertNoDestination(f);
    }

    [WindowsCredentialFact]
    public async Task Intent_expiry_cannot_outlive_the_shortest_requester_context()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToArray();
        contexts[1] = Clone(contexts[1], expiry: f.Clock.Now.AddSeconds(40));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().PrepareAsync(contexts, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256,
            "Synthetic overlong intent", f.Clock.Now.AddMinutes(1)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Intents)); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("root")] [InlineData("selection")] [InlineData("recovery")] [InlineData("checkpoint")]
    public async Task Profile_stripping_with_coherent_outer_hashes_is_rejected_by_the_actual_target_decoder(string location)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare();
        var node = JsonNode.Parse(Text(f.Database, "SELECT target FROM " + Intents))!.AsObject();
        var container = location switch { "selection" => node["selection"]!.AsObject(), "recovery" => node["recoveryTarget"]!.AsObject(), "checkpoint" => node["recoveryTarget"]!["checkpoint"]!["setAuthority"]!.AsObject(), _ => node };
        container.Remove("accessProfile"); RewriteIntentTarget(f, intent, node); var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead); f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => f.RestoredStore.GetCredentialRestrictedRecoveryAdoptionIntent(readers, intent.Id)); Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
    }

    [WindowsCredentialFact]
    public async Task A_coherently_rehashed_omitted_absence_scope_cannot_become_a_smaller_approved_intent()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare();
        var node = JsonNode.Parse(Text(f.Database, "SELECT target FROM " + Intents))!.AsObject(); node["recoveryTarget"]!["stageTarget"]!["scopes"]!.AsArray().RemoveAt(1);
        RewriteIntentTarget(f, intent, node); var digest = Text(f.Database, "SELECT digest FROM " + Intents); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove)).Take(1).ToArray();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ApproveAsync(contexts, intent.Id, digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256));
        Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("requester")] [InlineData("state")] [InlineData("delete-intent")] [InlineData("approval")]
    public async Task Retained_intent_identity_and_approval_are_immutable_without_repair(string changed)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); if (changed == "approval") intent = await f.Approve(intent);
        var before = Rows(f.Database, Intents); var approvals = Rows(f.Database, Approvals);
        Assert.Throws<SqliteException>(() => f.Sql(changed switch { "requester" => "UPDATE " + Intents + " SET requester_version=requester_version+1", "state" => "UPDATE " + Intents + " SET state='Succeeded'", "delete-intent" => "DELETE FROM " + Intents, _ => "UPDATE " + Approvals + " SET actor_version=actor_version+1" }));
        Assert.Equal(before, Rows(f.Database, Intents)); Assert.Equal(approvals, Rows(f.Database, Approvals)); Assert.Equal(intent.Digest, (await f.Read(intent.Id)).Digest);
    }

    [WindowsCredentialFact]
    public async Task Read_only_stage_approval_id_and_digest_do_not_substitute_for_an_adoption_intent()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var ordinary = new FoundationCredentialRestrictedRecoveryStageReviewCoordinator(f.RestoredStore, new(f.Destination, f.Clock), f.Instance);
        var old = await ordinary.PrepareAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare), f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic read-only review", f.Clock.Now.AddMinutes(5));
        old = f.RestoredStore.ApproveCredentialRestrictedRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), old.Id, old.Digest);
        var intent = await f.Prepare(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove); f.ClearKeys();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ApproveAsync(contexts, old.Id, old.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ApproveAsync(contexts, intent.Id, old.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256));
        Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals)); AssertNoDestination(f);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("revoked")]
    public async Task Reading_intent_history_needs_current_grants_across_the_complete_union(string attack)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryRead)).ToList();
        if (attack == "missing") contexts.RemoveAt(1); else if (attack == "duplicate") contexts.Add(contexts[0]); else f.RestoredStore.SetMembership(f.Review.Customer, "other-admin", MaintenanceExecutorFixture.Auditor, "other", "qa", ["Viewer"]);
        Assert.Throws<FoundationStoreException>(() => f.RestoredStore.GetCredentialRestrictedRecoveryAdoptionIntent(contexts, intent.Id));
    }

    [WindowsCredentialFact]
    public async Task Approved_history_is_readable_without_reviving_expired_or_revoked_original_humans()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Approve(await f.Prepare());
        Grant(f, RestrictedRecoveryReviewFixture.Requester, ["Viewer"]); Grant(f, RestrictedRecoveryReviewFixture.Approver, ["Viewer"]); f.Clock.Now = intent.ExpiresAt.AddMinutes(1); f.ClearKeys();
        var history = await f.Read(intent.Id); Assert.Equal(intent.Digest, history.Digest); Assert.Equal(intent.Approval, history.Approval); Assert.False(history.AdoptionAuthorized); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Repeated_restore_quarantines_every_inherited_intent_and_preserves_immutable_approval_history(bool approved)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = await f.Prepare(); if (approved) intent = await f.Approve(intent);
        var original = Rows(f.Database, Approvals); var backup = f.RestoredStore.CreateBackup(f.Review.Child("intent-history-backup.sqlite")); var bytes = File.ReadAllBytes(backup.DatabasePath); var protectedBefore = f.ProtectedSnapshot(); f.ClearKeys();
        var firstPath = f.Review.Child("intent-first-restored.sqlite"); var first = FoundationStore.RestoreBackup(backup, firstPath, f.Clock); var history = await f.Read(intent.Id, first);
        Assert.Equal("Quarantined", history.State); Assert.True(history.Quarantined); Assert.Equal(intent.Approval, history.Approval); Assert.Equal(original, Rows(firstPath, Approvals));
        var secondPath = f.Review.Child("intent-second-restored.sqlite"); var second = FoundationStore.RestoreBackup(first.CreateBackup(f.Review.Child("intent-second-backup.sqlite")), secondPath, f.Clock);
        Assert.NotEqual(first.GetCellInstanceIdentity().DatabaseInstanceId, second.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal("Quarantined", (await f.Read(intent.Id, second)).State);
        var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, store: second);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent, f.Coordinator(store: second), approvers));
        Assert.Equal(bytes, File.ReadAllBytes(backup.DatabasePath)); Assert.Equal(protectedBefore, f.ProtectedSnapshot()); Assert.Empty(f.Destination.Requests); AssertNoDestination(f);
        Assert.Equal(approved ? "Approved" : "Prepared", (await f.Read(intent.Id)).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Per_scope_audit_failure_rolls_back_prepare_or_approval_without_namespace_effects(bool approval)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approval ? await f.Prepare() : null; var coordinator = f.Coordinator();
        var before = f.ProtectedSnapshot(); var audits = f.Number("SELECT count(*) FROM audit");
        f.Sql("CREATE TRIGGER synthetic_adoption_audit_failure BEFORE INSERT ON audit WHEN NEW.action='" + (approval ? "CredentialRestrictedRecoveryAdoptionApproved" : "CredentialRestrictedRecoveryAdoptionPrepared") + "' AND NEW.project_id='other' BEGIN SELECT RAISE(ABORT,'synthetic scoped audit refusal'); END;");
        if (approval) await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent!, coordinator)); else await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(coordinator));
        Assert.Equal(approval ? 1 : 0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
        Assert.Equal(audits, f.Number("SELECT count(*) FROM audit")); Assert.Equal(before, f.ProtectedSnapshot()); AssertNoDestination(f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Final_store_time_cannot_corrupt_checkpoint_custody_then_publish_an_intent_or_approval(bool approval)
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var intent = approval ? await f.Prepare() : null;
        var clock = new CallbackClock(f.Clock.Now); var store = new FoundationStore(f.Database, f.Review.CellId, f.Review.Customer, clock); var coordinator = f.Coordinator(store: store);
        var checkpointPath = Path.Combine(f.Joint.CheckpointRoot, "checkpoint-0001.json"); var original = File.ReadAllBytes(checkpointPath); var changed = false;
        typeof(FoundationCredentialRestrictedRecoveryAdoptionCoordinator).GetProperty("ReviewFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(coordinator,
            new Action<string>(phase => { if (phase == "AfterVerification") clock.Next = () => { File.AppendAllText(checkpointPath, "!"); changed = true; }; }));
        var audits = f.Number("SELECT count(*) FROM audit"); f.ClearKeys();
        if (approval) await Assert.ThrowsAnyAsync<Exception>(() => f.Approve(intent!, coordinator)); else await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(coordinator));
        Assert.True(changed); Assert.Equal(original.Concat(new byte[] { 33 }).ToArray(), File.ReadAllBytes(checkpointPath)); Assert.Single(f.Destination.Requests);
        Assert.Equal(approval ? 1 : 0, f.Number("SELECT count(*) FROM " + Intents)); Assert.Equal(0, f.Number("SELECT count(*) FROM " + Approvals));
        Assert.Equal(audits, f.Number("SELECT count(*) FROM audit")); AssertNoDestination(f);
    }

    static void AssertNoDestination(RestrictedRecoveryAdoptionFixture f)
    { Assert.False(Directory.Exists(f.FutureVault)); Assert.False(Directory.Exists(f.FutureRollback)); Assert.Null(f.RestoredStore.GetCredentialRuntimeBindingStatus().CurrentBinding); Assert.Equal(1, f.Number("SELECT count(*) FROM recovery_execution_holds WHERE database_instance_id=(SELECT database_instance_id FROM cell WHERE singleton=1)")); }
    static void Grant(RestrictedRecoveryAdoptionFixture f, string actor, string[] roles)
    { f.RestoredStore.SetMembership(f.Review.Customer, "admin", actor, "default", "dev", roles); f.RestoredStore.SetMembership(f.Review.Customer, "other-admin", actor, "other", "qa", roles); }
    static void RewriteIntentTarget(RestrictedRecoveryAdoptionFixture f, FoundationCredentialRestrictedRecoveryAdoptionIntent intent, JsonObject target)
    {
        var json = target.ToJsonString(Json); var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var rewritten = intent with { TargetSha256 = sha }; var digest = (string)typeof(FoundationStore).GetMethod("RestrictedRecoveryAdoptionDigest", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [rewritten])!;
        var trigger = Text(f.Database, "SELECT sql FROM sqlite_master WHERE name='credential_restricted_recovery_adoption_intent_immutable'");
        Sql(f.Database, "DROP TRIGGER credential_restricted_recovery_adoption_intent_immutable;UPDATE " + Intents + " SET target=$target,target_sha256=$sha,digest=$digest WHERE id=$id;" + trigger,
            ("$target", json), ("$sha", sha), ("$digest", digest), ("$id", intent.Id));
    }
    static void InsertBinding(RestrictedRecoveryAdoptionFixture f, FoundationCredentialRuntimeBinding binding)
    {
        // Synthetic history projection only: copy a genuine separately provisioned row and append the
        // matching event with the real audit hash machinery. No production bypass or namespace adoption.
        using var db = Open(f.Database); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "INSERT INTO credential_runtime_bindings VALUES($c,$cell,$instance,$id,$path,$namespace,$authority,$generation,$activation,$created,$digest)";
        foreach (var p in new (string Name, object Value)[] { ("$c", binding.CustomerId), ("$cell", binding.CellId), ("$instance", binding.DatabaseInstanceId), ("$id", binding.Id), ("$path", binding.DatabasePathSha256),
            ("$namespace", JsonSerializer.Serialize(binding.Namespace, Json)), ("$authority", JsonSerializer.Serialize(binding.ActivationAuthority, Json)),
            ("$generation", binding.ActivationGeneration), ("$activation", binding.ActivationBindingSha256), ("$created", binding.CreatedAt.ToUnixTimeMilliseconds()), ("$digest", binding.Sha256) }) c.Parameters.AddWithValue(p.Name, p.Value);
        c.ExecuteNonQuery(); typeof(FoundationStore).GetMethod("Audit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.RestoredStore,
            [db, tx, "cell-maintenance", "default", "dev", "CredentialRuntimeBound", binding.Id, "Succeeded", "FreshOwnerNamespaceBinding", binding.Sha256]); tx.Commit();
    }
    static void Sql(string path, string sql, params (string Name, object Value)[] parameters)
    { using var db = Open(path); using var c = db.CreateCommand(); c.CommandText = sql; foreach (var p in parameters) c.Parameters.AddWithValue(p.Name, p.Value); c.ExecuteNonQuery(); }
    static string Text(string path, string sql) { using var db = Open(path); using var c = db.CreateCommand(); c.CommandText = sql; return (string)c.ExecuteScalar()!; }
    static string Rows(string path, string table)
    { using var db = Open(path); using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = c.ExecuteReader(); var rows = new List<object[]>(); while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return JsonSerializer.Serialize(rows, Json); }
    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    static void Corrupt(List<CustomerContext> contexts, string attack, DateTimeOffset now)
    {
        if (attack is "null-list" or "empty") { contexts.Clear(); return; }
        if (attack == "missing") { contexts.RemoveAt(1); return; }
        if (attack == "duplicate") { contexts.Add(contexts[0]); return; }
        if (attack == "extra") { contexts.Add(Clone(contexts[0], project: "not-reviewed")); return; }
        if (attack == "null-item") { contexts[0] = null!; return; }
        contexts[1] = attack switch
        {
            "actor" => Clone(contexts[1], actor: RestrictedRecoveryReviewFixture.Executor), "version" => Clone(contexts[1], version: contexts[1].RoleVersion + 1),
            "customer" => Clone(contexts[1], customer: Guid.NewGuid().ToString()), "cell" => Clone(contexts[1], cell: Guid.NewGuid().ToString()),
            "workload" => Clone(contexts[1], kind: PrincipalKind.Workload), "support" => Clone(contexts[1], support: "synthetic-support"),
            "capability" => Clone(contexts[1], capability: FoundationCapabilities.CredentialRecoveryRead), _ => Clone(contexts[1], expiry: now)
        };
    }
    static CustomerContext Clone(CustomerContext c, string? actor = null, string? customer = null, string? cell = null, string? project = null, long? version = null,
        PrincipalKind? kind = null, string? support = null, string? capability = null, DateTimeOffset? expiry = null) =>
        (CustomerContext)Activator.CreateInstance(typeof(CustomerContext), BindingFlags.NonPublic | BindingFlags.Instance, null,
            [customer ?? c.CustomerId, cell ?? c.CellId, actor ?? c.ActorKey, project ?? c.ProjectId, c.EnvironmentId, capability ?? c.Capability,
                version ?? c.RoleVersion, kind ?? c.Kind, expiry ?? c.ExpiresUtc, null, null, support], null)!;
    sealed class CallbackClock(DateTimeOffset now) : TimeProvider
    { internal Action? Next; public override DateTimeOffset GetUtcNow() { var action = Next; Next = null; action?.Invoke(); return now; } }
}
