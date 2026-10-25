using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const int MaximumScopedRecoveryJointTargetBytes = 262144;
    sealed record ScopedRecoveryJointTargetDocument(string Schema, CredentialScopedRecoverySetCheckpointAuthorityIdentity CheckpointAuthority,
        CredentialScopedRecoverySetCheckpoint Checkpoint, JsonElement StageTarget);

    internal static FoundationCredentialScopedRecoveryJointTarget CredentialScopedRecoveryJointTarget(CredentialScopedRecoveryStagePlan plan,
        CredentialScopedRecoveryStageReceipt receipt, CredentialScopedRecoverySetCheckpointAuthorityIdentity identity, CredentialScopedRecoverySetCheckpoint checkpoint)
    {
        try
        {
            var stage = CredentialScopedRecoveryStageReviewTarget(plan, receipt);
            if (checkpoint is null || checkpoint.SetAuthority != plan.SourceSetAuthority) throw new FoundationStoreException("ScopedRecoveryJointSelectionMismatch", 403);
            var target = new FoundationCredentialScopedRecoveryJointTarget(identity, checkpoint, stage); ValidateScopedRecoveryJointTarget(target); return target;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is CredentialException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new FoundationStoreException("ScopedRecoveryJointTargetInvalid", 400); }
    }

    static void ValidateScopedRecoveryJointTarget(FoundationCredentialScopedRecoveryJointTarget target)
    {
        if (target is null || target.CheckpointAuthority is null || target.Checkpoint is null || target.StageTarget is null)
            throw new FoundationStoreException("ScopedRecoveryJointTargetInvalid", 400);
        ValidateScopedRecoveryTarget(target.StageTarget);
        var identity = target.CheckpointAuthority; var head = target.Checkpoint; var stage = target.StageTarget; var source = stage.SourceNamespace; var pin = head.SetAuthority;
        if (identity.CustomerId != source.CustomerId || identity.CellId != source.CellId || identity.SourceNamespace != source ||
            !ValidDatabaseInstanceId(identity.AuthorityId) || !MaintenanceHash(identity.CommitmentSha256) || !MaintenanceHash(identity.PublicKeySha256) ||
            !CredentialRules.ProfileId(identity.SignerKeyId) || head.SourceNamespace != source || head.AuthorityId != identity.AuthorityId ||
            head.AuthorityCommitmentSha256 != identity.CommitmentSha256 || head.Sequence is < 1 or > CredentialScopedRecoverySetCheckpointAuthority.MaximumCheckpoints ||
            (head.Sequence == 1 ? head.PreviousSha256 != "" : !MaintenanceHash(head.PreviousSha256)) ||
            head.Sha256 != CredentialScopedRecoverySetCheckpointAuthority.CheckpointDigest(head) || pin is null ||
            pin.CustomerId != source.CustomerId || pin.CellId != source.CellId || !ValidDatabaseInstanceId(pin.SetId) ||
            pin.Generation is < 1 or > CredentialRecoveryInventory.MaximumCounter || pin.ArtifactSha256 != stage.SourceSetSha256 ||
            pin.SignerKeyId != identity.SignerKeyId || pin.PublicKeySha256 != identity.PublicKeySha256 || head.InventorySha256 != stage.SourceInventorySha256 ||
            !MaintenanceHash(head.RehearsalSha256) || !CredentialRecoverySets.Utc(head.AcceptedUtc) || !CredentialRecoverySets.Utc(head.CapturedUtc) ||
            !CredentialRecoverySets.Utc(head.IssuedUtc) || !CredentialRecoverySets.Utc(head.NotBeforeUtc) || !CredentialRecoverySets.Utc(head.ExpiresUtc) ||
            head.CapturedUtc > head.IssuedUtc || head.IssuedUtc >= head.ExpiresUtc || head.NotBeforeUtc >= head.ExpiresUtc ||
            head.ExpiresUtc - head.IssuedUtc > TimeSpan.FromDays(366) || head.AcceptedUtc < head.IssuedUtc || head.AcceptedUtc < head.NotBeforeUtc || head.AcceptedUtc >= head.ExpiresUtc)
            throw new FoundationStoreException("ScopedRecoveryJointTargetInvalid", 400);
        PublicScopedRecoveryString(identity.SignerKeyId);
    }

    static string ScopedRecoveryJointTargetJson(FoundationCredentialScopedRecoveryJointTarget target)
    {
        ValidateScopedRecoveryJointTarget(target); var json = JsonSerializer.Serialize(target, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumScopedRecoveryJointTargetBytes) throw new FoundationStoreException("ScopedRecoveryJointTargetLimit", 400); return json;
    }
    static FoundationCredentialScopedRecoveryJointTarget ParseScopedRecoveryJointTarget(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumScopedRecoveryJointTargetBytes) throw new JsonException();
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(json));
            var raw = document.RootElement.Deserialize<ScopedRecoveryJointTargetDocument>(CredentialRecoverySets.Json) ?? throw new JsonException();
            var target = new FoundationCredentialScopedRecoveryJointTarget(raw.CheckpointAuthority, raw.Checkpoint, ParseScopedRecoveryTarget(raw.StageTarget.GetRawText()));
            if (raw.Schema != target.Schema || ScopedRecoveryJointTargetJson(target) != json) throw new JsonException(); return target;
        }
        catch (Exception error) when (error is JsonException or CredentialException or FoundationStoreException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed"); }
    }
    static string ScopedRecoveryJointResultJson(FoundationCredentialScopedRecoveryJointResult result)
    {
        var json = JsonSerializer.Serialize(result, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > 16384) throw new FoundationStoreException("ScopedRecoveryJointResultLimit", 400); return json;
    }
    static FoundationCredentialScopedRecoveryJointResult ParseScopedRecoveryJointResult(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > 16384) throw new JsonException();
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(json));
            var result = document.RootElement.Deserialize<FoundationCredentialScopedRecoveryJointResult>(CredentialRecoverySets.Json) ?? throw new JsonException();
            if (ScopedRecoveryJointResultJson(result) != json) throw new JsonException(); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed"); }
    }
    static string ScopedRecoveryJointDigest(FoundationCredentialScopedRecoveryStageReview review, string admissionId, string executor,
        long version, string targetSha, DateTimeOffset admitted) => Hash(JsonSerializer.Serialize(new
    {
        schema = "platform-studio.credential-scoped-recovery-joint-admission.v1", review.Id, review.Digest, review.CustomerId, review.CellId,
        admissionId, executor, version, review.DatabaseInstanceId, targetSha, admitted = admitted.ToUnixTimeMilliseconds()
    }, CredentialRecoverySets.Json));

    static void RequireScopedRecoveryJointVerification(FoundationCredentialScopedRecoveryStageReview review,
        FoundationCredentialScopedRecoveryJointVerification current, CredentialScopedRecoveryStageVerification verification, DateTimeOffset completed)
    {
        var stage = current.Target.StageTarget;
        if (verification is null || verification.PlanSha256 != stage.PlanSha256 || verification.ReceiptSha256 != stage.ReceiptSha256 ||
            verification.SourceSetSha256 != stage.SourceSetSha256 || !MaintenanceHash(verification.Sha256) ||
            verification.ObservedUtc < current.AdmittedAt || verification.ObservedUtc > completed || !CredentialRecoverySets.Utc(verification.ObservedUtc) ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || !verification.OutputCiphertextsAuthenticated || !verification.DestinationAbsenceProofsAuthenticated ||
            verification.CompletionCoreSha256 != stage.CompletionCoreSha256 || verification.AuthenticatedProfileCount != stage.ProfileCount ||
            verification.AuthenticatedAbsenceCount != stage.AbsenceProofCount || verification.Receipt.StageId != stage.StageId || verification.Receipt.DestinationKey != stage.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != stage.DestinationRootCommitmentSha256 || verification.Receipt.StagedVaultNamespaceId != stage.StagedVaultNamespaceId ||
            verification.Receipt.StagedAuthorityNamespaceId != stage.StagedAuthorityNamespaceId || verification.Receipt.BindingSha256 != stage.BindingSha256 ||
            verification.Receipt.StateSha256 != stage.StateSha256 || current.ReviewDigest != review.Digest)
            throw new FoundationStoreException("ScopedRecoveryJointVerificationMismatch");
    }

    FoundationCredentialScopedRecoveryJointVerification RequireScopedRecoveryJointAdmission(SqliteConnection db, SqliteTransaction tx,
        CredentialScopedRecoveryJointAdmission admission, FoundationCredentialScopedRecoveryStageReview review)
    {
        var evidence = ReadScopedRecoveryJoint(db, tx, review);
        if (evidence.AdmissionId != admission.AdmissionId || evidence.Digest != admission.Digest || evidence.TargetSha256 != admission.TargetSha256 ||
            ScopedRecoveryJointTargetJson(evidence.Target) != ScopedRecoveryJointTargetJson(admission.Target) || evidence.DatabaseInstanceId != admission.DatabaseInstanceId)
            throw new FoundationStoreException("ScopedRecoveryJointAdmissionMismatch", 403);
        return evidence;
    }

    FoundationCredentialScopedRecoveryJointVerification ReadScopedRecoveryJoint(SqliteConnection db, SqliteTransaction tx, FoundationCredentialScopedRecoveryStageReview review)
    {
        try
        {
            long updated = 0; string? targetJson = null, resultJson = null;
            var execution = review.Execution ?? throw new FoundationStoreException("NotFound", 404);
            var result = Rows(db, tx, """
                SELECT admission_id,target,target_sha256,digest,state,reason,result_json,result_sha256,admitted,completed,updated
                FROM credential_scoped_recovery_joint_executions WHERE customer_id=$c AND review_id=$id
                """, r =>
                {
                    targetJson = r.GetString(1); resultJson = r.IsDBNull(6) ? null : r.GetString(6); updated = r.GetInt64(10);
                    return new FoundationCredentialScopedRecoveryJointVerification(review.Id, review.Digest, CustomerId, CellId,
                        ParseScopedRecoveryJointTarget(targetJson), r.GetString(2), r.GetString(3), r.GetString(0), execution.ExecutorKey, execution.ExecutorVersion,
                        review.DatabaseInstanceId, r.GetString(4), r.GetString(5), resultJson is null ? null : ParseScopedRecoveryJointResult(resultJson), r.IsDBNull(7) ? null : r.GetString(7),
                        At(r.GetInt64(8)), r.IsDBNull(9) ? null : At(r.GetInt64(9)), review.State);
                }, ("$c", CustomerId), ("$id", review.Id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
            if (result.AdmissionId != execution.AdmissionId || result.AdmittedAt != execution.AdmittedAt || result.State != execution.State || result.CompletedAt != execution.CompletedAt ||
                !MaintenanceHash(result.TargetSha256) || Hash(targetJson!) != result.TargetSha256 ||
                Hash(ScopedRecoveryTargetJson(result.Target.StageTarget)) != review.TargetSha256 ||
                result.AdmittedAt < result.Target.Checkpoint.AcceptedUtc || updated < result.AdmittedAt.ToUnixTimeMilliseconds() ||
                result.Digest != ScopedRecoveryJointDigest(review, result.AdmissionId, execution.ExecutorKey, execution.ExecutorVersion, result.TargetSha256, result.AdmittedAt))
                throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed");
            if (result.State == "OutcomeUnknown")
            {
                if (result.Reason != execution.Reason || result.Result is not null || result.ResultSha256 is not null || result.CompletedAt is not null ||
                    result.Reason != "AdmittedBeforeVerification" && ScopedRecoveryReviewReason(result.Reason) != result.Reason)
                    throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed");
            }
            else if (result.State == "Succeeded")
            {
                var recorded = result.Result; var ordinary = execution.Result; var head = result.Target.Checkpoint;
                if (result.Quarantined || recorded is null || ordinary is null || resultJson is null || result.Reason != "JointHeldVerificationRecorded" ||
                    !MaintenanceHash(result.ResultSha256) || Hash(resultJson) != result.ResultSha256 || result.CompletedAt is null || updated != result.CompletedAt.Value.ToUnixTimeMilliseconds() ||
                    recorded.ReviewDigest != review.Digest || recorded.TargetSha256 != result.TargetSha256 || recorded.StageVerificationSha256 != ordinary.StageVerificationSha256 ||
                    recorded.CheckpointAuthorityCommitmentSha256 != result.Target.CheckpointAuthority.CommitmentSha256 || recorded.CheckpointSequence != head.Sequence || recorded.CheckpointSha256 != head.Sha256 ||
                    recorded.PlanSha256 != ordinary.PlanSha256 || recorded.ReceiptSha256 != ordinary.ReceiptSha256 || recorded.SourceSetSha256 != ordinary.SourceSetSha256 || recorded.ObservedAt != ordinary.ObservedAt)
                    throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed");
            }
            else throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed");
            return result;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is JsonException or CredentialException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        { throw new FoundationStoreException("ScopedRecoveryJointIntegrityFailed"); }
    }
}
