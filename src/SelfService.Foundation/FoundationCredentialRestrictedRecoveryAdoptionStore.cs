using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    internal void RequireCredentialRestrictedRecoveryAdoptionPreparation(IReadOnlyList<CustomerContext> requesters, CredentialRestrictedRecoveryStagePlan plan)
    {
        var scopes = RestrictedRecoveryScopes(plan); using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now);
        _ = RestrictedRecoveryAdoptionContexts(db, tx, requesters, scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"], now);
        RestrictedRecoveryReviewLifecycle(db, tx); _ = RestrictedRecoveryAdoptionSource(db, tx, plan.SourceNamespace); tx.Commit();
    }

    internal FoundationCredentialRestrictedRecoveryAdoptionIntent PrepareCredentialRestrictedRecoveryAdoptionIntent(IReadOnlyList<CustomerContext> requesters,
        CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageVerification verification, CredentialRestrictedRecoveryAdoptionSession held,
        string purpose, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new FoundationStoreException("RestrictedRecoveryAdoptionWindowsRequired", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx); var now = At(Now);
        var scopes = RestrictedRecoveryScopes(plan);
        var actor = RestrictedRecoveryAdoptionContexts(db, tx, requesters, scopes, FoundationCapabilities.CredentialRecoveryPrepare, ["Operator"], now);
        RestrictedRecoveryReviewLifecycle(db, tx); ValidatePurpose(purpose);
        var target = CaptureRestrictedRecoveryAdoptionTarget(db, tx, plan, held);
        var earliest = new[] { target.RecoveryHold.CreatedAt, target.RecoveryTarget.Checkpoint.AcceptedUtc, held.Receipt.CompletedUtc }.Max();
        RequireRestrictedRecoveryAdoptionVerification(target, verification, earliest, now);
        if (!ReferenceEquals(held.Receipt, verification.Receipt)) throw new FoundationStoreException("RestrictedRecoveryAdoptionVerificationMismatch", 403);
        var expires = At(expiresUtc.ToUnixTimeMilliseconds());
        if (expires <= now || expires > now.AddMinutes(15) || expires > actor.ExpiresAt) throw new FoundationStoreException("RestrictedRecoveryAdoptionExpiryInvalid", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_restricted_recovery_adoption_intents WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialRestrictedRecoveryAdoptionIntents)
            throw new FoundationStoreException("RestrictedRecoveryAdoptionHistoryCapacityExceeded", 429);
        if (Number(db, tx, "SELECT count(*) FROM credential_restricted_recovery_adoption_intents WHERE customer_id=$c AND state IN ('Prepared','Approved') AND expires>$now",
            ("$c", CustomerId), ("$now", now.ToUnixTimeMilliseconds())) >= MaximumPendingCredentialRestrictedRecoveryAdoptionIntents)
            throw new FoundationStoreException("RestrictedRecoveryAdoptionPendingCapacityExceeded", 429);
        var json = RestrictedRecoveryAdoptionTargetJson(target);
        var intent = new FoundationCredentialRestrictedRecoveryAdoptionIntent(NewId(), CustomerId, CellId, target, Hash(json), actor.Key, actor.Version, actor.ExpiresAt,
            Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"), target.DatabaseInstanceId, Hash(purpose), expires, now,
            verification.Sha256, verification.ObservedUtc, "", "Prepared", null);
        intent = intent with { Digest = RestrictedRecoveryAdoptionDigest(intent) }; var scope = scopes[0];
        Exec(db, tx, """
            INSERT INTO credential_restricted_recovery_adoption_intents VALUES($c,$id,$p,$e,$target,$sha,$actor,$version,$actorExpires,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now,$verification,$observed)
            """, ("$c", CustomerId), ("$id", intent.Id), ("$p", scope.ProjectId), ("$e", scope.EnvironmentId), ("$target", json), ("$sha", intent.TargetSha256),
            ("$actor", actor.Key), ("$version", actor.Version), ("$actorExpires", actor.ExpiresAt.ToUnixTimeMilliseconds()), ("$policy", intent.PolicyVersion),
            ("$instance", intent.DatabaseInstanceId), ("$purpose", intent.PurposeSha256), ("$expires", expires.ToUnixTimeMilliseconds()), ("$digest", intent.Digest),
            ("$now", now.ToUnixTimeMilliseconds()), ("$verification", verification.Sha256), ("$observed", verification.ObservedUtc.ToUnixTimeMilliseconds()));
        AuditRestrictedRecoveryAdoptionAt(db, tx, intent, actor.Key, "CredentialRestrictedRecoveryAdoptionPrepared", "Prepared", "ChangingIntentOnlyNoExecutionGrant", now);
        var recorded = ReadRestrictedRecoveryAdoptionIntent(db, tx, intent.Id);
        held.AssertVerifiedCurrent(verification, now, cancellationToken);
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("RestrictedRecoveryAdoptionPreparationUncertain", 503); }
    }

    internal FoundationCredentialRestrictedRecoveryAdoptionIntent GetCredentialRestrictedRecoveryAdoptionIntentForApproval(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var intent = ReadRestrictedRecoveryAdoptionIntent(db, tx, requestId);
        var actor = RestrictedRecoveryAdoptionContexts(db, tx, approvers, intent.Target.RecoveryTarget.StageTarget.Scopes, FoundationCapabilities.CredentialRecoveryApprove, ["Approver"], now);
        RequireRestrictedRecoveryAdoptionApprover(db, tx, intent, actor.Key, expectedDigest, now);
        RequireCurrentRestrictedRecoveryAdoptionSource(db, tx, intent.Target); tx.Commit(); return intent;
    }

    internal FoundationCredentialRestrictedRecoveryAdoptionIntent ApproveCredentialRestrictedRecoveryAdoptionIntent(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest, CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageVerification verification,
        CredentialRestrictedRecoveryAdoptionSession held, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new FoundationStoreException("RestrictedRecoveryAdoptionWindowsRequired", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx); var now = At(Now);
        var intent = ReadRestrictedRecoveryAdoptionIntent(db, tx, requestId);
        var actor = RestrictedRecoveryAdoptionContexts(db, tx, approvers, intent.Target.RecoveryTarget.StageTarget.Scopes, FoundationCapabilities.CredentialRecoveryApprove, ["Approver"], now);
        RequireRestrictedRecoveryAdoptionApprover(db, tx, intent, actor.Key, expectedDigest, now);
        var current = CaptureRestrictedRecoveryAdoptionTarget(db, tx, plan, held);
        if (Hash(RestrictedRecoveryAdoptionTargetJson(current)) != intent.TargetSha256) throw new FoundationStoreException("RestrictedRecoveryAdoptionTargetChanged", 403);
        RequireRestrictedRecoveryAdoptionVerification(current, verification, intent.PreparedAt, now);
        if (!ReferenceEquals(held.Receipt, verification.Receipt)) throw new FoundationStoreException("RestrictedRecoveryAdoptionVerificationMismatch", 403);
        Exec(db, tx, "INSERT INTO credential_restricted_recovery_adoption_approvals VALUES($c,$id,$actor,$version,$expires,$digest,$verification,$observed,$now)",
            ("$c", CustomerId), ("$id", intent.Id), ("$actor", actor.Key), ("$version", actor.Version), ("$expires", actor.ExpiresAt.ToUnixTimeMilliseconds()),
            ("$digest", intent.Digest), ("$verification", verification.Sha256), ("$observed", verification.ObservedUtc.ToUnixTimeMilliseconds()), ("$now", now.ToUnixTimeMilliseconds()));
        Exec(db, tx, "UPDATE credential_restricted_recovery_adoption_intents SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", intent.Id));
        AuditRestrictedRecoveryAdoptionAt(db, tx, intent, actor.Key, "CredentialRestrictedRecoveryAdoptionApproved", "Approved", "DistinctHumanIntentOnly", now);
        var recorded = ReadRestrictedRecoveryAdoptionIntent(db, tx, intent.Id);
        held.AssertVerifiedCurrent(verification, now, cancellationToken);
        try { tx.Commit(); return recorded; }
        catch { throw new FoundationStoreException("RestrictedRecoveryAdoptionApprovalUncertain", 503); }
    }

    public FoundationCredentialRestrictedRecoveryAdoptionIntent GetCredentialRestrictedRecoveryAdoptionIntent(IReadOnlyList<CustomerContext> readers, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); var now = At(Now); var intent = ReadRestrictedRecoveryAdoptionIntent(db, tx, requestId);
        _ = RestrictedRecoveryAdoptionContexts(db, tx, readers, intent.Target.RecoveryTarget.StageTarget.Scopes, FoundationCapabilities.CredentialRecoveryRead, ["Operator", "Approver", "Auditor"], now);
        tx.Commit(); return intent;
    }
    void RequireRestrictedRecoveryAdoptionApprover(SqliteConnection db, SqliteTransaction tx, FoundationCredentialRestrictedRecoveryAdoptionIntent intent,
        string approver, string expectedDigest, DateTimeOffset now)
    {
        MatchDigest(intent.Digest, expectedDigest);
        if (intent.State != "Prepared" || intent.Approval is not null) throw new FoundationStoreException("RestrictedRecoveryAdoptionApprovalUnavailable", 403);
        if (intent.RequesterKey == approver) throw new FoundationStoreException("SelfApprovalDenied", 403);
        RequireRestrictedRecoveryAdoptionAuthority(db, tx, intent, now);
    }
    void RequireCurrentRestrictedRecoveryAdoptionSource(SqliteConnection db, SqliteTransaction tx, FoundationCredentialRestrictedRecoveryAdoptionTarget target)
    {
        var source = RestrictedRecoveryAdoptionSource(db, tx, target.RecoveryTarget.StageTarget.SourceNamespace);
        if (source.Hold != target.RecoveryHold || source.HistorySha256 != target.RuntimeBindingHistorySha256 || source.Count != target.RuntimeBindingHistoryCount ||
            source.SourceBinding != target.SourceRuntimeBinding || target.DatabasePathSha256 != CredentialRuntimeDatabasePathDigest(databasePath))
            throw new FoundationStoreException("RestrictedRecoveryAdoptionRecoveryStateChanged", 403);
    }

    void HoldRecoveredCredentialRestrictedRecoveryAdoptionIntents(SqliteConnection db, SqliteTransaction tx)
    {
        var now = At(Now); var ids = Rows(db, tx,
            "SELECT id FROM credential_restricted_recovery_adoption_intents WHERE customer_id=$c AND state IN ('Prepared','Approved') ORDER BY id LIMIT $limit",
            r => r.GetString(0), ("$c", CustomerId), ("$limit", MaximumCredentialRestrictedRecoveryAdoptionIntents + 1));
        if (ids.Count > MaximumCredentialRestrictedRecoveryAdoptionIntents) throw new FoundationStoreException("RestrictedRecoveryAdoptionRestoreCapacityExceeded");
        foreach (var id in ids)
        {
            var intent = ReadRestrictedRecoveryAdoptionIntent(db, tx, id);
            Exec(db, tx, "UPDATE credential_restricted_recovery_adoption_intents SET state='Quarantined' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", id));
            AuditRestrictedRecoveryAdoptionAt(db, tx, intent, "cell-maintenance", "RecoveredCredentialRestrictedRecoveryAdoptionHeld", "Quarantined", "IntentRequiresFreshRecoveryReview", now);
        }
    }
}
