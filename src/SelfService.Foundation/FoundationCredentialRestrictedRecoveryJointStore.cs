using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    internal FoundationCredentialRestrictedRecoveryStageReview GetCredentialRestrictedRecoveryJointReviewForExecution(IReadOnlyList<CustomerContext> executors, string reviewId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, reviewId);
        var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
        RequireRestrictedRecoveryReviewApproved(db, tx, review, actor.Key, now); tx.Commit(); return review;
    }

    public FoundationCredentialRestrictedRecoveryJointVerification GetCredentialRestrictedRecoveryJointVerification(IReadOnlyList<CustomerContext> readers, string reviewId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, reviewId);
        _ = RestrictedRecoveryContexts(db, tx, readers, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"], now);
        var evidence = ReadRestrictedRecoveryJoint(db, tx, review); tx.Commit(); return evidence;
    }

    internal CredentialRestrictedRecoveryJointAdmission AdmitCredentialRestrictedRecoveryJointVerification(IReadOnlyList<CustomerContext> executors,
        string reviewId, string expectedReviewDigest, FoundationCredentialRestrictedRecoveryJointTarget actualTarget)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, reviewId);
            var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
            MatchDigest(review.Digest, expectedReviewDigest); RequireRestrictedRecoveryReviewApproved(db, tx, review, actor.Key, now);
            var targetJson = RestrictedRecoveryJointTargetJson(actualTarget);
            MatchDigest(review.TargetSha256, Hash(RestrictedRecoveryTargetJson(actualTarget.StageTarget)));
            var admittedAt = now;
            if (admittedAt < review.PreparedAt || admittedAt < review.ApprovedAt || admittedAt < actualTarget.Checkpoint.AcceptedUtc ||
                admittedAt >= review.ExpiresAt || admittedAt >= review.RequesterExpiresAt || admittedAt >= review.ApproverExpiresAt || admittedAt >= actor.ExpiresAt)
                throw new FoundationStoreException("RestrictedRecoveryJointAdmissionTimeInvalid", 403);
            var claim = new CredentialRestrictedRecoveryStageReviewAdmission(review, NewId(), actor.Key, actor.Version, review.DatabaseInstanceId);
            var targetSha = Hash(targetJson); var digest = RestrictedRecoveryJointDigest(review, claim.AdmissionId, actor.Key, actor.Version, targetSha, admittedAt);
            // Consume the ordinary review too: old and joint coordinators share this one-use admission boundary.
            Exec(db, tx, """
                INSERT INTO credential_restricted_recovery_stage_review_executions VALUES($c,$id,$admission,$actor,$version,$instance,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", claim.AdmissionId), ("$actor", actor.Key), ("$version", actor.Version), ("$instance", review.DatabaseInstanceId), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            Exec(db, tx, """
                INSERT INTO credential_restricted_recovery_joint_executions VALUES($c,$id,$admission,$target,$sha,$digest,'OutcomeUnknown','AdmittedBeforeVerification',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", review.Id), ("$admission", claim.AdmissionId), ("$target", targetJson), ("$sha", targetSha), ("$digest", digest), ("$now", admittedAt.ToUnixTimeMilliseconds()));
            AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewAdmitted", "OutcomeUnknown", "OneUseBeforeDestinationKeyAccess", review.Digest, now);
            AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryJointAdmitted", "OutcomeUnknown", "CurrentCheckpointAndStageHeld", digest, now);
            _ = ReadRestrictedRecoveryJoint(db, tx, ReadRestrictedRecoveryReview(db, tx, review.Id));
            commitAttempted = true; tx.Commit(); return new(claim, actualTarget, targetSha, digest);
        }
        catch (Exception) when (commitAttempted) { throw new FoundationStoreException("RestrictedRecoveryJointAdmissionUncertain", 503); }
    }

    internal FoundationCredentialRestrictedRecoveryJointVerification CompleteCredentialRestrictedRecoveryJointVerification(CredentialRestrictedRecoveryJointAdmission admission,
        IReadOnlyList<CustomerContext> executors, FoundationCredentialRestrictedRecoveryJointTarget actualTarget, CredentialRestrictedRecoveryStageVerification verification,
        CredentialRestrictedRecoveryJointSession held, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new FoundationStoreException("RestrictedRecoveryJointWindowsRequired", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        var now = At(Now); var review = RequireRestrictedRecoveryReviewAdmission(db, tx, admission.ReviewAdmission);
        var current = RequireRestrictedRecoveryJointAdmission(db, tx, admission, review);
        var actor = RestrictedRecoveryContexts(db, tx, executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify, ["Operator"], now);
        if (actor.Key != admission.ReviewAdmission.ExecutorKey || actor.Version != admission.ReviewAdmission.ExecutorVersion)
            throw new FoundationStoreException("RestrictedRecoveryJointExecutorChanged", 403);
        RequireRestrictedRecoveryReviewAuthority(db, tx, review, now);
        MatchDigest(current.TargetSha256, Hash(RestrictedRecoveryJointTargetJson(actualTarget)));
        if (held is null || verification is null || held.Identity != current.Target.CheckpointAuthority || held.Checkpoint != current.Target.Checkpoint ||
            held.Receipt.Sha256 != current.Target.StageTarget.ReceiptSha256 || !ReferenceEquals(held.Receipt, verification.Receipt))
            throw new FoundationStoreException("RestrictedRecoveryJointVerificationMismatch");
        var completedAt = now;
        if (completedAt < review.PreparedAt || completedAt < review.ApprovedAt || completedAt < current.AdmittedAt || completedAt < actualTarget.Checkpoint.AcceptedUtc ||
            completedAt >= review.ExpiresAt || completedAt >= review.RequesterExpiresAt || completedAt >= review.ApproverExpiresAt || completedAt >= actor.ExpiresAt)
            throw new FoundationStoreException("RestrictedRecoveryJointCompletionTimeInvalid", 403);
        RequireRestrictedRecoveryJointVerification(review, current, verification, completedAt);
        var ordinaryResult = new FoundationCredentialRestrictedRecoveryStageReviewResult(review.Digest, review.TargetSha256, verification.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.CompletionProofSha256, verification.ObservedUtc);
        var ordinaryJson = RestrictedRecoveryResultJson(ordinaryResult);
        var result = new FoundationCredentialRestrictedRecoveryJointResult(review.Digest, current.TargetSha256, verification.Sha256,
            current.Target.CheckpointAuthority.CommitmentSha256, current.Target.Checkpoint.Sequence, current.Target.Checkpoint.Sha256,
            verification.PlanSha256, verification.ReceiptSha256, verification.SourceSetSha256, verification.CompletionProofSha256, verification.ObservedUtc);
        var json = RestrictedRecoveryJointResultJson(result);
        Exec(db, tx, """
            UPDATE credential_restricted_recovery_stage_review_executions SET state='Succeeded',reason='HeldStageVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'
            """, ("$json", ordinaryJson), ("$sha", Hash(ordinaryJson)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        Exec(db, tx, """
            UPDATE credential_restricted_recovery_joint_executions SET state='Succeeded',reason='JointHeldVerificationRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND review_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", completedAt.ToUnixTimeMilliseconds()), ("$c", CustomerId), ("$id", review.Id));
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryReviewCompleted", "Succeeded", "HeldEvidenceNoPromotionAuthority", Hash(ordinaryJson), now);
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryJointCompleted", "Succeeded", "CurrentCheckpointHeldNoPromotion", Hash(json), now);
        var recorded = ReadRestrictedRecoveryJoint(db, tx, ReadRestrictedRecoveryReview(db, tx, review.Id));
        held.AssertVerifiedCurrent(verification, now, cancellationToken); // Both actual guards and exact session result; no clock callback after SQL authority sampling.
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("RestrictedRecoveryJointCompletionUncertain", 503); }
    }

    internal FoundationCredentialRestrictedRecoveryJointVerification RecordCredentialRestrictedRecoveryJointUnknown(CredentialRestrictedRecoveryJointAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = At(Now);
        var review = RequireRestrictedRecoveryReviewAdmission(db, tx, admission.ReviewAdmission, permitSucceeded: true);
        var current = RequireRestrictedRecoveryJointAdmission(db, tx, admission, review);
        if (current.State == "Succeeded") { tx.Commit(); return current; }
        var reason = RestrictedRecoveryReviewReason(reasonCode); var updated = now.ToUnixTimeMilliseconds();
        Exec(db, tx, "UPDATE credential_restricted_recovery_stage_review_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND request_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", updated), ("$c", CustomerId), ("$id", review.Id));
        Exec(db, tx, "UPDATE credential_restricted_recovery_joint_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND review_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", updated), ("$c", CustomerId), ("$id", review.Id));
        AuditRestrictedRecoveryReviewAt(db, tx, review, admission.ReviewAdmission.ExecutorKey, "CredentialRestrictedRecoveryReviewUnknown", "OutcomeUnknown", reason, review.Digest, now);
        AuditRestrictedRecoveryReviewAt(db, tx, review, admission.ReviewAdmission.ExecutorKey, "CredentialRestrictedRecoveryJointUnknown", "OutcomeUnknown", reason, current.Digest, now);
        var recorded = ReadRestrictedRecoveryJoint(db, tx, ReadRestrictedRecoveryReview(db, tx, review.Id)); tx.Commit(); return recorded;
    }

    internal void RecordCredentialRestrictedRecoveryJointDenied(IReadOnlyList<CustomerContext> executors, string reviewId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var now = At(Now); var review = ReadRestrictedRecoveryReview(db, tx, reviewId);
        var actor = RestrictedRecoveryContextIdentity(executors, review.Target.Scopes, FoundationCapabilities.CredentialRecoveryVerify); MatchDigest(review.Digest, expectedDigest);
        AuditRestrictedRecoveryReviewAt(db, tx, review, actor.Key, "CredentialRestrictedRecoveryJointDenied", "Denied", RestrictedRecoveryReviewReason(reasonCode), review.Digest, now); tx.Commit();
    }

    void HoldRecoveredCredentialRestrictedRecoveryJointVerifications(SqliteConnection db, SqliteTransaction tx)
    {
        var now = At(Now);
        var ids = Rows(db, tx, "SELECT review_id FROM credential_restricted_recovery_joint_executions WHERE customer_id=$c AND state='OutcomeUnknown' ORDER BY review_id LIMIT $limit",
            r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialRestrictedRecoveryStageReviews + 1));
        if (ids.Count > MaximumCredentialRestrictedRecoveryStageReviews) throw new FoundationStoreException("RestrictedRecoveryJointRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var review = ReadRestrictedRecoveryReview(db, tx, id); var evidence = ReadRestrictedRecoveryJoint(db, tx, review);
            if (!evidence.Quarantined) throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed");
            // Keep both original consumed outcomes unchanged. The retained v13 request is already quarantined and instance-fenced.
            AuditRestrictedRecoveryReviewAt(db, tx, review, "cell-maintenance", "RecoveredCredentialRestrictedRecoveryJointHeld", "OutcomeUnknown", "RecoveryRequiresReconciliation", evidence.Digest, now);
        }
    }
}
