using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    internal FoundationCredentialScopedRecoveryStageReview GetCredentialScopedRecoveryJointReviewForExecution(IReadOnlyList<CustomerContext> executors, string reviewId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadScopedRecoveryReview(db, tx, reviewId);
        var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        RequireScopedRecoveryReviewApproved(db, tx, review, actor.Key); tx.Commit(); return review;
    }

    public FoundationCredentialScopedRecoveryJointVerification GetCredentialScopedRecoveryJointVerification(IReadOnlyList<CustomerContext> readers, string reviewId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var review = ReadScopedRecoveryReview(db, tx, reviewId);
        _ = ScopedRecoveryContexts(db, tx, readers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"]);
        var evidence = ReadScopedRecoveryJoint(db, tx, review); tx.Commit(); return evidence;
    }

    internal CredentialScopedRecoveryJointAdmission AdmitCredentialScopedRecoveryJointVerification(IReadOnlyList<CustomerContext> executors,
        string reviewId, string expectedReviewDigest, FoundationCredentialScopedRecoveryJointTarget actualTarget)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); var review = ReadScopedRecoveryReview(db, tx, reviewId);
            var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
            MatchDigest(review.Digest, expectedReviewDigest); RequireScopedRecoveryReviewApproved(db, tx, review, actor.Key);
            var targetJson = ScopedRecoveryJointTargetJson(actualTarget);
            MatchDigest(review.TargetSha256, Hash(ScopedRecoveryTargetJson(actualTarget.StageTarget)));
            var admittedAt = At(Now);
            if (admittedAt < review.PreparedAt || admittedAt < review.ApprovedAt || admittedAt < actualTarget.Checkpoint.AcceptedUtc ||
                admittedAt >= review.ExpiresAt || admittedAt >= review.RequesterExpiresAt || admittedAt >= review.ApproverExpiresAt || admittedAt >= actor.ExpiresAt)
                throw new FoundationStoreException("ScopedRecoveryJointAdmissionTimeInvalid", 403);
            var claim = new CredentialScopedRecoveryStageReviewAdmission(review, NewId(), actor.Key, actor.Version, review.DatabaseInstanceId);
            var targetSha = Hash(targetJson); var digest = ScopedRecoveryJointDigest(review, claim.AdmissionId, actor.Key, actor.Version, targetSha, admittedAt);
            // Consume the ordinary review too: old and joint coordinators share this one-use admission boundary.
            Exec(db, tx, """
                INSERT INTO credential_scoped_recovery_stage_review_executions VALUES($c,$id,$admission,$actor,$version,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", claim.AdmissionId), ("$actor", actor.Key), ("$version", actor.Version), ("$instance", review.DatabaseInstanceId), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            Exec(db, tx, """
                INSERT INTO credential_scoped_recovery_joint_executions VALUES($c,$id,$admission,$target,$sha,$digest,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", claim.AdmissionId), ("$target", targetJson), ("$sha", targetSha), ("$digest", digest), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewAdmitted", "OutcomeUnknown", "OneUseBeforeDestinationKeyAccess", review.Digest);
            AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryJointAdmitted", "OutcomeUnknown", "CurrentCheckpointAndStageHeld", digest);
            _ = ReadScopedRecoveryJoint(db, tx, ReadScopedRecoveryReview(db, tx, review.Id));
            commitAttempted = true; tx.Commit(); return new(claim, actualTarget, targetSha, digest);
        }
        catch (Exception) when (commitAttempted) { throw new FoundationStoreException("ScopedRecoveryJointAdmissionUncertain", 503); }
    }

    internal FoundationCredentialScopedRecoveryJointVerification CompleteCredentialScopedRecoveryJointVerification(CredentialScopedRecoveryJointAdmission admission,
        IReadOnlyList<CustomerContext> executors, FoundationCredentialScopedRecoveryJointTarget actualTarget, CredentialScopedRecoveryStageVerification verification)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        var review = RequireScopedRecoveryReviewAdmission(db, tx, admission.ReviewAdmission);
        var current = RequireScopedRecoveryJointAdmission(db, tx, admission, review);
        var actor = ScopedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"]);
        if (actor.Key != admission.ReviewAdmission.ExecutorKey || actor.Version != admission.ReviewAdmission.ExecutorVersion)
            throw new FoundationStoreException("ScopedRecoveryJointExecutorChanged", 403);
        RequireScopedRecoveryReviewAuthority(db, tx, review);
        MatchDigest(current.TargetSha256, Hash(ScopedRecoveryJointTargetJson(actualTarget)));
        var completedAt = At(Now);
        if (completedAt < review.PreparedAt || completedAt < review.ApprovedAt || completedAt < current.AdmittedAt || completedAt < actualTarget.Checkpoint.AcceptedUtc ||
            completedAt >= review.ExpiresAt || completedAt >= review.RequesterExpiresAt || completedAt >= review.ApproverExpiresAt || completedAt >= actor.ExpiresAt)
            throw new FoundationStoreException("ScopedRecoveryJointCompletionTimeInvalid", 403);
        RequireScopedRecoveryJointVerification(review, current, verification, completedAt);
        var ordinaryResult = new FoundationCredentialScopedRecoveryStageReviewResult(review.Digest, review.TargetSha256, verification.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.ObservedUtc);
        var ordinaryJson = ScopedRecoveryResultJson(ordinaryResult);
        var result = new FoundationCredentialScopedRecoveryJointResult(review.Digest, current.TargetSha256, verification.Sha256,
            current.Target.CheckpointAuthority.CommitmentSha256, current.Target.Checkpoint.Sequence, current.Target.Checkpoint.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.ObservedUtc);
        var json = ScopedRecoveryJointResultJson(result);
        Exec(db, tx, """
            UPDATE credential_scoped_recovery_stage_review_executions SET state='Succeeded',reason='HeldStageVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'
            """, ("$json", ordinaryJson), ("$sha", Hash(ordinaryJson)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        Exec(db, tx, """
            UPDATE credential_scoped_recovery_joint_executions SET state='Succeeded',reason='JointHeldVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND review_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryReviewCompleted", "Succeeded", "HeldEvidenceNoPromotionAuthority", Hash(ordinaryJson));
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryJointCompleted", "Succeeded", "CurrentCheckpointHeldNoPromotion", Hash(json));
        var recorded = ReadScopedRecoveryJoint(db, tx, ReadScopedRecoveryReview(db, tx, review.Id));
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("ScopedRecoveryJointCompletionUncertain", 503); }
    }

    internal FoundationCredentialScopedRecoveryJointVerification RecordCredentialScopedRecoveryJointUnknown(CredentialScopedRecoveryJointAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var review = RequireScopedRecoveryReviewAdmission(db, tx, admission.ReviewAdmission, permitSucceeded: true);
        var current = RequireScopedRecoveryJointAdmission(db, tx, admission, review);
        if (current.State == "Succeeded") { tx.Commit(); return current; }
        var reason = ScopedRecoveryReviewReason(reasonCode); var updated = Now;
        Exec(db, tx, "UPDATE credential_scoped_recovery_stage_review_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", updated), ("$c", CustomerId), ("$id", review.Id));
        Exec(db, tx, "UPDATE credential_scoped_recovery_joint_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND review_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", updated), ("$c", CustomerId), ("$id", review.Id));
        AuditScopedRecoveryReview(db, tx, review, admission.ReviewAdmission.ExecutorKey, "CredentialScopedRecoveryReviewUnknown", "OutcomeUnknown", reason, review.Digest);
        AuditScopedRecoveryReview(db, tx, review, admission.ReviewAdmission.ExecutorKey, "CredentialScopedRecoveryJointUnknown", "OutcomeUnknown", reason, current.Digest);
        var recorded = ReadScopedRecoveryJoint(db, tx, ReadScopedRecoveryReview(db, tx, review.Id)); tx.Commit(); return recorded;
    }

    internal void RecordCredentialScopedRecoveryJointDenied(IReadOnlyList<CustomerContext> executors, string reviewId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var review = ReadScopedRecoveryReview(db, tx, reviewId);
        var actor = ScopedRecoveryContextIdentity(executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify); MatchDigest(review.Digest, expectedDigest);
        AuditScopedRecoveryReview(db, tx, review, actor.Key, "CredentialScopedRecoveryJointDenied", "Denied", ScopedRecoveryReviewReason(reasonCode), review.Digest); tx.Commit();
    }

    void HoldRecoveredCredentialScopedRecoveryJointVerifications(SqliteConnection db, SqliteTransaction tx)
    {
        var ids = Rows(db, tx, "SELECT review_id FROM credential_scoped_recovery_joint_executions WHERE customer_id=$c AND state='OutcomeUnknown' ORDER BY review_id LIMIT $limit",
            r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialScopedRecoveryStageReviews + 1));
        if (ids.Count > MaximumCredentialScopedRecoveryStageReviews) throw new FoundationStoreException("ScopedRecoveryJointRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var review = ReadScopedRecoveryReview(db, tx, id); var evidence = ReadScopedRecoveryJoint(db, tx, review);
            if (!evidence.Quarantined) throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed");
            // Keep both original consumed outcomes unchanged. The retained v9 request is already quarantined and instance-fenced.
            AuditScopedRecoveryReview(db, tx, review, "cell-maintenance", "RecoveredCredentialScopedRecoveryJointHeld", "OutcomeUnknown", "RecoveryRequiresReconciliation", evidence.Digest);
        }
    }
}
