using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialScopedRecoveryStageReviews = 2048, MaximumPendingCredentialScopedRecoveryStageReviews = 64;
    public const int MaximumCredentialScopedRecoveryScopes = 256, MaximumCredentialScopedRecoveryTargetBytes = 131072;
    sealed record ScopedRecoveryActor(string Key, long Version, DateTimeOffset ExpiresAt);

    internal void RequireCredentialScopedRecoveryStageReviewPreparation(IReadOnlyList<CustomerContext> contexts, CredentialScopedRecoveryStagePlan plan)
    {
        var scopes = ScopedRecoveryScopes(plan); using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        _ = ScopedRecoveryContexts(db, tx, contexts, scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"]);
        ScopedRecoveryReviewLifecycle(db, tx); tx.Commit();
    }

    public FoundationCredentialScopedRecoveryStageReview PrepareCredentialScopedRecoveryStageReview(IReadOnlyList<CustomerContext> requesters,
        CredentialScopedRecoveryStagePlan plan, CredentialScopedRecoveryStageReceipt receipt, string purpose, DateTimeOffset expiresUtc)
    {
        var target = CredentialScopedRecoveryStageReviewTarget(plan, receipt);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var actor = ScopedRecoveryContexts(db, tx, requesters, target.Scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"]);
        ScopedRecoveryReviewLifecycle(db, tx); ValidatePurpose(purpose);
        var preparedAt = At(Now); var expires = At(expiresUtc.ToUnixTimeMilliseconds());
        if (expires <= preparedAt || expires > preparedAt.AddMinutes(15) || expires > actor.ExpiresAt || receipt.CompletedUtc > preparedAt)
            throw new FoundationStoreException("ScopedRecoveryStageReviewExpiryInvalid", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_scoped_recovery_stage_review_requests WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialScopedRecoveryStageReviews)
            throw new FoundationStoreException("ScopedRecoveryStageReviewHistoryCapacityExceeded", 429);
        if (Number(db, tx, """
            SELECT count(*) FROM credential_scoped_recovery_stage_review_requests q WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND q.expires>$now
            AND NOT EXISTS(SELECT 1 FROM credential_scoped_recovery_stage_review_executions e WHERE e.customer_id=q.customer_id AND e.request_id=q.id)
            """, ("$c", CustomerId), ("$now", preparedAt.ToUnixTimeMilliseconds())) >= MaximumPendingCredentialScopedRecoveryStageReviews)
            throw new FoundationStoreException("ScopedRecoveryStageReviewPendingCapacityExceeded", 429);
        if (target.SourceNamespace.CustomerId != CustomerId || target.SourceNamespace.CellId != CellId) throw new FoundationStoreException("ScopedRecoveryStageReviewCellMismatch", 403);
        var json = ScopedRecoveryTargetJson(target); var scope = target.Scopes[0];
        var review = new FoundationCredentialScopedRecoveryStageReview(NewId(), CustomerId, CellId, scope.ProjectId, scope.EnvironmentId,
            target, Hash(json), actor.Key, actor.Version, actor.ExpiresAt, Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"),
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), Hash(purpose), expires, preparedAt, "", "Prepared", null, null, null, null, null);
        review = review with { Digest = ScopedRecoveryReviewDigest(review) };
        Exec(db, tx, """
            INSERT INTO credential_scoped_recovery_stage_review_requests VALUES($c,$id,$p,$e,$target,$sha,$actor,$version,$actorExpires,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now)
            """, ("$c", CustomerId), ("$id", review.Id), ("$p", scope.ProjectId), ("$e", scope.EnvironmentId), ("$target", json), ("$sha", review.TargetSha256),
            ("$actor", actor.Key), ("$version", actor.Version), ("$actorExpires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$policy", review.PolicyVersion),
            ("$instance", review.DatabaseInstanceId), ("$purpose", review.PurposeSha256), ("$expires", expires.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", review.PreparedAt.ToUnixTimeMilliseconds()));
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewPrepared", "Prepared", "AllScopeExactReviewRequired", review.Digest);
        var recorded = ReadScopedRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }

    public FoundationCredentialScopedRecoveryStageReview ApproveCredentialScopedRecoveryStageReview(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = ReadScopedRecoveryReview(db, tx, requestId);
        var actor = ScopedRecoveryContexts(db, tx, approvers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryApprove, ["Approver"]);
        MatchDigest(review.Digest, expectedDigest);
        if (review.State != "Prepared" || review.ApproverKey is not null || review.Execution is not null) throw new FoundationStoreException("ScopedRecoveryStageReviewAlreadyApproved");
        if (actor.Key == review.RequesterKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        RequireScopedRecoveryReviewAuthority(db, tx, review);
        Exec(db, tx, "INSERT INTO credential_scoped_recovery_stage_review_approvals VALUES($c,$id,$actor,$version,$expires,$digest,$now)",
            ("$c", CustomerId), ("$id", review.Id), ("$actor", actor.Key), ("$version", actor.Version), ("$expires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", Now));
        Exec(db, tx, "UPDATE credential_scoped_recovery_stage_review_requests SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", review.Id));
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewApproved", "Approved", "DistinctAllScopeApprover", review.Digest);
        var result = ReadScopedRecoveryReview(db, tx, review.Id); tx.Commit(); return result;
    }

    public FoundationCredentialScopedRecoveryStageReview GetCredentialScopedRecoveryStageReview(IReadOnlyList<CustomerContext> readers, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadScopedRecoveryReview(db, tx, requestId);
        _ = ScopedRecoveryContexts(db, tx, readers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"]);
        tx.Commit(); return review; // Static-integrity history remains readable after expiry/quarantine without reviving authority.
    }
    internal FoundationCredentialScopedRecoveryStageReview GetCredentialScopedRecoveryStageReviewForExecution(IReadOnlyList<CustomerContext> executors, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadScopedRecoveryReview(db, tx, requestId);
        var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        RequireScopedRecoveryReviewApproved(db, tx, review, actor.Key); tx.Commit(); return review;
    }

    internal CredentialScopedRecoveryStageReviewAdmission AdmitCredentialScopedRecoveryStageReview(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedDigest, FoundationCredentialScopedRecoveryStageTarget actualTarget)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); var review = ReadScopedRecoveryReview(db, tx, requestId);
            var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
            MatchDigest(review.Digest, expectedDigest); RequireScopedRecoveryReviewApproved(db, tx, review, actor.Key);
            MatchDigest(review.TargetSha256, Hash(ScopedRecoveryTargetJson(actualTarget)));
            var admittedAt = At(Now);
            if (admittedAt < review.PreparedAt || admittedAt < review.ApprovedAt || admittedAt >= review.ExpiresAt ||
                admittedAt >= review.RequesterExpiresAt || admittedAt >= review.ApproverExpiresAt || admittedAt >= actor.ExpiresAt)
                throw new FoundationStoreException("ScopedRecoveryStageReviewAdmissionTimeInvalid", 403);
            var admission = new CredentialScopedRecoveryStageReviewAdmission(review, NewId(), actor.Key, actor.Version, review.DatabaseInstanceId);
            Exec(db, tx, """
                INSERT INTO credential_scoped_recovery_stage_review_executions VALUES($c,$id,$admission,$actor,$version,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", admission.AdmissionId), ("$actor", actor.Key), ("$version", actor.Version), ("$instance", review.DatabaseInstanceId), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewAdmitted", "OutcomeUnknown", "OneUseBeforeDestinationKeyAccess", review.Digest);
            _ = ReadScopedRecoveryReview(db, tx, review.Id); // Reject malformed persisted admission before committing or permitting destination-key access.
            commitAttempted = true; tx.Commit(); return admission;
        }
        catch (Exception) when (commitAttempted) { throw new FoundationStoreException("ScopedRecoveryStageReviewAdmissionUncertain", 503); }
    }

    internal FoundationCredentialScopedRecoveryStageReview CompleteCredentialScopedRecoveryStageReview(CredentialScopedRecoveryStageReviewAdmission admission,
        IReadOnlyList<CustomerContext> executors, CredentialScopedRecoveryStageVerification verification)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        var review = RequireScopedRecoveryReviewAdmission(db, tx, admission);
        var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        if (actor.Key != admission.ExecutorKey || actor.Version != admission.ExecutorVersion) throw new FoundationStoreException("ScopedRecoveryStageReviewExecutorChanged", 403);
        RequireScopedRecoveryReviewAuthority(db, tx, review);
        var completedAt = At(Now);
        if (completedAt < review.PreparedAt || completedAt < review.ApprovedAt || completedAt < review.Execution!.AdmittedAt ||
            completedAt >= review.ExpiresAt || completedAt >= review.RequesterExpiresAt || completedAt >= review.ApproverExpiresAt || completedAt >= actor.ExpiresAt)
            throw new FoundationStoreException("ScopedRecoveryStageReviewCompletionTimeInvalid", 403);
        if (verification is null || verification.PlanSha256 != review.Target.PlanSha256 || verification.ReceiptSha256 != review.Target.ReceiptSha256 ||
            verification.SourceSetSha256 != review.Target.SourceSetSha256 || !MaintenanceHash(verification.Sha256) ||
            verification.ObservedUtc < review.Execution!.AdmittedAt || verification.ObservedUtc > completedAt || !CredentialRecoverySets.Utc(verification.ObservedUtc) ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || !verification.OutputCiphertextsAuthenticated || !verification.DestinationAbsenceProofsAuthenticated ||
            verification.CompletionCoreSha256 != review.Target.CompletionCoreSha256 ||
            verification.AuthenticatedProfileCount != review.Target.ProfileCount || verification.AuthenticatedAbsenceCount != review.Target.AbsenceProofCount ||
            verification.Receipt.StageId != review.Target.StageId || verification.Receipt.DestinationKey != review.Target.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != review.Target.DestinationRootCommitmentSha256 ||
            verification.Receipt.StagedVaultNamespaceId != review.Target.StagedVaultNamespaceId || verification.Receipt.StagedAuthorityNamespaceId != review.Target.StagedAuthorityNamespaceId ||
            verification.Receipt.BindingSha256 != review.Target.BindingSha256 || verification.Receipt.StateSha256 != review.Target.StateSha256)
            throw new FoundationStoreException("ScopedRecoveryStageReviewVerificationMismatch");
        var result = new FoundationCredentialScopedRecoveryStageReviewResult(review.Digest, review.TargetSha256, verification.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.ObservedUtc);
        var json = ScopedRecoveryResultJson(result);
        Exec(db, tx, """
            UPDATE credential_scoped_recovery_stage_review_executions SET state='Succeeded',reason='HeldStageVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewCompleted", "Succeeded", "HeldEvidenceNoPromotionAuthority", Hash(json));
        var recorded = ReadScopedRecoveryReview(db, tx, review.Id);
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("ScopedRecoveryStageReviewCompletionUncertain", 503); }
    }
    internal FoundationCredentialScopedRecoveryStageReview RecordCredentialScopedRecoveryStageReviewUnknown(CredentialScopedRecoveryStageReviewAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = RequireScopedRecoveryReviewAdmission(db, tx, admission, permitSucceeded: true);
        if (review.Execution!.State == "Succeeded") { tx.Commit(); return review; }
        var reason = ScopedRecoveryReviewReason(reasonCode);
        Exec(db, tx, "UPDATE credential_scoped_recovery_stage_review_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", Now), ("$c", CustomerId), ("$id", review.Id));
        AuditScopedRecoveryReview(db, tx, review, admission.ExecutorKey, "CredentialScopedRecoveryReviewUnknown", "OutcomeUnknown", reason, review.Digest);
        var recorded = ReadScopedRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }
    internal void RecordCredentialScopedRecoveryStageReviewDenied(IReadOnlyList<CustomerContext> executors, string requestId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = ReadScopedRecoveryReview(db, tx, requestId);
        var actor = ScopedRecoveryContextIdentity(executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify);
        MatchDigest(review.Digest, expectedDigest);
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewDenied", "Denied", ScopedRecoveryReviewReason(reasonCode), review.Digest); tx.Commit();
    }

    void HoldRecoveredCredentialScopedRecoveryReviews(SqliteConnection db, SqliteTransaction tx)
    {
        var ids = Rows(db, tx, """
            SELECT q.id FROM credential_scoped_recovery_stage_review_requests q
            LEFT JOIN credential_scoped_recovery_stage_review_executions e ON e.customer_id=q.customer_id AND e.request_id=q.id
            WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND (e.request_id IS NULL OR e.state!='Succeeded') ORDER BY q.id LIMIT $limit
            """, r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialScopedRecoveryStageReviews + 1));
        if (ids.Count > MaximumCredentialScopedRecoveryStageReviews) throw new FoundationStoreException("ScopedRecoveryStageReviewRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var review = ReadScopedRecoveryReview(db, tx, id);
            Exec(db, tx, "UPDATE credential_scoped_recovery_stage_review_requests SET state='Quarantined' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", id));
            AuditScopedRecoveryReview(db, tx, review, "cell-maintenance", "RecoveredCredentialScopedRecoveryReviewHeld", "OutcomeUnknown", "ScopedRecoveryRequiresReconciliation", review.Digest);
        }
    }
}
