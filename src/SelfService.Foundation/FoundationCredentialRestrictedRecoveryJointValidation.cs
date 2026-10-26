using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const int MaximumRestrictedRecoveryJointTargetBytes = 262144;
    sealed record RestrictedRecoveryJointTargetDocument(string Schema, string AccessProfile, CredentialRestrictedRecoverySetCheckpointAuthorityIdentity CheckpointAuthority,
        CredentialRestrictedRecoverySetCheckpoint Checkpoint, JsonElement StageTarget);

    internal static FoundationCredentialRestrictedRecoveryJointTarget CredentialRestrictedRecoveryJointTarget(CredentialRestrictedRecoveryStagePlan plan,
        CredentialRestrictedRecoveryStageReceipt receipt, CredentialRestrictedRecoverySetCheckpointAuthorityIdentity identity, CredentialRestrictedRecoverySetCheckpoint checkpoint)
    {
        try
        {
            var stage = CredentialRestrictedRecoveryStageReviewTarget(plan, receipt);
            if (checkpoint is null || checkpoint.SetAuthority != plan.SourceSetAuthority) throw new FoundationStoreException("RestrictedRecoveryJointSelectionMismatch", 403);
            var target = new FoundationCredentialRestrictedRecoveryJointTarget(identity, checkpoint, stage); ValidateRestrictedRecoveryJointTarget(target); return target;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is CredentialException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new FoundationStoreException("RestrictedRecoveryJointTargetInvalid", 400); }
    }

    static void ValidateRestrictedRecoveryJointTarget(FoundationCredentialRestrictedRecoveryJointTarget target)
    {
        if (!OperatingSystem.IsWindows()) throw new FoundationStoreException("RestrictedRecoveryJointWindowsRequired", 403);
        if (target is null || target.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || target.CheckpointAuthority is null || target.Checkpoint is null || target.StageTarget is null)
            throw new FoundationStoreException("RestrictedRecoveryJointTargetInvalid", 400);
        ValidateRestrictedRecoveryTarget(target.StageTarget);
        var identity = target.CheckpointAuthority; var head = target.Checkpoint; var stage = target.StageTarget; var source = stage.SourceNamespace; var pin = head.SetAuthority;
        if (identity.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || head.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
            pin is null || pin.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
            identity.CustomerId != source.CustomerId || identity.CellId != source.CellId || identity.SourceNamespace != source ||
            !ValidDatabaseInstanceId(identity.AuthorityId) || !MaintenanceHash(identity.CommitmentSha256) || !MaintenanceHash(identity.PublicKeySha256) ||
            !CredentialRules.ProfileId(identity.SignerKeyId) || head.SourceNamespace != source || head.AuthorityId != identity.AuthorityId ||
            head.AuthorityCommitmentSha256 != identity.CommitmentSha256 || head.Sequence is < 1 or > CredentialRestrictedRecoverySetCheckpointAuthority.MaximumCheckpoints ||
            (head.Sequence == 1 ? head.PreviousSha256 != "" : !MaintenanceHash(head.PreviousSha256)) ||
            head.Sha256 != CredentialRestrictedRecoverySetCheckpointAuthority.CheckpointDigest(head) || pin is null ||
            pin.CustomerId != source.CustomerId || pin.CellId != source.CellId || !ValidDatabaseInstanceId(pin.SetId) ||
            pin.Generation is < 1 or > CredentialRecoveryInventory.MaximumCounter || pin.ArtifactSha256 != stage.SourceSetSha256 ||
            pin.SignerKeyId != identity.SignerKeyId || pin.PublicKeySha256 != identity.PublicKeySha256 || head.InventorySha256 != stage.SourceInventorySha256 ||
            !MaintenanceHash(head.RehearsalSha256) || !CredentialRecoverySets.Utc(head.AcceptedUtc) || !CredentialRecoverySets.Utc(head.CapturedUtc) ||
            !CredentialRecoverySets.Utc(head.IssuedUtc) || !CredentialRecoverySets.Utc(head.NotBeforeUtc) || !CredentialRecoverySets.Utc(head.ExpiresUtc) ||
            head.CapturedUtc > head.IssuedUtc || head.IssuedUtc >= head.ExpiresUtc || head.NotBeforeUtc >= head.ExpiresUtc ||
            head.ExpiresUtc - head.IssuedUtc > TimeSpan.FromDays(366) || head.AcceptedUtc < head.IssuedUtc || head.AcceptedUtc < head.NotBeforeUtc || head.AcceptedUtc >= head.ExpiresUtc)
            throw new FoundationStoreException("RestrictedRecoveryJointTargetInvalid", 400);
        PublicRestrictedRecoveryString(identity.SignerKeyId);
    }

    static string RestrictedRecoveryJointTargetJson(FoundationCredentialRestrictedRecoveryJointTarget target)
    {
        ValidateRestrictedRecoveryJointTarget(target); var json = JsonSerializer.Serialize(target, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumRestrictedRecoveryJointTargetBytes) throw new FoundationStoreException("RestrictedRecoveryJointTargetLimit", 400); return json;
    }
    static FoundationCredentialRestrictedRecoveryJointTarget ParseRestrictedRecoveryJointTarget(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumRestrictedRecoveryJointTargetBytes) throw new JsonException();
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(json));
            var root = document.RootElement;
            RequireRestrictedJointProfile(root);
            RequireRestrictedJointProfile(root.GetProperty("checkpointAuthority"));
            RequireRestrictedJointProfile(root.GetProperty("checkpoint"));
            RequireRestrictedJointProfile(root.GetProperty("checkpoint").GetProperty("setAuthority"));
            RequireRestrictedJointProfile(root.GetProperty("stageTarget"));
            var raw = document.RootElement.Deserialize<RestrictedRecoveryJointTargetDocument>(CredentialRecoverySets.Json) ?? throw new JsonException();
            var target = new FoundationCredentialRestrictedRecoveryJointTarget(raw.CheckpointAuthority, raw.Checkpoint, ParseRestrictedRecoveryTarget(raw.StageTarget.GetRawText()));
            if (raw.Schema != target.Schema || raw.AccessProfile != target.AccessProfile || RestrictedRecoveryJointTargetJson(target) != json) throw new JsonException(); return target;
        }
        catch (Exception error) when (error is JsonException or CredentialException or FoundationStoreException or ArgumentException or NullReferenceException or InvalidOperationException or KeyNotFoundException)
        { throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed"); }
    }
    static string RestrictedRecoveryJointResultJson(FoundationCredentialRestrictedRecoveryJointResult result)
    {
        var json = JsonSerializer.Serialize(result, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > 16384) throw new FoundationStoreException("RestrictedRecoveryJointResultLimit", 400); return json;
    }
    static FoundationCredentialRestrictedRecoveryJointResult ParseRestrictedRecoveryJointResult(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > 16384) throw new JsonException();
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(json));
            RequireRestrictedJointProfile(document.RootElement);
            var result = document.RootElement.Deserialize<FoundationCredentialRestrictedRecoveryJointResult>(CredentialRecoverySets.Json) ?? throw new JsonException();
            if (RestrictedRecoveryJointResultJson(result) != json) throw new JsonException(); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed"); }
    }
    static void RequireRestrictedJointProfile(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("accessProfile", out var profile) ||
            profile.ValueKind != JsonValueKind.String || profile.GetString() != CredentialRestrictedRecoveryStaging.Profile) throw new JsonException();
    }
    static string RestrictedRecoveryJointDigest(FoundationCredentialRestrictedRecoveryStageReview review, string admissionId, string executor,
        long version, string targetSha, DateTimeOffset admitted) => Hash(JsonSerializer.Serialize(new
    {
        schema = "platform-studio.credential-restricted-recovery-joint-admission.v1", accessProfile = CredentialRestrictedRecoveryStaging.Profile, review.Id, review.Digest, review.CustomerId, review.CellId,
        admissionId, executor, version, review.DatabaseInstanceId, targetSha, admitted = admitted.ToUnixTimeMilliseconds()
    }, CredentialRecoverySets.Json));

    static void RequireRestrictedRecoveryJointVerification(FoundationCredentialRestrictedRecoveryStageReview review,
        FoundationCredentialRestrictedRecoveryJointVerification current, CredentialRestrictedRecoveryStageVerification verification, DateTimeOffset completed)
    {
        var stage = current.Target.StageTarget;
        if (verification is null || verification.PlanSha256 != stage.PlanSha256 || verification.ReceiptSha256 != stage.ReceiptSha256 ||
            verification.SourceSetSha256 != stage.SourceSetSha256 || !MaintenanceHash(verification.Sha256) ||
            verification.ObservedUtc < current.AdmittedAt || verification.ObservedUtc > completed || !CredentialRecoverySets.Utc(verification.ObservedUtc) ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || !verification.OutputCiphertextsAuthenticated || !verification.DestinationAbsenceProofsAuthenticated ||
            verification.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || verification.Receipt.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
            !verification.DestinationCompletionProofAuthenticated || verification.CompletionProofSha256 != stage.CompletionProofSha256 ||
            verification.Receipt.CompletionProofSha256 != stage.CompletionProofSha256 ||
            verification.CompletionCoreSha256 != stage.CompletionCoreSha256 || verification.AuthenticatedProfileCount != stage.ProfileCount ||
            verification.AuthenticatedAbsenceCount != stage.AbsenceProofCount || verification.Receipt.StageId != stage.StageId || verification.Receipt.DestinationKey != stage.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != stage.DestinationRootCommitmentSha256 || verification.Receipt.StagedVaultNamespaceId != stage.StagedVaultNamespaceId ||
            verification.Receipt.StagedAuthorityNamespaceId != stage.StagedAuthorityNamespaceId || verification.Receipt.BindingSha256 != stage.BindingSha256 ||
            verification.Receipt.StateSha256 != stage.StateSha256 || current.ReviewDigest != review.Digest)
            throw new FoundationStoreException("RestrictedRecoveryJointVerificationMismatch");
    }

    FoundationCredentialRestrictedRecoveryJointVerification RequireRestrictedRecoveryJointAdmission(SqliteConnection db, SqliteTransaction tx,
        CredentialRestrictedRecoveryJointAdmission admission, FoundationCredentialRestrictedRecoveryStageReview review)
    {
        var evidence = ReadRestrictedRecoveryJoint(db, tx, review);
        if (evidence.AdmissionId != admission.AdmissionId || evidence.Digest != admission.Digest || evidence.TargetSha256 != admission.TargetSha256 ||
            RestrictedRecoveryJointTargetJson(evidence.Target) != RestrictedRecoveryJointTargetJson(admission.Target) || evidence.DatabaseInstanceId != admission.DatabaseInstanceId)
            throw new FoundationStoreException("RestrictedRecoveryJointAdmissionMismatch", 403);
        return evidence;
    }

    FoundationCredentialRestrictedRecoveryJointVerification ReadRestrictedRecoveryJoint(SqliteConnection db, SqliteTransaction tx, FoundationCredentialRestrictedRecoveryStageReview review)
    {
        try
        {
            long updated = 0; string? targetJson = null, resultJson = null;
            var execution = review.Execution ?? throw new FoundationStoreException("NotFound", 404);
            var result = Rows(db, tx, """
                SELECT admission_id,target,target_sha256,digest,state,reason,result_json,result_sha256,admitted,completed,updated
                FROM credential_restricted_recovery_joint_executions WHERE customer_id=$c AND review_id=$id
                """, r =>
                {
                    targetJson = r.GetString(1); resultJson = r.IsDBNull(6) ? null : r.GetString(6); updated = r.GetInt64(10);
                    return new FoundationCredentialRestrictedRecoveryJointVerification(review.Id, review.Digest, CustomerId, CellId,
                        ParseRestrictedRecoveryJointTarget(targetJson), r.GetString(2), r.GetString(3), r.GetString(0), execution.ExecutorKey, execution.ExecutorVersion,
                        review.DatabaseInstanceId, r.GetString(4), r.GetString(5), resultJson is null ? null : ParseRestrictedRecoveryJointResult(resultJson), r.IsDBNull(7) ? null : r.GetString(7),
                        At(r.GetInt64(8)), r.IsDBNull(9) ? null : At(r.GetInt64(9)), review.State);
                }, ("$c", CustomerId), ("$id", review.Id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
            if (result.AdmissionId != execution.AdmissionId || result.AdmittedAt != execution.AdmittedAt || result.State != execution.State || result.CompletedAt != execution.CompletedAt ||
                !MaintenanceHash(result.TargetSha256) || Hash(targetJson!) != result.TargetSha256 ||
                Hash(RestrictedRecoveryTargetJson(result.Target.StageTarget)) != review.TargetSha256 ||
                result.AdmittedAt < result.Target.Checkpoint.AcceptedUtc || updated < result.AdmittedAt.ToUnixTimeMilliseconds() ||
                result.Digest != RestrictedRecoveryJointDigest(review, result.AdmissionId, execution.ExecutorKey, execution.ExecutorVersion, result.TargetSha256, result.AdmittedAt))
                throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed");
            if (result.State == "OutcomeUnknown")
            {
                if (result.Reason != execution.Reason || result.Result is not null || result.ResultSha256 is not null || result.CompletedAt is not null ||
                    result.Reason != "AdmittedBeforeVerification" && RestrictedRecoveryReviewReason(result.Reason) != result.Reason)
                    throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed");
            }
            else if (result.State == "Succeeded")
            {
                var recorded = result.Result; var ordinary = execution.Result; var head = result.Target.Checkpoint;
                if (result.Quarantined || recorded is null || ordinary is null || resultJson is null || result.Reason != "JointHeldVerificationRecorded" ||
                    !MaintenanceHash(result.ResultSha256) || Hash(resultJson) != result.ResultSha256 || result.CompletedAt is null || updated != result.CompletedAt.Value.ToUnixTimeMilliseconds() ||
                    recorded.ReviewDigest != review.Digest || recorded.TargetSha256 != result.TargetSha256 || recorded.StageVerificationSha256 != ordinary.StageVerificationSha256 ||
                    recorded.CheckpointAuthorityCommitmentSha256 != result.Target.CheckpointAuthority.CommitmentSha256 || recorded.CheckpointSequence != head.Sequence || recorded.CheckpointSha256 != head.Sha256 ||
                    recorded.PlanSha256 != ordinary.PlanSha256 || recorded.ReceiptSha256 != ordinary.ReceiptSha256 || recorded.SourceSetSha256 != ordinary.SourceSetSha256 ||
                    recorded.CompletionProofSha256 != ordinary.CompletionProofSha256 || recorded.CompletionProofSha256 != result.Target.StageTarget.CompletionProofSha256 || recorded.ObservedAt != ordinary.ObservedAt)
                    throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed");
            }
            else throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed");
            return result;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is JsonException or CredentialException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        { throw new FoundationStoreException("RestrictedRecoveryJointIntegrityFailed"); }
    }
}
