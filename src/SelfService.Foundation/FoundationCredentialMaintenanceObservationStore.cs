using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialMaintenanceObservationRequests = 2048, MaximumPendingCredentialMaintenanceObservations = 64;
    internal Action? ObservationCommitFault { get; set; }

    internal FoundationCredentialMaintenanceIntent GetCredentialMaintenanceForObservationPreparation(CustomerContext requester, string originalIntentId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, requester, FoundationCapabilities.CredentialMaintenancePrepare, ["Operator"]);
        ObservationLifecycle(db, tx); var original = ReadMaintenance(db, tx, originalIntentId);
        MaintenanceScope(requester, original); RequireUnknownOriginal(original); tx.Commit(); return original;
    }

    public FoundationCredentialMaintenanceObservationRequest PrepareCredentialMaintenanceObservation(CustomerContext requester,
        string originalIntentId, FoundationCredentialMaintenanceTarget observation, string purpose, DateTimeOffset expiresUtc)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContext(db, tx, requester, FoundationCapabilities.CredentialMaintenancePrepare, ["Operator"]); ObservationLifecycle(db, tx);
        var original = ReadMaintenance(db, tx, originalIntentId); MaintenanceScope(requester, original); RequirePublishedObservation(original, observation);
        ValidatePurpose(purpose);
        if (expiresUtc <= clock.GetUtcNow() || expiresUtc > clock.GetUtcNow().AddMinutes(15)) throw new FoundationStoreException("InvalidMaintenanceExpiry", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_maintenance_observation_requests WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialMaintenanceObservationRequests)
            throw new FoundationStoreException("MaintenanceObservationHistoryCapacityExceeded", 429);
        if (Number(db, tx, """
            SELECT count(*) FROM credential_maintenance_observation_requests q WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND q.expires>$now
            AND NOT EXISTS(SELECT 1 FROM credential_maintenance_observation_receipts r WHERE r.customer_id=q.customer_id AND r.request_id=q.id)
            """, ("$c", CustomerId), ("$now", Now)) >= MaximumPendingCredentialMaintenanceObservations)
            throw new FoundationStoreException("MaintenanceObservationPendingCapacityExceeded", 429);
        var json = MaintenanceTargetJson(observation);
        var request = new FoundationCredentialMaintenanceObservationRequest(NewId(), CustomerId, CellId, original.ProjectId, original.EnvironmentId,
            original.Id, original.Digest, original.Execution!.AdmissionId, original.Execution.ExpectedAfterSha256, observation, Hash(json),
            requester.ActorKey, requester.RoleVersion, Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"),
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), Hash(purpose), At(expiresUtc.ToUnixTimeMilliseconds()),
            "", "Prepared", null, null, null);
        request = request with { Digest = ObservationRequestDigest(request) };
        Exec(db, tx, """
            INSERT INTO credential_maintenance_observation_requests VALUES($c,$id,$p,$e,$original,$originaldigest,$admission,$after,$observation,$sha,$actor,$version,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now)
            """, ("$c", CustomerId), ("$id", request.Id), ("$p", request.ProjectId), ("$e", request.EnvironmentId), ("$original", original.Id),
            ("$originaldigest", original.Digest), ("$admission", request.OriginalAdmissionId), ("$after", request.ExpectedAfterSha256),
            ("$observation", json), ("$sha", request.ObservationSha256), ("$actor", requester.ActorKey), ("$version", requester.RoleVersion),
            ("$policy", request.PolicyVersion), ("$instance", request.DatabaseInstanceId), ("$purpose", request.PurposeSha256),
            ("$expires", request.ExpiresAt.ToUnixTimeMilliseconds()), ("$digest", request.Digest), ("$now", Now));
        Audit(db, tx, requester.ActorKey, request.ProjectId, request.EnvironmentId, "CredentialMaintenanceObservationPrepared", request.Id, "Prepared", "CurrentEvidenceReviewOnly", request.Digest);
        tx.Commit(); return request;
    }

    public FoundationCredentialMaintenanceObservationRequest ApproveCredentialMaintenanceObservation(CustomerContext approver, string requestId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContext(db, tx, approver, FoundationCapabilities.CredentialMaintenanceApprove, ["Approver"]);
        var request = ReadMaintenanceObservation(db, tx, requestId); ObservationScope(approver, request); MatchDigest(request.Digest, expectedDigest);
        if (request.State != "Prepared" || request.ApproverKey is not null || request.Receipt is not null) throw new FoundationStoreException("MaintenanceObservationApprovalAlreadyRecorded");
        if (request.RequesterKey == approver.ActorKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        ValidateObservationAuthority(db, tx, request);
        Exec(db, tx, "INSERT INTO credential_maintenance_observation_approvals VALUES($c,$id,$actor,$version,$digest,$now)",
            ("$c", CustomerId), ("$id", request.Id), ("$actor", approver.ActorKey), ("$version", approver.RoleVersion), ("$digest", request.Digest), ("$now", Now));
        Exec(db, tx, "UPDATE credential_maintenance_observation_requests SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", request.Id));
        Audit(db, tx, approver.ActorKey, request.ProjectId, request.EnvironmentId, "CredentialMaintenanceObservationApproved", request.Id, "Approved", "DistinctCurrentEvidenceApproval", request.Digest);
        var result = ReadMaintenanceObservation(db, tx, request.Id); tx.Commit(); return result;
    }

    public FoundationCredentialMaintenanceObservationRequest GetCredentialMaintenanceObservation(CustomerContext reader, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, reader, FoundationCapabilities.CredentialMaintenanceRead, ["Operator", "Approver", "Auditor"]);
        var request = ReadMaintenanceObservation(db, tx, requestId); ObservationScope(reader, request); tx.Commit(); return request;
    }

    internal FoundationCredentialMaintenanceObservationRequest GetCredentialMaintenanceObservationForRecording(CustomerContext recorder, string requestId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, recorder, FoundationCapabilities.CredentialMaintenanceExecute, ["Operator"]);
        var request = ReadMaintenanceObservation(db, tx, requestId); ObservationScope(recorder, request); RequireObservationApproved(db, tx, request, recorder);
        tx.Commit(); return request;
    }

    internal FoundationCredentialMaintenanceObservationRequest RecordCredentialMaintenanceObservation(CustomerContext recorder, string requestId,
        string expectedDigest, FoundationCredentialMaintenanceTarget actualObservation)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var commitAttempted = false;
        try
        {
            AssertActivation(db, tx); MaintenanceContext(db, tx, recorder, FoundationCapabilities.CredentialMaintenanceExecute, ["Operator"]);
            var request = ReadMaintenanceObservation(db, tx, requestId); ObservationScope(recorder, request); MatchDigest(request.Digest, expectedDigest);
            RequireObservationApproved(db, tx, request, recorder);
            var original = ReadMaintenance(db, tx, request.OriginalIntentId); RequirePublishedObservation(original, actualObservation);
            MatchDigest(request.ObservationSha256, Hash(MaintenanceTargetJson(actualObservation)));
            var receipt = new FoundationCredentialMaintenanceObservationReceipt(NewId(), recorder.ActorKey, recorder.RoleVersion, "ObservedPublished",
                request.Digest, request.ObservationSha256, At(Now), "");
            receipt = receipt with { Digest = ObservationReceiptDigest(receipt) };
            Exec(db, tx, """
                INSERT INTO credential_maintenance_observation_receipts VALUES($c,$request,$id,$actor,$version,'ObservedPublished',$digest,$sha,$at,$receipt)
                """, ("$c", CustomerId), ("$request", request.Id), ("$id", receipt.Id), ("$actor", receipt.RecorderKey), ("$version", receipt.RecorderVersion),
                ("$digest", receipt.RequestDigest), ("$sha", receipt.ObservationSha256), ("$at", receipt.ObservedAt.ToUnixTimeMilliseconds()), ("$receipt", receipt.Digest));
            Audit(db, tx, recorder.ActorKey, request.ProjectId, request.EnvironmentId, "CredentialMaintenanceObservedPublished", request.Id,
                "ObservedPublished", "CurrentEvidenceOriginalOutcomeUnknown", receipt.Digest);
            var result = ReadMaintenanceObservation(db, tx, request.Id);
            commitAttempted = true; tx.Commit(); ObservationCommitFault?.Invoke(); return result;
        }
        catch (Exception) when (commitAttempted)
        { throw new FoundationStoreException("MaintenanceObservationRecordUncertain", 503); }
    }

    internal void RecordCredentialMaintenanceObservationDenied(CustomerContext recorder, string requestId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContextIdentity(recorder, FoundationCapabilities.CredentialMaintenanceExecute);
        var request = ReadMaintenanceObservation(db, tx, requestId); ObservationScope(recorder, request); MatchDigest(request.Digest, expectedDigest);
        Audit(db, tx, recorder.ActorKey, request.ProjectId, request.EnvironmentId, "CredentialMaintenanceObservationDenied", request.Id,
            "Denied", "CurrentEvidenceNotRecorded", request.Digest); tx.Commit();
    }

    void RequireObservationApproved(SqliteConnection db, SqliteTransaction tx, FoundationCredentialMaintenanceObservationRequest request, CustomerContext recorder)
    {
        if (request.State != "Approved" || request.ApproverKey is null) throw new FoundationStoreException("MaintenanceObservationApprovalRequired", 403);
        if (request.Receipt is not null) throw new FoundationStoreException("MaintenanceObservationAlreadyRecorded");
        if (request.ApproverKey == recorder.ActorKey) throw new FoundationStoreException("ApprovalSeparationRequired", 403);
        ValidateObservationAuthority(db, tx, request);
    }

    void ValidateObservationAuthority(SqliteConnection db, SqliteTransaction tx, FoundationCredentialMaintenanceObservationRequest request)
    {
        if (request.State == "Quarantined") throw new FoundationStoreException("MaintenanceObservationQuarantined");
        if (request.ExpiresAt <= clock.GetUtcNow()) throw new FoundationStoreException("MaintenanceObservationExpired");
        if (request.DatabaseInstanceId != ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1")) throw new FoundationStoreException("MaintenanceObservationInstanceChanged");
        if (request.PolicyVersion != Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1")) throw new FoundationStoreException("PolicyChanged");
        ObservationLifecycle(db, tx);
        ValidateMaintenanceStandingGrant(db, tx, request.RequesterKey, request.ProjectId, request.EnvironmentId);
        if (Require(db, tx, request.RequesterKey, request.ProjectId, request.EnvironmentId, ["Operator"], requireActive: false) != request.RequesterVersion)
            throw new FoundationStoreException("RequesterGrantChanged", 403);
        if (request.ApproverKey is not null)
        {
            ValidateMaintenanceStandingGrant(db, tx, request.ApproverKey, request.ProjectId, request.EnvironmentId);
            if (Require(db, tx, request.ApproverKey, request.ProjectId, request.EnvironmentId, ["Approver"], requireActive: false) != request.ApproverVersion)
                throw new FoundationStoreException("ApproverGrantChanged", 403);
        }
    }

    void ObservationLifecycle(SqliteConnection db, SqliteTransaction tx)
    {
        var lifecycle = ReadLifecycle(db, tx);
        if (lifecycle.State is not ("Active" or "Suspended" or "Offboarding") || (lifecycle.State == "Active") != lifecycle.Enabled)
            throw new FoundationStoreException("MaintenanceObservationLifecycleDenied", 403);
    }
    static void ObservationScope(CustomerContext context, FoundationCredentialMaintenanceObservationRequest request) =>
        MaintenanceScope(context, request.CustomerId, request.CellId, request.ProjectId, request.EnvironmentId);
    static void RequireUnknownOriginal(FoundationCredentialMaintenanceIntent original)
    {
        if (original.Execution is not { State: "OutcomeUnknown" }) throw new FoundationStoreException("MaintenanceOriginalUnknownRequired", 403);
    }
    static void RequirePublishedObservation(FoundationCredentialMaintenanceIntent original, FoundationCredentialMaintenanceTarget observation)
    {
        RequireUnknownOriginal(original); _ = ValidateMaintenanceTarget(observation, original.CustomerId, original.CellId, readOnlyObservation: true);
        var before = original.Target;
        if (observation.ProfileScopeJson != before.ProfileScopeJson || observation.ProfileScopeSha256 != before.ProfileScopeSha256 ||
            observation.ProfileIdSha256 != before.ProfileIdSha256 || observation.VaultNamespaceId != before.VaultNamespaceId ||
            observation.NamespaceCommitmentSha256 != before.NamespaceCommitmentSha256 || observation.ProfileGeneration != before.ProfileGeneration + 1 ||
            observation.SourceKeyId != before.DestinationKeyId || observation.ExpiresUnixMilliseconds != before.ExpiresUnixMilliseconds ||
            observation.Enabled != (original.Operation == CredentialMaintenanceOperation.Revoke ? false : before.Enabled) ||
            observation.EnvelopeSha256 != original.Execution!.ExpectedAfterSha256 || observation.AuthorityNamespaceId != before.AuthorityNamespaceId ||
            observation.AuthorityBindingSha256 != before.AuthorityBindingSha256 ||
            before.AuthorityNamespaceId is not null && observation.AuthorityRevision < before.AuthorityRevision + 2)
            throw new FoundationStoreException("MaintenancePublishedObservationMismatch");
    }

    static string ObservationRequestDigest(FoundationCredentialMaintenanceObservationRequest q) => Hash(CanonicalJson(JsonSerializer.Serialize(new
    { schema = "platform-studio.credential-maintenance-observation.v1", q.Id, q.CustomerId, q.CellId, q.ProjectId, q.EnvironmentId,
        q.OriginalIntentId, q.OriginalIntentDigest, q.OriginalAdmissionId, q.ExpectedAfterSha256, q.ObservationSha256,
        q.RequesterKey, q.RequesterVersion, q.PolicyVersion, q.DatabaseInstanceId, q.PurposeSha256, expires = q.ExpiresAt.ToUnixTimeMilliseconds() }, Json)));
    static string ObservationReceiptDigest(FoundationCredentialMaintenanceObservationReceipt r) => Hash(CanonicalJson(JsonSerializer.Serialize(new
    { schema = "platform-studio.credential-maintenance-observation-receipt.v1", r.Id, r.RecorderKey, r.RecorderVersion, r.Status,
        r.RequestDigest, r.ObservationSha256, observedAt = r.ObservedAt.ToUnixTimeMilliseconds() }, Json)));

    FoundationCredentialMaintenanceObservationRequest ReadMaintenanceObservation(SqliteConnection db, SqliteTransaction tx, string id)
    {
        ValidateId(id); string? rawObservation = null;
        var request = Rows(db, tx, """
            SELECT id,project_id,environment_id,original_intent_id,original_intent_digest,original_admission_id,expected_after_sha256,
              observation,observation_sha256,requester,requester_version,policy_version,database_instance_id,purpose_sha256,expires,digest,state
            FROM credential_maintenance_observation_requests WHERE customer_id=$c AND id=$id
            """, r => { rawObservation = r.GetString(7); return new FoundationCredentialMaintenanceObservationRequest(r.GetString(0), CustomerId, CellId,
                r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                DeserializeMaintenance<FoundationCredentialMaintenanceTarget>(rawObservation, 16384), r.GetString(8), r.GetString(9), r.GetInt64(10),
                r.GetInt64(11), r.GetString(12), r.GetString(13), At(r.GetInt64(14)), r.GetString(15), r.GetString(16), null, null, null); },
            ("$c", CustomerId), ("$id", id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
        var approvals = Rows(db, tx, "SELECT actor_key,actor_version,digest FROM credential_maintenance_observation_approvals WHERE customer_id=$c AND request_id=$id",
            r => (Actor: r.GetString(0), Version: r.GetInt64(1), Digest: r.GetString(2)), ("$c", CustomerId), ("$id", id));
        if (approvals.Count > 1 || approvals.Count == 1 && approvals[0].Digest != request.Digest) throw new FoundationStoreException("MaintenanceObservationIntegrityFailed");
        var receipt = Rows(db, tx, """
            SELECT id,recorder_key,recorder_version,status,request_digest,observation_sha256,observed_at,digest
            FROM credential_maintenance_observation_receipts WHERE customer_id=$c AND request_id=$id
            """, r => new FoundationCredentialMaintenanceObservationReceipt(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                r.GetString(4), r.GetString(5), At(r.GetInt64(6)), r.GetString(7)), ("$c", CustomerId), ("$id", id)).SingleOrDefault();
        request = request with { ApproverKey = approvals.Count == 0 ? null : approvals[0].Actor, ApproverVersion = approvals.Count == 0 ? null : approvals[0].Version, Receipt = receipt };
        var original = ReadMaintenance(db, tx, request.OriginalIntentId); ValidateObservationReadIntegrity(request, rawObservation!, original); return request;
    }

    static void ValidateObservationReadIntegrity(FoundationCredentialMaintenanceObservationRequest q, string rawObservation, FoundationCredentialMaintenanceIntent original)
    {
        RequirePublishedObservation(original, q.Observation); RequireHuman(q.RequesterKey);
        if (q.ProjectId != original.ProjectId || q.EnvironmentId != original.EnvironmentId || q.OriginalIntentDigest != original.Digest ||
            q.OriginalAdmissionId != original.Execution!.AdmissionId || q.ExpectedAfterSha256 != original.Execution.ExpectedAfterSha256 ||
            CanonicalJson(rawObservation) != MaintenanceTargetJson(q.Observation) || Hash(MaintenanceTargetJson(q.Observation)) != q.ObservationSha256 ||
            q.RequesterVersion <= 0 || q.PolicyVersion <= 0 || !ValidDatabaseInstanceId(q.DatabaseInstanceId) || !MaintenanceHash(q.PurposeSha256) ||
            q.Digest != ObservationRequestDigest(q) || q.State is not ("Prepared" or "Approved" or "Quarantined"))
            throw new FoundationStoreException("MaintenanceObservationIntegrityFailed");
        if (q.ApproverKey is null ? q.ApproverVersion is not null || q.State == "Approved" || q.Receipt is not null :
            q.ApproverVersion is null or <= 0 || q.ApproverKey == q.RequesterKey || q.State == "Prepared")
            throw new FoundationStoreException("MaintenanceObservationIntegrityFailed");
        if (q.ApproverKey is not null) RequireHuman(q.ApproverKey);
        if (q.Receipt is not { } receipt) return;
        RequireHuman(receipt.RecorderKey);
        if (q.State != "Approved" || !ValidDatabaseInstanceId(receipt.Id) || receipt.RecorderVersion <= 0 || receipt.RecorderKey == q.ApproverKey ||
            receipt.Status != "ObservedPublished" || receipt.RequestDigest != q.Digest || receipt.ObservationSha256 != q.ObservationSha256 ||
            receipt.ObservedAt >= q.ExpiresAt || receipt.Digest != ObservationReceiptDigest(receipt)) throw new FoundationStoreException("MaintenanceObservationIntegrityFailed");
    }
}
