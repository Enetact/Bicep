using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialRestrictedRecoveryStageReviews = 2048, MaximumPendingCredentialRestrictedRecoveryStageReviews = 64;
    public const int MaximumCredentialRestrictedRecoveryScopes = 256, MaximumCredentialRestrictedRecoveryTargetBytes = 131072;
    sealed record RestrictedRecoveryActor(string Key, long Version, DateTimeOffset ExpiresAt);

    internal void RequireCredentialRestrictedRecoveryStageReviewPreparation(IReadOnlyList<CustomerContext> contexts, CredentialRestrictedRecoveryStagePlan plan)
    {
        var scopes = RestrictedRecoveryScopes(plan); using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        var now = At(Now);
        _ = RestrictedRecoveryContexts(db, tx, contexts, scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"], now);
        RestrictedRecoveryReviewLifecycle(db, tx); tx.Commit();
    }

    public FoundationCredentialRestrictedRecoveryStageReview PrepareCredentialRestrictedRecoveryStageReview(IReadOnlyList<CustomerContext> requesters,
        CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt receipt, string purpose, DateTimeOffset expiresUtc)
    {
        var target = CredentialRestrictedRecoveryStageReviewTarget(plan, receipt);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var now = At(Now);
        var actor = RestrictedRecoveryContexts(db, tx, requesters, target.Scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"], now);
        RestrictedRecoveryReviewLifecycle(db, tx); ValidatePurpose(purpose);
        var preparedAt = now; var expires = At(expiresUtc.ToUnixTimeMilliseconds());
        if (expires <= preparedAt || expires > preparedAt.AddMinutes(15) || expires > actor.ExpiresAt || receipt.CompletedUtc > preparedAt)
            throw new FoundationStoreException("RestrictedRecoveryStageReviewExpiryInvalid", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_restricted_recovery_stage_review_requests WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialRestrictedRecoveryStageReviews)
            throw new FoundationStoreException("RestrictedRecoveryStageReviewHistoryCapacityExceeded", 429);
        if (Number(db, tx, """
            SELECT count(*) FROM credential_restricted_recovery_stage_review_requests q WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND q.expires>$now
            AND NOT EXISTS(SELECT 1 FROM credential_restricted_recovery_stage_review_executions e WHERE e.customer_id=q.customer_id AND e.request_id=q.id)
            """, ("$c", CustomerId), ("$now", preparedAt.ToUnixTimeMilliseconds())) >= MaximumPendingCredentialRestrictedRecoveryStageReviews)
            throw new FoundationStoreException("RestrictedRecoveryStageReviewPendingCapacityExceeded", 429);
        if (target.SourceNamespace.CustomerId != CustomerId || target.SourceNamespace.CellId != CellId) throw new FoundationStoreException("RestrictedRecoveryStageReviewCellMismatch", 403);
        var json = RestrictedRecoveryTargetJson(target); var scope = target.Scopes[0];
        var review = new FoundationCredentialRestrictedRecoveryStageReview(NewId(), CustomerId, CellId, scope.ProjectId, scope.EnvironmentId,
            target, Hash(json), actor.Key, actor.Version, actor.ExpiresAt, Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"),
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), Hash(purpose), expires, preparedAt, "", "Prepared", null, null, null, null, null);
        review = review with { Digest = RestrictedRecoveryReviewDigest(review) };
        Exec(db, tx, """
            INSERT INTO credential_restricted_recovery_stage_review_requests VALUES($c,$id,$p,$e,$target,$sha,$actor,$version,$actorExpires,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now)
            """, ("$c", CustomerId), ("$id", review.Id), ("$p", scope.ProjectId), ("$e", scope.EnvironmentId), ("$target", json), ("$sha", review.TargetSha256),
            ("$actor", actor.Key), ("$version", actor.Version), ("$actorExpires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$policy", review.PolicyVersion),
            ("$instance", review.DatabaseInstanceId), ("$purpose", review.PurposeSha256), ("$expires", expires.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", review.PreparedAt.ToUnixTimeMilliseconds()));
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewPrepared", "Prepared", "AllScopeExactReviewRequired", review.Digest, now);
        var recorded = ReadRestrictedRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }

    public FoundationCredentialRestrictedRecoveryStageReview ApproveCredentialRestrictedRecoveryStageReview(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, requestId);
        var actor = RestrictedRecoveryContexts(db, tx, approvers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryApprove, ["Approver"], now);
        MatchDigest(review.Digest, expectedDigest);
        if (review.State != "Prepared" || review.ApproverKey is not null || review.Execution is not null) throw new FoundationStoreException("RestrictedRecoveryStageReviewAlreadyApproved");
        if (actor.Key == review.RequesterKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        RequireRestrictedRecoveryReviewAuthority(db, tx, review, now);
        Exec(db, tx, "INSERT INTO credential_restricted_recovery_stage_review_approvals VALUES($c,$id,$actor,$version,$expires,$digest,$now)",
            ("$c", CustomerId), ("$id", review.Id), ("$actor", actor.Key), ("$version", actor.Version), ("$expires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$digest", review.Digest), ("$now", now.ToUnixTimeMilliseconds()));
        Exec(db, tx, "UPDATE credential_restricted_recovery_stage_review_requests SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", review.Id));
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewApproved", "Approved", "DistinctAllScopeApprover", review.Digest, now);
        var result = ReadRestrictedRecoveryReview(db, tx, review.Id); tx.Commit(); return result;
    }

    public FoundationCredentialRestrictedRecoveryStageReview GetCredentialRestrictedRecoveryStageReview(IReadOnlyList<CustomerContext> readers, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, requestId);
        _ = RestrictedRecoveryContexts(db, tx, readers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"], now);
        tx.Commit(); return review; // Static-integrity history remains readable after expiry/quarantine without reviving authority.
    }
    internal FoundationCredentialRestrictedRecoveryStageReview GetCredentialRestrictedRecoveryStageReviewForExecution(IReadOnlyList<CustomerContext> executors, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, requestId);
        var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
        RequireRestrictedRecoveryReviewApproved(db, tx, review, actor.Key, now); tx.Commit(); return review;
    }

    internal CredentialRestrictedRecoveryStageReviewAdmission AdmitCredentialRestrictedRecoveryStageReview(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedDigest, FoundationCredentialRestrictedRecoveryStageTarget actualTarget)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, requestId);
            var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
            MatchDigest(review.Digest, expectedDigest); RequireRestrictedRecoveryReviewApproved(db, tx, review, actor.Key, now);
            MatchDigest(review.TargetSha256, Hash(RestrictedRecoveryTargetJson(actualTarget)));
            var admittedAt = now;
            if (admittedAt < review.PreparedAt || admittedAt < review.ApprovedAt || admittedAt >= review.ExpiresAt ||
                admittedAt >= review.RequesterExpiresAt || admittedAt >= review.ApproverExpiresAt || admittedAt >= actor.ExpiresAt)
                throw new FoundationStoreException("RestrictedRecoveryStageReviewAdmissionTimeInvalid", 403);
            var admission = new CredentialRestrictedRecoveryStageReviewAdmission(review, NewId(), actor.Key, actor.Version, review.DatabaseInstanceId);
            Exec(db, tx, """
                INSERT INTO credential_restricted_recovery_stage_review_executions VALUES($c,$id,$admission,$actor,$version,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", admission.AdmissionId), ("$actor", actor.Key), ("$version", actor.Version), ("$instance", review.DatabaseInstanceId), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewAdmitted", "OutcomeUnknown", "OneUseBeforeDestinationKeyAccess", review.Digest, now);
            _ = ReadRestrictedRecoveryReview(db, tx, review.Id); // Reject malformed persisted admission before committing or permitting destination-key access.
            commitAttempted = true; tx.Commit(); return admission;
        }
        catch (Exception) when (commitAttempted) { throw new FoundationStoreException("RestrictedRecoveryStageReviewAdmissionUncertain", 503); }
    }

    internal FoundationCredentialRestrictedRecoveryStageReview CompleteCredentialRestrictedRecoveryStageReview(CredentialRestrictedRecoveryStageReviewAdmission admission,
        IReadOnlyList<CustomerContext> executors, CredentialRestrictedRecoveryStageVerification verification,
        CredentialRestrictedRecoveryStageVerifier.HeldVerification held, CancellationToken cancellationToken)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        var now = At(Now); var review = RequireRestrictedRecoveryReviewAdmission(db, tx, admission);
        var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
        if (actor.Key != admission.ExecutorKey || actor.Version != admission.ExecutorVersion) throw new FoundationStoreException("RestrictedRecoveryStageReviewExecutorChanged", 403);
        RequireRestrictedRecoveryReviewAuthority(db, tx, review, now);
        var completedAt = now;
        if (completedAt < review.PreparedAt || completedAt < review.ApprovedAt || completedAt < review.Execution!.AdmittedAt ||
            completedAt >= review.ExpiresAt || completedAt >= review.RequesterExpiresAt || completedAt >= review.ApproverExpiresAt || completedAt >= actor.ExpiresAt)
            throw new FoundationStoreException("RestrictedRecoveryStageReviewCompletionTimeInvalid", 403);
        if (verification is null || verification.PlanSha256 != review.Target.PlanSha256 || verification.ReceiptSha256 != review.Target.ReceiptSha256 ||
            verification.SourceSetSha256 != review.Target.SourceSetSha256 || !MaintenanceHash(verification.Sha256) ||
            verification.ObservedUtc < review.Execution!.AdmittedAt || verification.ObservedUtc > completedAt || !CredentialRecoverySets.Utc(verification.ObservedUtc) ||
            verification.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || verification.Receipt.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || !verification.OutputCiphertextsAuthenticated || !verification.DestinationAbsenceProofsAuthenticated ||
            !verification.DestinationCompletionProofAuthenticated || verification.CompletionProofSha256 != review.Target.CompletionProofSha256 ||
            verification.Receipt.CompletionProofSha256 != review.Target.CompletionProofSha256 ||
            verification.CompletionCoreSha256 != review.Target.CompletionCoreSha256 ||
            verification.AuthenticatedProfileCount != review.Target.ProfileCount || verification.AuthenticatedAbsenceCount != review.Target.AbsenceProofCount ||
            verification.Receipt.StageId != review.Target.StageId || verification.Receipt.DestinationKey != review.Target.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != review.Target.DestinationRootCommitmentSha256 ||
            verification.Receipt.StagedVaultNamespaceId != review.Target.StagedVaultNamespaceId || verification.Receipt.StagedAuthorityNamespaceId != review.Target.StagedAuthorityNamespaceId ||
            verification.Receipt.BindingSha256 != review.Target.BindingSha256 || verification.Receipt.StateSha256 != review.Target.StateSha256)
            throw new FoundationStoreException("RestrictedRecoveryStageReviewVerificationMismatch");
        var result = new FoundationCredentialRestrictedRecoveryStageReviewResult(review.Digest, review.TargetSha256, verification.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.CompletionProofSha256, verification.ObservedUtc);
        var json = RestrictedRecoveryResultJson(result);
        Exec(db, tx, """
            UPDATE credential_restricted_recovery_stage_review_executions SET state='Succeeded',reason='HeldStageVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewCompleted", "Succeeded", "HeldEvidenceNoPromotionAuthority", Hash(json), now);
        var recorded = ReadRestrictedRecoveryReview(db, tx, review.Id);
        held.AssertVerifiedCurrent(verification, cancellationToken); // Actual session/result, clock-free snapshot and cancellation LAST.
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("RestrictedRecoveryStageReviewCompletionUncertain", 503); }
    }
    internal FoundationCredentialRestrictedRecoveryStageReview RecordCredentialRestrictedRecoveryStageReviewUnknown(CredentialRestrictedRecoveryStageReviewAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = At(Now); var review = RequireRestrictedRecoveryReviewAdmission(db, tx, admission, permitSucceeded: true);
        if (review.Execution!.State == "Succeeded") { tx.Commit(); return review; }
        var reason = RestrictedRecoveryReviewReason(reasonCode);
        Exec(db, tx, "UPDATE credential_restricted_recovery_stage_review_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", now.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditRestrictedRecoveryReviewAt(db, tx, review, admission.ExecutorKey, "CredentialRestrictedRecoveryReviewUnknown", "OutcomeUnknown", reason, review.Digest, now);
        var recorded = ReadRestrictedRecoveryReview(db, tx, review.Id); tx.Commit(); return recorded;
    }
    internal void RecordCredentialRestrictedRecoveryStageReviewDenied(IReadOnlyList<CustomerContext> executors, string requestId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, requestId);
        var actor = RestrictedRecoveryContextIdentity(executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify);
        MatchDigest(review.Digest, expectedDigest);
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewDenied", "Denied", RestrictedRecoveryReviewReason(reasonCode), review.Digest, now); tx.Commit();
    }

    void HoldRecoveredCredentialRestrictedRecoveryReviews(SqliteConnection db, SqliteTransaction tx)
    {
        var now = At(Now);
        var ids = Rows(db, tx, """
            SELECT q.id FROM credential_restricted_recovery_stage_review_requests q
            LEFT JOIN credential_restricted_recovery_stage_review_executions e ON e.customer_id=q.customer_id AND e.request_id=q.id
            WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND (e.request_id IS NULL OR e.state!='Succeeded') ORDER BY q.id LIMIT $limit
            """, r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialRestrictedRecoveryStageReviews + 1));
        if (ids.Count > MaximumCredentialRestrictedRecoveryStageReviews) throw new FoundationStoreException("RestrictedRecoveryStageReviewRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var review = ReadRestrictedRecoveryReview(db, tx, id);
            Exec(db, tx, "UPDATE credential_restricted_recovery_stage_review_requests SET state='Quarantined' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", id));
            AuditRestrictedRecoveryReviewAt(db, tx, review, "cell-maintenance", "RecoveredCredentialRestrictedRecoveryReviewHeld", "OutcomeUnknown", "RestrictedRecoveryRequiresReconciliation", review.Digest, now);
        }
    }
}
