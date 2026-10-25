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
public sealed class FoundationCredentialRecoveryReviewBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Two_scope_review_requires_the_complete_human_grants_and_returns_only_inactive_metadata()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var before = f.StageSnapshot(); f.ClearKeys();
        var request = f.Store.PrepareCredentialRecoveryStageReview((await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).Reverse().ToArray(),
            f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5));
        var approved = f.Store.ApproveCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), request.Id, request.Digest);
        var read = f.Store.GetCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead), request.Id);
        Assert.Equal("Prepared", request.State); Assert.Equal("Approved", approved.State); Assert.Equal(JsonSerializer.Serialize(approved, Json), JsonSerializer.Serialize(read, Json));
        Assert.Equal(new[] { "default/dev", "recovery-sibling/prod" }, request.Target.Scopes.Select(s => s.ProjectId + "/" + s.EnvironmentId));
        Assert.Equal(f.Plan.Sha256, request.Target.PlanSha256); Assert.Equal(f.Receipt.Sha256, request.Target.ReceiptSha256);
        Assert.Equal(f.Set.Sha256, request.Target.SourceSetSha256); Assert.NotEqual(request.RequesterKey, approved.ApproverKey);
        Assert.Throws<NotSupportedException>(() => ((IList<FoundationCredentialRecoveryScope>)request.Target.Scopes).Clear());
        var text = JsonSerializer.Serialize(read, Json);
        foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.Cell.Scope.AuthProfileId, "\"ciphertext\":", "\"protectedKey\":" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageSnapshot());
        foreach (var scope in read.Target.Scopes)
        {
            var events = f.Store.ListAudit(f.Customer, ReviewFixture.Auditor, scope.ProjectId, scope.EnvironmentId, 1000).Where(e => e.TargetId == request.Id).ToArray();
            Assert.Contains(events, e => e.Action == "CredentialRecoveryReviewPrepared"); Assert.Contains(events, e => e.Action == "CredentialRecoveryReviewApproved");
            Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, ReviewFixture.Auditor, scope.ProjectId, scope.EnvironmentId));
        }
    }

    [Theory]
    [InlineData("missing")] [InlineData("extra")] [InlineData("duplicate")] [InlineData("empty")] [InlineData("too-many")]
    [InlineData("actor")] [InlineData("customer")] [InlineData("cell")] [InlineData("capability")] [InlineData("version")]
    [InlineData("expired")] [InlineData("workload")] [InlineData("support")] [InlineData("scope-case")]
    public async Task Prepare_refuses_a_partial_ambiguous_or_cross_bound_context_set_without_records(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare)).ToList();
        if (attack == "missing") contexts.RemoveAt(1);
        else if (attack == "empty") contexts.Clear();
        else if (attack == "extra") contexts.Add(Clone(contexts[0], project: "other-project"));
        else if (attack == "duplicate") contexts.Add(contexts[0]);
        else if (attack == "too-many") contexts = Enumerable.Repeat(contexts[0], 257).ToList();
        else contexts[1] = attack switch
        {
            "actor" => Clone(contexts[1], actor: ReviewFixture.Approver), "customer" => Clone(contexts[1], customer: Guid.NewGuid().ToString()),
            "cell" => Clone(contexts[1], cell: Guid.NewGuid().ToString()), "capability" => Clone(contexts[1], capability: FoundationCapabilities.CredentialMaintenancePrepare),
            "version" => Clone(contexts[1], version: contexts[1].RoleVersion + 1), "expired" => Clone(contexts[1], expiry: f.Cell.Clock.Now),
            "workload" => Clone(contexts[1], kind: PrincipalKind.Workload), "support" => Clone(contexts[1], support: "synthetic-support-approval"),
            _ => Clone(contexts[1], project: "RECOVERY-SIBLING")
        };
        var audit = f.Number("SELECT count(*) FROM audit"); var before = f.StageSnapshot(); f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRecoveryStageReview(contexts, f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.RequestCount()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(before, f.StageSnapshot());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("actor")] [InlineData("expired")] [InlineData("capability")]
    public async Task Approval_needs_every_exact_scope_of_one_current_approver(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); var contexts = (await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove)).ToList();
        if (attack == "missing") contexts.RemoveAt(1); else if (attack == "duplicate") contexts.Add(contexts[0]);
        else contexts[1] = attack switch { "actor" => Clone(contexts[1], actor: ReviewFixture.Operator), "expired" => Clone(contexts[1], expiry: f.Cell.Clock.Now), _ => Clone(contexts[1], capability: FoundationCapabilities.CredentialRecoveryRead) };
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRecoveryStageReview(contexts, request.Id, request.Digest));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_approvals")); Assert.Equal("Prepared", (await f.Read(request.Id)).State);
    }

    [Theory]
    [InlineData("requester-disabled")] [InlineData("requester-role")] [InlineData("requester-version")]
    [InlineData("approver-disabled")] [InlineData("approver-role")] [InlineData("approver-version")]
    public async Task One_changed_scope_or_actor_version_refuses_the_whole_approval(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        var actor = attack.StartsWith("requester", StringComparison.Ordinal) ? ReviewFixture.Operator : ReviewFixture.Approver;
        var role = actor == ReviewFixture.Operator ? "Operator" : "Approver";
        f.Store.SetMembership(f.Customer, "secondary-admin", actor, "recovery-sibling", "prod", [attack.EndsWith("role", StringComparison.Ordinal) ? "Viewer" : role], !attack.EndsWith("disabled", StringComparison.Ordinal));
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRecoveryStageReview(approvers, request.Id, request.Digest));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_approvals")); Assert.Equal("Prepared", (await f.Read(request.Id)).State);
    }

    [WindowsCredentialFact]
    public async Task Same_actor_with_both_roles_cannot_approve_its_own_request()
    {
        using var f = new ReviewFixture(); await f.Initialize(); f.Grant(ReviewFixture.Operator, ["Operator", "Approver"]);
        var request = await f.Prepare(); var ownApproval = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, subject: IdentityTestIssuer.Subject, signedRoles: ["Operator", "Approver"]);
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRecoveryStageReview(ownApproval, request.Id, request.Digest));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_approvals"));
    }

    [Theory]
    [InlineData("Operator", FoundationCapabilities.CredentialRecoveryApprove)]
    [InlineData("Approver", FoundationCapabilities.CredentialRecoveryPrepare)]
    [InlineData("Viewer", FoundationCapabilities.CredentialRecoveryRead)]
    [InlineData("CustomerAdmin", FoundationCapabilities.CredentialRecoveryVerify)]
    public async Task Current_database_membership_never_overrides_the_signed_role_intersection(string signedRole, string capability)
    {
        using var f = new ReviewFixture(); await f.Initialize(); f.Grant(ReviewFixture.Operator, ["Operator", "Approver", "Auditor", "CustomerAdmin"]);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => f.Contexts(capability, subject: IdentityTestIssuer.Subject, signedRoles: [signedRole]));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("support-only")] [InlineData("support-mixed")]
    public async Task Raw_invalid_membership_roles_cannot_be_smuggled_through_an_earlier_context(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var roles = attack switch { "unknown" => "[\"Operator\",\"Unrecognized\"]", "duplicate" => "[\"Operator\",\"Operator\"]", "support-only" => "[\"SupportDelegate\"]", _ => "[\"Operator\",\"SupportDelegate\"]" };
        f.Sql("UPDATE memberships SET roles=$roles WHERE actor_key=$actor AND project_id='recovery-sibling'", ("$roles", roles), ("$actor", ReviewFixture.Operator));
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRecoveryStageReview(contexts, f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.RequestCount());
    }

    [Theory]
    [InlineData("policy")] [InlineData("instance")] [InlineData("expiry")]
    public async Task A_current_approval_cannot_be_added_to_a_stale_request(string changed)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare();
        if (changed == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1");
        if (changed == "instance") f.Sql("UPDATE cell SET database_instance_id=$id", ("$id", Guid.NewGuid().ToString("N")));
        if (changed == "expiry") f.Cell.Clock.Now = request.ExpiresAt;
        var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRecoveryStageReview(approvers, request.Id, request.Digest));
        Assert.Equal("Prepared", (await f.Read(request.Id)).State);
    }

    [WindowsCredentialFact]
    public async Task Request_expiry_cannot_outlive_the_shortest_context_and_approval_expiry_is_pinned()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var requesters = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare, lifetime: TimeSpan.FromMinutes(2));
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRecoveryStageReview(requesters, f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(3)));
        var request = f.Store.PrepareCredentialRecoveryStageReview(requesters, f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(1));
        Assert.Equal(f.Cell.Clock.Now.AddMinutes(2), request.RequesterExpiresAt);
        var approved = f.Store.ApproveCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, lifetime: TimeSpan.FromSeconds(40)), request.Id, request.Digest);
        Assert.Equal(f.Cell.Clock.Now.AddSeconds(40), approved.ApproverExpiresAt);
    }

    [WindowsCredentialFact]
    public async Task Incorrect_digest_and_concurrent_approval_attempts_do_not_overwrite_the_exact_approval()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialRecoveryStageReview(contexts, request.Id, new string('1', 64)));
        async Task<bool> Approve() => await Task.Run(() => { try { f.Store.ApproveCredentialRecoveryStageReview(contexts, request.Id, request.Digest); return true; } catch (FoundationStoreException) { return false; } });
        var results = await Task.WhenAll(Approve(), Approve()); Assert.Single(results, value => value); Assert.Single(results, value => !value);
        Assert.Equal(1, f.Number("SELECT count(*) FROM credential_recovery_stage_review_approvals")); Assert.Equal("Approved", (await f.Read(request.Id)).State);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("disabled-scope")] [InlineData("stale-version")]
    public async Task Full_history_read_never_leaks_another_scope_through_partial_or_revoked_reader_grants(string attack)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); var readers = (await f.Contexts(FoundationCapabilities.CredentialRecoveryRead)).ToList();
        if (attack == "missing") readers.RemoveAt(1); else if (attack == "duplicate") readers.Add(readers[0]);
        else f.Store.SetMembership(f.Customer, "secondary-admin", ReviewFixture.Auditor, "recovery-sibling", "prod", ["Auditor"], enabled: attack != "disabled-scope");
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRecoveryStageReview(readers, request.Id));
        if (attack == "stale-version") Assert.Equal(request.Id, (await f.Read(request.Id)).Id);
    }

    [WindowsCredentialFact]
    public async Task Historical_read_uses_current_reader_grants_without_reviving_requester_or_approver_authority()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); var approved = await f.Approve(request);
        f.Grant(ReviewFixture.Operator, ["Viewer"]); f.Grant(ReviewFixture.Approver, ["Viewer"]); f.Cell.Clock.Now = request.ExpiresAt.AddMinutes(1);
        var history = await f.Read(request.Id); Assert.Equal(approved.Digest, history.Digest); Assert.Equal(approved.ApproverKey, history.ApproverKey);
        Assert.Null(history.Execution); Assert.Equal("Approved", history.State);
    }

    [Theory]
    [InlineData("plan")] [InlineData("receipt")]
    public async Task A_real_but_different_plan_or_completion_receipt_is_not_a_replacement_for_the_reviewed_target(string changed)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var other = CredentialRecoveryStaging.PreparePlan(f.Selected, f.Authority, f.Identity,
            f.Child("other-held-stage"), f.Receipt.DestinationKey, "Different exact owner selection", f.Cell.Clock.Now.AddMinutes(10), f.Cell.Clock);
        var otherReceipt = await new CredentialRecoveryStageExecutor(f.Cell.Keys, f.DestinationKeys, f.Cell.Clock).StageAsync(f.Set, other, other.Sha256, f.Signer);
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare); var before = f.StageSnapshot(); f.ClearKeys();
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRecoveryStageReview(contexts, changed == "plan" ? other : f.Plan,
            changed == "receipt" ? otherReceipt : f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.RequestCount()); Assert.Equal(before, f.StageSnapshot()); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_sets_and_unscoped_absence_maps_never_become_vacuous_whole_cell_authority(bool absent)
    {
        using var f = new ReviewFixture(strong: absent); await f.Initialize(profiles: absent ? 2 : 0, includeAbsence: absent);
        Assert.Equal(absent, f.Plan.AbsentProfileIds.Count > 0); var before = f.StageSnapshot(); f.ClearKeys();
        var requesters = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialRecoveryStageReview(requesters, f.Plan, f.Receipt, ReviewFixture.Purpose, f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.RequestCount()); Assert.Equal(before, f.StageSnapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Expired_approval_context_is_not_revived_by_a_fresh_executor_token()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare();
        var approved = f.Store.ApproveCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, lifetime: TimeSpan.FromSeconds(40)), request.Id, request.Digest);
        f.Cell.Clock.Now = approved.ApproverExpiresAt!.Value; f.ClearKeys();
        var executors = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(executors, request.Id, request.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_executions")); Assert.Empty(f.DestinationKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task An_approver_with_an_operator_role_still_cannot_execute_its_own_approval()
    {
        using var f = new ReviewFixture(); await f.Initialize(); f.Grant(ReviewFixture.Approver, ["Approver", "Operator"]);
        var request = await f.Prepare(); await f.Approve(request); f.ClearKeys();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, subject: IdentityTestIssuer.OtherSubject, signedRoles: ["Approver", "Operator"]);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, request.Id, request.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_executions")); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_clock_discontinuity_at_admission_cannot_commit_invalid_authority_or_access_the_destination_key(bool forward)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request);
        var executors = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromSeconds(40));
        var clock = new AdmissionDiscontinuityClock(f.Cell.Clock.Now,
            forward ? executors.Min(c => c.ExpiresUtc).AddMilliseconds(1) : request.PreparedAt.AddMilliseconds(-1));
        var store = new FoundationStore(f.Child("cell.db"), f.Cell.Scope.CellId, f.Customer, clock);
        var coordinator = new FoundationCredentialRecoveryStageReviewCoordinator(store, new CredentialRecoveryStageVerifier(f.DestinationKeys, f.Cell.Clock), store.GetCellInstanceIdentity().DatabaseInstanceId);
        var stage = f.StageSnapshot(); var audits = f.Number("SELECT count(*) FROM audit"); f.ClearKeys(); clock.Begin();
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ExecuteAsync(executors, request.Id, request.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.True(clock.ObservedDiscontinuity); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_executions")); Assert.Equal(audits, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal("Approved", (await f.Read(request.Id)).State); Assert.Equal(stage, f.StageSnapshot());
    }

    [WindowsCredentialFact]
    public async Task Scoped_audit_pages_contain_commitments_but_no_other_scope_target_or_private_payload()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request);
        var start = f.Store.CreatePagedScopeExport(f.Customer, ReviewFixture.Auditor, "default", "dev", 2); var pages = new List<FoundationPagedExportPage> { start.FirstPage };
        while (pages[^1].NextCursor is { } cursor) pages.Add(f.Store.ReadPagedScopeExportPage(f.Customer, ReviewFixture.Auditor, "default", "dev", start.Snapshot.Id, cursor, start.Snapshot.Sha256));
        Assert.Equal(start.Snapshot.Sha256, Hash(Convert.FromBase64String(start.Snapshot.CanonicalManifestBase64)));
        for (var i = 0; i < pages.Count; i++) { Assert.Equal(pages[i].Sha256, Hash(Convert.FromBase64String(pages[i].CanonicalPageBase64))); Assert.Equal(start.Snapshot.PageSha256[i], pages[i].Sha256); }
        var audits = pages.SelectMany(p => p.Items).Where(i => i.Kind == "audit").Select(i => i.Metadata).ToArray();
        Assert.Contains(audits, e => e.GetProperty("targetId").GetString() == request.Id && e.GetProperty("action").GetString() == "CredentialRecoveryReviewApproved");
        Assert.All(audits, e => { Assert.Equal("default", e.GetProperty("projectId").GetString()); Assert.Equal("dev", e.GetProperty("environmentId").GetString()); });
        var text = JsonSerializer.Serialize(pages, Json);
        foreach (var value in new[] { f.Cell.Root, CredentialFixture.Token, f.Cell.Scope.AuthProfileId, "recovery-sibling", "\"ciphertext\":", "\"target\":" }) Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Restore_quarantines_unfinished_review_and_repeated_restore_rotates_authority_without_touching_stage(bool approved)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); if (approved) request = await f.Approve(request);
        var originalApproval = f.Rows("credential_recovery_stage_review_approvals"); var originalInstance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        var backup = f.Store.CreateBackup(f.Child("review-snapshot.sqlite")); var sourceBytes = File.ReadAllBytes(backup.DatabasePath); var stageBytes = f.StageSnapshot(); f.ClearKeys();
        var restored = FoundationStore.RestoreBackup(backup, f.Child("restored-review.sqlite"), f.Cell.Clock);
        var history = restored.GetCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: restored), request.Id);
        Assert.Equal("Quarantined", history.State); Assert.Equal(request.DatabaseInstanceId, history.DatabaseInstanceId); Assert.Null(history.Execution);
        Assert.NotEqual(originalInstance, restored.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal(originalApproval, f.Rows("credential_recovery_stage_review_approvals", f.Child("restored-review.sqlite")));
        var restoredApprovers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, store: restored);
        Assert.Throws<FoundationStoreException>(() => restored.ApproveCredentialRecoveryStageReview(restoredApprovers, request.Id, request.Digest));
        var next = restored.CreateBackup(f.Child("second-snapshot.sqlite")); var twice = FoundationStore.RestoreBackup(next, f.Child("twice-restored.sqlite"), f.Cell.Clock);
        Assert.NotEqual(restored.GetCellInstanceIdentity().DatabaseInstanceId, twice.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal("Quarantined", twice.GetCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: twice), request.Id).State);
        Assert.Equal(sourceBytes, File.ReadAllBytes(backup.DatabasePath)); Assert.Equal(stageBytes, f.StageSnapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests);
        Assert.Equal(approved ? "Approved" : "Prepared", (await f.Read(request.Id)).State);
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")] [InlineData("Succeeded")]
    public async Task Restored_consumed_unknown_and_completed_results_are_immutable_history_and_never_replay_authority(string phase)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request);
        var coordinator = f.Coordinator(); if (phase != "Succeeded") typeof(FoundationCredentialRecoveryStageReviewCoordinator).GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator,
            new Action<string>(observed => { if (observed == phase) throw new IOException("Synthetic local review interruption."); }));
        var result = await coordinator.ExecuteAsync(await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify), request.Id, request.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        Assert.Equal(phase == "Succeeded" ? "Succeeded" : "OutcomeUnknown", result.Execution!.State);
        if (result.Execution.Result is { } success)
        { Assert.Equal("HeldStageVerified", success.Status); Assert.True(success.InactiveRecoveryHold); Assert.False(success.PromotionAuthorized); Assert.False(success.LiveAdoptionAuthorized); Assert.False(success.ActivationPerformed); Assert.False(success.ExternalExecutionAuthorized); }
        var admissions = f.Rows("credential_recovery_stage_review_executions"); var approvals = f.Rows("credential_recovery_stage_review_approvals");
        var snapshot = f.Store.CreateBackup(f.Child("consumed-snapshot.sqlite")); var original = File.ReadAllBytes(snapshot.DatabasePath); var stage = f.StageSnapshot();
        var restoredPath = f.Child("consumed-restored.sqlite"); f.ClearKeys(); var restored = FoundationStore.RestoreBackup(snapshot, restoredPath, f.Cell.Clock);
        var history = restored.GetCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryRead, store: restored), request.Id);
        Assert.Equal(phase == "Succeeded" ? "Approved" : "Quarantined", history.State);
        Assert.Equal(JsonSerializer.Serialize(result.Execution, Json), JsonSerializer.Serialize(history.Execution, Json));
        Assert.Equal(admissions, f.Rows("credential_recovery_stage_review_executions", restoredPath)); Assert.Equal(approvals, f.Rows("credential_recovery_stage_review_approvals", restoredPath));
        var executors = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, store: restored);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator(restored).ExecuteAsync(executors, request.Id, request.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(admissions, f.Rows("credential_recovery_stage_review_executions", restoredPath)); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(original, File.ReadAllBytes(snapshot.DatabasePath)); Assert.Equal(stage, f.StageSnapshot());
        foreach (var scope in request.Target.Scopes) Assert.True(restored.VerifyAuditIntegrity(f.Customer, ReviewFixture.Auditor, scope.ProjectId, scope.EnvironmentId));
    }

    [WindowsCredentialFact]
    public async Task Restore_audit_capacity_failure_preserves_source_stage_and_has_no_partially_recovered_target()
    {
        using var f = new ReviewFixture(); await f.Initialize(); await f.Prepare(); var snapshot = f.Store.CreateBackup(f.Child("capacity-snapshot.sqlite"));
        snapshot = FoundationTestAuditCapacity.FillSnapshot(snapshot, f.Cell.Clock, availableAuditEvents: 1); var bytes = File.ReadAllBytes(snapshot.DatabasePath); var stage = f.StageSnapshot();
        Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, f.Child("capacity-restored.sqlite"), f.Cell.Clock));
        Assert.Equal(bytes, File.ReadAllBytes(snapshot.DatabasePath)); Assert.Equal(stage, f.StageSnapshot()); Assert.False(File.Exists(f.Child("capacity-restored.sqlite")));
    }

    static CustomerContext Clone(CustomerContext original, string? actor = null, string? customer = null, string? cell = null,
        string? capability = null, string? project = null, long? version = null, DateTimeOffset? expiry = null, PrincipalKind? kind = null, string? support = null) =>
        (CustomerContext)Activator.CreateInstance(typeof(CustomerContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [customer ?? original.CustomerId, cell ?? original.CellId, actor ?? original.ActorKey, project ?? original.ProjectId, original.EnvironmentId,
                capability ?? original.Capability, version ?? original.RoleVersion, kind ?? original.Kind, expiry ?? original.ExpiresUtc, null, null, support], null)!;
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed class AdmissionDiscontinuityClock(DateTimeOffset now, DateTimeOffset discontinuity) : TimeProvider
    {
        int calls; bool armed;
        public bool ObservedDiscontinuity { get; private set; }
        public void Begin() { calls = 0; armed = true; }
        public override DateTimeOffset GetUtcNow()
        {
            // Initial execution and admission each check contexts then standing authority. The next sample
            // is the durable admission timestamp; exercise a discontinuity at that publication boundary.
            if (armed && ++calls == 5) { ObservedDiscontinuity = true; return discontinuity; }
            return now;
        }
    }

    sealed class ReviewFixture : IDisposable
    {
        public const string Purpose = "Explicit synthetic inactive recovery review";
        public const string Operator = MaintenanceExecutorFixture.Operator, Approver = MaintenanceExecutorFixture.Approver, Auditor = MaintenanceExecutorFixture.Auditor;
        public CredentialFixture Cell { get; } = new(); readonly IdentityTestIssuer issuer = new(); public RSA Signer { get; } = RSA.Create(2048);
        public CredentialTestKeys DestinationKeys { get; } = new(); public FoundationStore Store { get; }
        public string Customer => Cell.Scope.CustomerId; public string StagePath => Child("held-review-stage"); public CredentialVaultNamespaceIdentity Identity { get; } public CredentialVault SourceVault { get; }
        public CredentialRecoverySet Set { get; private set; } = null!; public CredentialRecoverySetAuthority Authority { get; private set; } = null!;
        public CredentialRecoverySetVerification Selected { get; private set; } = null!; public CredentialRecoveryStagePlan Plan { get; private set; } = null!; public CredentialRecoveryStageReceipt Receipt { get; private set; } = null!;
        public ReviewFixture(bool strong = false)
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Store = new(Child("cell.db"), Cell.Scope.CellId, Customer, Cell.Clock);
            SourceVault = strong ? CredentialVault.ProvisionStrong(Cell.Options with { RootPath = Child("review-source-vault"), RollbackProtection = new(Child("review-source-authority")) }, Cell.Keys, Cell.Clock) : Cell.Vault;
            Identity = SourceVault.DescribeNamespace();
            var onboard = Store.PrepareScopeOnboarding(Customer, "admin", "recovery-sibling", "prod", "secondary-admin", Purpose);
            Store.ApproveAdministrativePlan(Customer, "approver", onboard.Id, onboard.Digest); Store.ExecuteScopeOnboarding(Customer, "admin", onboard.Id, onboard.Digest);
            Grant(Operator, ["Operator"]); Grant(Approver, ["Approver"]); Grant(Auditor, ["Auditor"]); DestinationKeys.Values.Clear(); DestinationKeys.Values["recovery-destination"] = RandomNumberGenerator.GetBytes(32);
        }
        public async Task Initialize(int profiles = 2, bool includeAbsence = false)
        {
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            if (includeAbsence)
            {
                const string id = "profile/absent-scope-unavailable";
                var property = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
                property.SetValue(SourceVault, new Action<string>(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic unpublished profile."); }));
                try { await Assert.ThrowsAsync<IOException>(() => SourceVault.PutAsync(Cell.Scope with { AuthProfileId = id }, secret, Cell.Clock.Now.AddHours(1))); } finally { property.SetValue(SourceVault, null); }
                var pending = await SourceVault.InspectRollbackStateAsync(id); await SourceVault.ReconcilePendingWriteAsync(id, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            }
            if (profiles > 0) await SourceVault.PutAsync(Cell.Scope, secret, Cell.Clock.Now.AddHours(1));
            if (profiles > 1) await SourceVault.PutAsync(Cell.Scope with { ProjectId = "recovery-sibling", EnvironmentId = "prod", AuthProfileId = "profile/recovery-sibling" }, secret, Cell.Clock.Now.AddHours(1));
            Set = await SourceVault.CaptureSignedRecoverySetAsync(Identity, Signer, "synthetic-review-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
            Authority = CredentialRecoverySets.Authority(Set); Selected = CredentialRecoverySets.Verify(Set, Authority, Signer, Identity, Cell.Clock);
            var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Customer, Cell.Scope.CellId, "recovery-destination", DestinationKeys);
            Plan = CredentialRecoveryStaging.PreparePlan(Selected, Authority, Identity, StagePath, key, Purpose, Cell.Clock.Now.AddMinutes(10), Cell.Clock);
            Receipt = await new CredentialRecoveryStageExecutor(Cell.Keys, DestinationKeys, Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, Signer);
        }
        public void Grant(string actor, string[] roles)
        { Store.SetMembership(Customer, "admin", actor, "default", "dev", roles); Store.SetMembership(Customer, "secondary-admin", actor, "recovery-sibling", "prod", roles); }
        public async Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? subject = null, string[]? signedRoles = null, TimeSpan? lifetime = null, FoundationStore? store = null)
        {
            store ??= Store; var role = capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator";
            subject ??= role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject); claims["roles"] = signedRoles ?? [role];
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims)); var resolver = new CustomerContextResolver(options, new Members(store), Cell.Clock);
            return [resolver.Require(principal, Customer, "default", "dev", capability), resolver.Require(principal, Customer, "recovery-sibling", "prod", capability)];
        }
        public async Task<FoundationCredentialRecoveryStageReview> Prepare() => Store.PrepareCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Receipt, Purpose, Cell.Clock.Now.AddMinutes(5));
        public async Task<FoundationCredentialRecoveryStageReview> Approve(FoundationCredentialRecoveryStageReview review) => Store.ApproveCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        public async Task<FoundationCredentialRecoveryStageReview> Read(string id) => Store.GetCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        public FoundationCredentialRecoveryStageReviewCoordinator Coordinator(FoundationStore? store = null)
        { store ??= Store; return new(store, new CredentialRecoveryStageVerifier(DestinationKeys, Cell.Clock), store.GetCellInstanceIdentity().DatabaseInstanceId); }
        public void ClearKeys() { Cell.Keys.Requests.Clear(); DestinationKeys.Requests.Clear(); }
        public string Child(string name) => Path.Combine(Cell.Root, name);
        public string[] StageSnapshot() => Directory.EnumerateFileSystemEntries(StagePath, "*", SearchOption.AllDirectories).Prepend(StagePath).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(StagePath, path) + ":" + Hash((Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm()) + ":" + (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path)))).ToArray();
        public long RequestCount() => Number("SELECT count(*) FROM credential_recovery_stage_review_requests");
        public long Number(string sql, string? path = null) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Sql(string sql, params (string Name, object Value)[] parameters) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value); command.ExecuteNonQuery(); }
        public string Rows(string table, string? path = null)
        { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = command.ExecuteReader(); var rows = new List<object[]>(); while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return JsonSerializer.Serialize(rows, Json); }
        SqliteConnection Open(string? path = null) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Child("cell.db"), Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); issuer.Dispose(); Cell.Dispose(); }
        sealed class Members(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetCredentialMaintenanceMemberships(customerId, actorKey).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
