using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialRecoveryStageReviews = 2048, MaximumPendingCredentialRecoveryStageReviews = 64;
    public const int MaximumCredentialRecoveryScopes = 256, MaximumCredentialRecoveryTargetBytes = 131072;
    sealed record RecoveryActor(string Key, long Version, DateTimeOffset ExpiresAt);

    internal void RequireCredentialRecoveryStageReviewPreparation(IReadOnlyList<CustomerContext> contexts, CredentialRecoveryStagePlan plan)
    {
        var scopes = RecoveryScopes(plan); using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        _ = RecoveryContexts(db, tx, contexts, scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"]);
        RecoveryReviewLifecycle(db, tx); tx.Commit();
    }

    public FoundationCredentialRecoveryStageReview PrepareCredentialRecoveryStageReview(IReadOnlyList<CustomerContext> requesters,
        CredentialRecoveryStagePlan plan, CredentialRecoveryStageReceipt receipt, string purpose, DateTimeOffset expiresUtc)
    {
        var target = CredentialRecoveryStageReviewTarget(plan, receipt);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var actor = RecoveryContexts(db, tx, requesters, target.Scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"]);
        RecoveryReviewLifecycle(db, tx); ValidatePurpose(purpose);
        var preparedAt = At(Now); var expires = At(expiresUtc.ToUnixTimeMilliseconds());
        if (expires <= preparedAt || expires > preparedAt.AddMinutes(15) || expires > actor.ExpiresAt || receipt.CompletedUtc > preparedAt)
            throw new FoundationStoreException("RecoveryStageReviewExpiryInvalid", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_recovery_stage_review_requests WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialRecoveryStageReviews)
            throw new FoundationStoreException("RecoveryStageReviewHistoryCapacityExceeded", 429);
        if (Number(db, tx, """
            SELECT count(*) FROM credential_recovery_stage_review_requests q WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND q.expires>$now
            AND NOT EXISTS(SELECT 1 FROM credential_recovery_stage_review_executions e WHERE e.customer_id=q.customer_id AND e.request_id=q.id)
            """, ("$c", CustomerId), ("$now", preparedAt.ToUnixTimeMilliseconds())) >= MaximumPendingCredentialRecoveryStageReviews)
            throw new FoundationStoreException("RecoveryStageReviewPendingCapacityExceeded", 429);
        if (target.SourceNamespace.CustomerId != CustomerId || target.SourceNamespace.CellId != CellId) throw new FoundationStoreException("RecoveryStageReviewCellMismatch", 403);
        var json = RecoveryTargetJson(target); var scope = target.Scopes[0];
        var review = new FoundationCredentialRecoveryStageReview(NewId(), CustomerId, CellId, scope.ProjectId, scope.EnvironmentId,
            target, Hash(json), actor.Key, actor.Version, actor.ExpiresAt, Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"),
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), Hash(purpose), expires, preparedAt, "", "Prepared", null, null, null, null, null);
        review = review with { Digest = RecoveryReviewDigest(review) };
        Exec(db, tx, """
            INSERT INTO credential_recovery_stage_review_requests VALUES($c,$id,$p,$e,$target,$sha,$actor,$version,$actorExpires,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now)
            """, ("$c", CustomerId), ("$id", review.Id), ("$p", scope.ProjectId), ("$e", scope.EnvironmentId), ("$target", json), ("$sha", review.TargetSha256),
            ("$actor", actor.Key), ("$version", actor.Version), ("$actorExpires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$policy", review.PolicyVersion),
            ("$instance", review.DatabaseInstanceId), ("$purpose", review.PurposeSha256), ("$expires", expires.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", review.PreparedAt.ToUnixTimeMilliseconds()));
        AuditRecoveryReview(db, tx, review, actor.Key, "CredentialRecoveryReviewPrepared", "Prepared", "AllScopeExactReviewRequired", review.Digest);
        var recorded = ReadRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }

    public FoundationCredentialRecoveryStageReview ApproveCredentialRecoveryStageReview(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = ReadRecoveryReview(db, tx, requestId);
        var actor = RecoveryContexts(db, tx, approvers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryApprove, ["Approver"]);
        MatchDigest(review.Digest, expectedDigest);
        if (review.State != "Prepared" || review.ApproverKey is not null || review.Execution is not null) throw new FoundationStoreException("RecoveryStageReviewAlreadyApproved");
        if (actor.Key == review.RequesterKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        RequireRecoveryReviewAuthority(db, tx, review);
        Exec(db, tx, "INSERT INTO credential_recovery_stage_review_approvals VALUES($c,$id,$actor,$version,$expires,$digest,$now)",
            ("$c", CustomerId), ("$id", review.Id), ("$actor", actor.Key), ("$version", actor.Version), ("$expires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", Now));
        Exec(db, tx, "UPDATE credential_recovery_stage_review_requests SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", review.Id));
        AuditRecoveryReview(db, tx, review, actor.Key, "CredentialRecoveryReviewApproved", "Approved", "DistinctAllScopeApprover", review.Digest);
        var result = ReadRecoveryReview(db, tx, review.Id); tx.Commit(); return result;
    }

    public FoundationCredentialRecoveryStageReview GetCredentialRecoveryStageReview(IReadOnlyList<CustomerContext> readers, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadRecoveryReview(db, tx, requestId);
        _ = RecoveryContexts(db, tx, readers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"]);
        tx.Commit(); return review; // Static-integrity history remains readable after expiry/quarantine without reviving authority.
    }
    internal FoundationCredentialRecoveryStageReview GetCredentialRecoveryStageReviewForExecution(IReadOnlyList<CustomerContext> executors, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadRecoveryReview(db, tx, requestId);
        var actor = RecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        RequireRecoveryReviewApproved(db, tx, review, actor.Key); tx.Commit(); return review;
    }

    internal CredentialRecoveryStageReviewAdmission AdmitCredentialRecoveryStageReview(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedDigest, FoundationCredentialRecoveryStageTarget actualTarget)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); var review = ReadRecoveryReview(db, tx, requestId);
            var actor = RecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
            MatchDigest(review.Digest, expectedDigest); RequireRecoveryReviewApproved(db, tx, review, actor.Key);
            MatchDigest(review.TargetSha256, Hash(RecoveryTargetJson(actualTarget)));
            var admittedAt = At(Now);
            if (admittedAt < review.PreparedAt || admittedAt < review.ApprovedAt || admittedAt >= review.ExpiresAt ||
                admittedAt >= review.RequesterExpiresAt || admittedAt >= review.ApproverExpiresAt || admittedAt >= actor.ExpiresAt)
                throw new FoundationStoreException("RecoveryStageReviewAdmissionTimeInvalid", 403);
            var admission = new CredentialRecoveryStageReviewAdmission(review, NewId(), actor.Key, actor.Version, review.DatabaseInstanceId);
            Exec(db, tx, """
                INSERT INTO credential_recovery_stage_review_executions VALUES($c,$id,$admission,$actor,$version,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", admission.AdmissionId), ("$actor", actor.Key), ("$version", actor.Version), ("$instance", review.DatabaseInstanceId), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            AuditRecoveryReview(db, tx, review, actor.Key, "CredentialRecoveryReviewAdmitted", "OutcomeUnknown", "OneUseBeforeDestinationKeyAccess", review.Digest);
            _ = ReadRecoveryReview(db, tx, review.Id); // Reject malformed persisted admission before committing or permitting destination-key access.
            commitAttempted = true; tx.Commit(); return admission;
        }
        catch (Exception) when (commitAttempted) { throw new FoundationStoreException("RecoveryStageReviewAdmissionUncertain", 503); }
    }

    internal FoundationCredentialRecoveryStageReview CompleteCredentialRecoveryStageReview(CredentialRecoveryStageReviewAdmission admission,
        IReadOnlyList<CustomerContext> executors, CredentialRecoveryStageVerification verification)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        var review = RequireRecoveryReviewAdmission(db, tx, admission);
        var actor = RecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        if (actor.Key != admission.ExecutorKey || actor.Version != admission.ExecutorVersion) throw new FoundationStoreException("RecoveryStageReviewExecutorChanged", 403);
        RequireRecoveryReviewAuthority(db, tx, review);
        var completedAt = At(Now);
        if (completedAt < review.PreparedAt || completedAt < review.ApprovedAt || completedAt < review.Execution!.AdmittedAt ||
            completedAt >= review.ExpiresAt || completedAt >= review.RequesterExpiresAt || completedAt >= review.ApproverExpiresAt || completedAt >= actor.ExpiresAt)
            throw new FoundationStoreException("RecoveryStageReviewCompletionTimeInvalid", 403);
        if (verification is null || verification.PlanSha256 != review.Target.PlanSha256 || verification.ReceiptSha256 != review.Target.ReceiptSha256 ||
            verification.SourceSetSha256 != review.Target.SourceSetSha256 || !MaintenanceHash(verification.Sha256) ||
            verification.ObservedUtc < review.Execution!.AdmittedAt || verification.ObservedUtc > completedAt || !CredentialRecoverySets.Utc(verification.ObservedUtc) ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || !verification.OutputCiphertextsAuthenticated ||
            verification.Receipt.StageId != review.Target.StageId || verification.Receipt.DestinationKey != review.Target.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != review.Target.DestinationRootCommitmentSha256 ||
            verification.Receipt.StagedVaultNamespaceId != review.Target.StagedVaultNamespaceId || verification.Receipt.StagedAuthorityNamespaceId != review.Target.StagedAuthorityNamespaceId ||
            verification.Receipt.BindingSha256 != review.Target.BindingSha256 || verification.Receipt.StateSha256 != review.Target.StateSha256)
            throw new FoundationStoreException("RecoveryStageReviewVerificationMismatch");
        var result = new FoundationCredentialRecoveryStageReviewResult(review.Digest, review.TargetSha256, verification.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.ObservedUtc);
        var json = RecoveryResultJson(result);
        Exec(db, tx, """
            UPDATE credential_recovery_stage_review_executions SET state='Succeeded',reason='HeldStageVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditRecoveryReview(db, tx, review, actor.Key, "CredentialRecoveryReviewCompleted", "Succeeded", "HeldEvidenceNoPromotionAuthority", Hash(json));
        var recorded = ReadRecoveryReview(db, tx, review.Id);
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("RecoveryStageReviewCompletionUncertain", 503); }
    }
    internal FoundationCredentialRecoveryStageReview RecordCredentialRecoveryStageReviewUnknown(CredentialRecoveryStageReviewAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = RequireRecoveryReviewAdmission(db, tx, admission, permitSucceeded: true);
        if (review.Execution!.State == "Succeeded") { tx.Commit(); return review; }
        var reason = RecoveryReviewReason(reasonCode);
        Exec(db, tx, "UPDATE credential_recovery_stage_review_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", Now), ("$c", CustomerId), ("$id", review.Id));
        AuditRecoveryReview(db, tx, review, admission.ExecutorKey, "CredentialRecoveryReviewUnknown", "OutcomeUnknown", reason, review.Digest);
        var recorded = ReadRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }
    internal void RecordCredentialRecoveryStageReviewDenied(IReadOnlyList<CustomerContext> executors, string requestId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = ReadRecoveryReview(db, tx, requestId);
        var actor = RecoveryContextIdentity(executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify);
        MatchDigest(review.Digest, expectedDigest);
        AuditRecoveryReview(db, tx, review, actor.Key, "CredentialRecoveryReviewDenied", "Denied", RecoveryReviewReason(reasonCode), review.Digest); tx.Commit();
    }

    void HoldRecoveredCredentialRecoveryReviews(SqliteConnection db, SqliteTransaction tx)
    {
        var ids = Rows(db, tx, """
            SELECT q.id FROM credential_recovery_stage_review_requests q
            LEFT JOIN credential_recovery_stage_review_executions e ON e.customer_id=q.customer_id AND e.request_id=q.id
            WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND (e.request_id IS NULL OR e.state!='Succeeded') ORDER BY q.id LIMIT $limit
            """, r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialRecoveryStageReviews + 1));
        if (ids.Count > MaximumCredentialRecoveryStageReviews) throw new FoundationStoreException("RecoveryStageReviewRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var review = ReadRecoveryReview(db, tx, id);
            Exec(db, tx, "UPDATE credential_recovery_stage_review_requests SET state='Quarantined' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", id));
            AuditRecoveryReview(db, tx, review, "cell-maintenance", "RecoveredCredentialRecoveryReviewHeld", "OutcomeUnknown", "RecoveryRequiresReconciliation", review.Digest);
        }
    }
}
