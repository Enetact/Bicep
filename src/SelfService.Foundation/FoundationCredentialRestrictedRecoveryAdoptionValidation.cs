using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialRestrictedRecoveryAdoptionIntents = 2048, MaximumPendingCredentialRestrictedRecoveryAdoptionIntents = 64;
    public const int MaximumCredentialRestrictedRecoveryAdoptionTargetBytes = 262144;
    const string RestrictedAdoptionOperation = "ProvisionFreshRestrictedNamespacesForRestoredInstance";
    sealed record RestrictedAdoptionTargetDocument(string Schema, string AccessProfile, string Operation, JsonElement RecoveryTarget,
        string DatabaseInstanceId, string DatabasePathSha256, FoundationRecoveryExecutionHold RecoveryHold, bool RuntimeBindingRequired,
        string RuntimeBindingHistorySha256, int RuntimeBindingHistoryCount, string? CurrentRuntimeBindingSha256,
        FoundationCredentialRuntimeBinding SourceRuntimeBinding, FoundationCredentialRestrictedRecoveryAdoptionSelection Selection);

    static void ValidateRestrictedRecoveryAdoptionTarget(FoundationCredentialRestrictedRecoveryAdoptionTarget target)
    {
        if (target is null) throw new FoundationStoreException("RestrictedRecoveryAdoptionTargetInvalid", 400);
        ValidateRestrictedRecoveryJointTarget(target.RecoveryTarget);
        var source = target.RecoveryTarget.StageTarget.SourceNamespace; var hold = target.RecoveryHold;
        var binding = target.SourceRuntimeBinding; var selection = target.Selection;
        if (!ValidDatabaseInstanceId(target.DatabaseInstanceId) || !MaintenanceHash(target.DatabasePathSha256) ||
            hold is null || hold.CustomerId != source.CustomerId || hold.CellId != source.CellId || hold.DatabaseInstanceId != target.DatabaseInstanceId ||
            !ValidDatabaseInstanceId(hold.Id) || hold.Origin != "RestoredSnapshot" || !ValidDatabaseInstanceId(hold.SourceDatabaseInstanceId) ||
            hold.SourceDatabaseInstanceId == hold.DatabaseInstanceId || !MaintenanceHash(hold.BackupSha256) || hold.PolicyVersion is < 1 or > 9007199254740991 ||
            !CredentialRecoverySets.Utc(hold.CreatedAt) || hold.CreatedAt.ToUnixTimeMilliseconds() <= 0 || At(hold.CreatedAt.ToUnixTimeMilliseconds()) != hold.CreatedAt ||
            hold.Reason != RecoveryHoldReason || hold.Sha256 != RecoveryExecutionHoldDigest(hold) ||
            !target.RuntimeBindingRequired || target.CurrentRuntimeBindingSha256 is not null ||
            !MaintenanceHash(target.RuntimeBindingHistorySha256) || target.RuntimeBindingHistoryCount is < 1 or > MaximumCredentialRuntimeBindings ||
            binding is null || binding.CustomerId != source.CustomerId || binding.CellId != source.CellId ||
            binding.DatabaseInstanceId != hold.SourceDatabaseInstanceId || binding.Namespace != source || !ValidDatabaseInstanceId(binding.Id) ||
            !MaintenanceHash(binding.DatabasePathSha256) || !CredentialRecoverySets.Utc(binding.CreatedAt) || binding.CreatedAt.ToUnixTimeMilliseconds() <= 0 ||
            At(binding.CreatedAt.ToUnixTimeMilliseconds()) != binding.CreatedAt || binding.CreatedAt > hold.CreatedAt ||
            binding.Sha256 != CredentialRuntimeBindingDigest(binding) || binding.ActivationAuthority is null ||
            binding.ActivationAuthority.CustomerId != source.CustomerId || binding.ActivationAuthority.CellId != source.CellId ||
            !ValidDatabaseInstanceId(binding.ActivationAuthority.AuthorityId) || !MaintenanceHash(binding.ActivationAuthority.CustodyCommitmentSha256) ||
            binding.ActivationGeneration is < 1 or > FoundationCellActivationAuthority.MaximumGenerations || !MaintenanceHash(binding.ActivationBindingSha256) ||
            selection is null || selection.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || selection.Operation != RestrictedAdoptionOperation ||
            !MaintenanceHash(selection.VaultRootCommitmentSha256) || !MaintenanceHash(selection.RollbackRootCommitmentSha256) ||
            selection.VaultRootCommitmentSha256 == selection.RollbackRootCommitmentSha256 || selection.ActivationAuthority is null ||
            selection.ActivationAuthority.CustomerId != source.CustomerId || selection.ActivationAuthority.CellId != source.CellId ||
            !ValidDatabaseInstanceId(selection.ActivationAuthority.AuthorityId) || !MaintenanceHash(selection.ActivationAuthority.CustodyCommitmentSha256) ||
            selection.ActivationGeneration is < 0 or > FoundationCellActivationAuthority.MaximumGenerations ||
            (selection.ActivationGeneration == 0 ? selection.ActivationBindingSha256 is not null : !MaintenanceHash(selection.ActivationBindingSha256)))
            throw new FoundationStoreException("RestrictedRecoveryAdoptionTargetInvalid", 400);
    }
    static string RestrictedRecoveryAdoptionTargetJson(FoundationCredentialRestrictedRecoveryAdoptionTarget target)
    {
        ValidateRestrictedRecoveryAdoptionTarget(target); var json = JsonSerializer.Serialize(target, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumCredentialRestrictedRecoveryAdoptionTargetBytes) throw new FoundationStoreException("RestrictedRecoveryAdoptionTargetLimit", 400);
        return json;
    }
    static FoundationCredentialRestrictedRecoveryAdoptionTarget ParseRestrictedRecoveryAdoptionTarget(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumCredentialRestrictedRecoveryAdoptionTargetBytes) throw new JsonException();
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(json)); var raw = doc.RootElement;
            RequireRestrictedJointProfile(raw); RequireRestrictedJointProfile(raw.GetProperty("selection"));
            if (raw.GetProperty("operation").GetString() != RestrictedAdoptionOperation ||
                raw.GetProperty("selection").GetProperty("operation").GetString() != RestrictedAdoptionOperation) throw new JsonException();
            var value = raw.Deserialize<RestrictedAdoptionTargetDocument>(CredentialRecoverySets.Json) ?? throw new JsonException();
            var target = new FoundationCredentialRestrictedRecoveryAdoptionTarget(ParseRestrictedRecoveryJointTarget(value.RecoveryTarget.GetRawText()),
                value.DatabaseInstanceId, value.DatabasePathSha256, value.RecoveryHold, value.RuntimeBindingRequired, value.RuntimeBindingHistorySha256,
                value.RuntimeBindingHistoryCount, value.CurrentRuntimeBindingSha256, value.SourceRuntimeBinding, value.Selection);
            if (value.Schema != target.Schema || RestrictedRecoveryAdoptionTargetJson(target) != json) throw new JsonException(); return target;
        }
        catch (Exception error) when (error is JsonException or FoundationStoreException or CredentialException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException or OverflowException)
        { throw new FoundationStoreException("RestrictedRecoveryAdoptionIntegrityFailed"); }
    }
    static string RestrictedRecoveryAdoptionDigest(FoundationCredentialRestrictedRecoveryAdoptionIntent intent) => Hash(JsonSerializer.Serialize(new
    {
        schema = "platform-studio.credential-restricted-recovery-adoption-intent-digest.v1", accessProfile = CredentialRestrictedRecoveryStaging.Profile,
        operation = RestrictedAdoptionOperation, intent.Id, intent.CustomerId, intent.CellId, intent.TargetSha256, intent.RequesterKey, intent.RequesterVersion,
        requesterExpires = intent.RequesterExpiresAt.ToUnixTimeMilliseconds(), intent.PolicyVersion, intent.DatabaseInstanceId, intent.PurposeSha256,
        expires = intent.ExpiresAt.ToUnixTimeMilliseconds(), prepared = intent.PreparedAt.ToUnixTimeMilliseconds(), intent.PreparedVerificationSha256,
        verificationObserved = intent.PreparedVerificationObservedUtc.ToUnixTimeMilliseconds()
    }, CredentialRecoverySets.Json));

    FoundationCredentialRestrictedRecoveryAdoptionTarget CaptureRestrictedRecoveryAdoptionTarget(SqliteConnection db, SqliteTransaction tx,
        CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryAdoptionSession held)
    {
        if (!OperatingSystem.IsWindows()) throw new FoundationStoreException("RestrictedRecoveryAdoptionWindowsRequired", 403);
        var recovery = CredentialRestrictedRecoveryJointTarget(plan, held.Receipt, held.CheckpointAuthority, held.Checkpoint);
        var state = RestrictedRecoveryAdoptionSource(db, tx, plan.SourceNamespace);
        var target = new FoundationCredentialRestrictedRecoveryAdoptionTarget(recovery, state.Hold.DatabaseInstanceId,
            CredentialRuntimeDatabasePathDigest(databasePath), state.Hold, true, state.HistorySha256, state.Count, null, state.SourceBinding, held.Selection);
        ValidateRestrictedRecoveryAdoptionTarget(target); return target;
    }
    sealed record RestrictedAdoptionSource(FoundationRecoveryExecutionHold Hold, FoundationCredentialRuntimeBinding SourceBinding, string HistorySha256, int Count);
    RestrictedAdoptionSource RestrictedRecoveryAdoptionSource(SqliteConnection db, SqliteTransaction tx, CredentialVaultNamespaceIdentity sourceNamespace)
    {
        var hold = ReadCurrentRecoveryExecutionHold(db, tx);
        if (hold is null || hold.Origin != "RestoredSnapshot" || !ValidDatabaseInstanceId(hold.SourceDatabaseInstanceId))
            throw new FoundationStoreException("RestrictedRecoveryAdoptionVerifiedRestoreRequired", 403);
        ValidateAdministrativeAuditChain(db, tx, "RestrictedRecoveryAdoptionRestoreAuditInvalid");
        if (Number(db, tx, """
            SELECT count(*) FROM audit WHERE customer_id=$c AND cell_id=$cell AND action='BackupRestored' AND target_id=$cell
              AND outcome='Succeeded' AND reason='RecoveryPolicyChanged' AND correlation=$digest AND created>=$created
            """, ("$c", CustomerId), ("$cell", CellId), ("$digest", hold.BackupSha256), ("$created", hold.CreatedAt.ToUnixTimeMilliseconds())) == 0)
            throw new FoundationStoreException("RestrictedRecoveryAdoptionVerifiedRestoreRequired", 403);
        var rows = ReadCredentialRuntimeBindings(db, tx);
        if (!CredentialRuntimeBindingRequired(db, tx, rows.Count) || rows.Any(row => row.DatabaseInstanceId == hold.DatabaseInstanceId))
            throw new FoundationStoreException("RestrictedRecoveryAdoptionHistoricalBindingRequired", 403);
        var source = rows.SingleOrDefault(row => row.DatabaseInstanceId == hold.SourceDatabaseInstanceId);
        if (source is null || source.Namespace != sourceNamespace)
            throw new FoundationStoreException("RestrictedRecoveryAdoptionSourceBindingMismatch", 403);
        var history = rows.OrderBy(row => row.DatabaseInstanceId, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var digest = Hash(JsonSerializer.Serialize(new { schema = "platform-studio.credential-restricted-recovery-adoption-binding-history.v1", CustomerId, CellId, bindings = history }, CredentialRecoverySets.Json));
        return new(hold, source, digest, rows.Count);
    }

    static void RequireRestrictedRecoveryAdoptionVerification(FoundationCredentialRestrictedRecoveryAdoptionTarget target,
        CredentialRestrictedRecoveryStageVerification verification, DateTimeOffset earliest, DateTimeOffset now)
    {
        var stage = target.RecoveryTarget.StageTarget;
        if (verification is null || verification.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || !MaintenanceHash(verification.Sha256) ||
            !CredentialRecoverySets.Utc(verification.ObservedUtc) || verification.ObservedUtc < earliest || verification.ObservedUtc > now ||
            verification.PlanSha256 != stage.PlanSha256 || verification.ReceiptSha256 != stage.ReceiptSha256 || verification.SourceSetSha256 != stage.SourceSetSha256 ||
            verification.CompletionProofSha256 != stage.CompletionProofSha256 || verification.Receipt.CompletionProofSha256 != stage.CompletionProofSha256 ||
            !verification.DestinationCompletionProofAuthenticated || !verification.OutputCiphertextsAuthenticated || !verification.DestinationAbsenceProofsAuthenticated ||
            !verification.InactiveRecoveryHold || verification.NormalVaultUsable || verification.CompletionCoreSha256 != stage.CompletionCoreSha256 ||
            verification.AuthenticatedProfileCount != stage.ProfileCount || verification.AuthenticatedAbsenceCount != stage.AbsenceProofCount ||
            verification.Receipt.StageId != stage.StageId || verification.Receipt.DestinationKey != stage.DestinationKey ||
            verification.Receipt.DestinationRootCommitmentSha256 != stage.DestinationRootCommitmentSha256 || verification.Receipt.StagedVaultNamespaceId != stage.StagedVaultNamespaceId ||
            verification.Receipt.StagedAuthorityNamespaceId != stage.StagedAuthorityNamespaceId || verification.Receipt.BindingSha256 != stage.BindingSha256 || verification.Receipt.StateSha256 != stage.StateSha256)
            throw new FoundationStoreException("RestrictedRecoveryAdoptionVerificationMismatch", 403);
    }

    RestrictedRecoveryActor RestrictedRecoveryAdoptionContexts(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<CustomerContext> contexts,
        IReadOnlyList<FoundationCredentialRecoveryScope> scopes, string capability, string[] roles, DateTimeOffset now)
    {
        try { return RestrictedRecoveryContexts(db, tx, contexts, scopes, capability, roles, now); }
        catch (FoundationStoreException error) when (error.Code.StartsWith("RestrictedRecoveryStageReview", StringComparison.Ordinal))
        { throw new FoundationStoreException(error.Code.Replace("RestrictedRecoveryStageReview", "RestrictedRecoveryAdoption", StringComparison.Ordinal), error.Status); }
    }
    void RequireRestrictedRecoveryAdoptionAuthority(SqliteConnection db, SqliteTransaction tx, FoundationCredentialRestrictedRecoveryAdoptionIntent intent, DateTimeOffset now)
    {
        if (intent.State == "Quarantined") throw new FoundationStoreException("RestrictedRecoveryAdoptionQuarantined", 403);
        if (now < intent.PreparedAt || now >= intent.ExpiresAt || now >= intent.RequesterExpiresAt) throw new FoundationStoreException("RestrictedRecoveryAdoptionExpired", 403);
        if (ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1") != intent.DatabaseInstanceId) throw new FoundationStoreException("RestrictedRecoveryAdoptionInstanceChanged", 403);
        if (Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") != intent.PolicyVersion) throw new FoundationStoreException("PolicyChanged", 403);
        RestrictedRecoveryReviewLifecycle(db, tx);
        foreach (var scope in intent.Target.RecoveryTarget.StageTarget.Scopes)
            RestrictedRecoveryStandingGrant(db, tx, intent.RequesterKey, scope, ["Operator"], intent.RequesterVersion);
    }
    void AuditRestrictedRecoveryAdoptionAt(SqliteConnection db, SqliteTransaction tx, FoundationCredentialRestrictedRecoveryAdoptionIntent intent,
        string actor, string action, string outcome, string reason, DateTimeOffset now)
    {
        foreach (var scope in intent.Target.RecoveryTarget.StageTarget.Scopes)
            RestrictedRecoveryAuditAt(db, tx, actor, scope.ProjectId, scope.EnvironmentId, action, intent.Id, outcome, reason, intent.Digest, now);
    }

    FoundationCredentialRestrictedRecoveryAdoptionIntent ReadRestrictedRecoveryAdoptionIntent(SqliteConnection db, SqliteTransaction tx, string requestId)
    {
        ValidateId(requestId); string? targetJson = null, project = null, environment = null;
        try
        {
            var intent = Rows(db, tx, """
                SELECT id,project_id,environment_id,target,target_sha256,requester,requester_version,requester_expires,policy_version,
                  database_instance_id,purpose_sha256,expires,digest,state,created,verification_sha256,verification_observed
                FROM credential_restricted_recovery_adoption_intents WHERE customer_id=$c AND id=$id
                """, r =>
            {
                project = r.GetString(1); environment = r.GetString(2); targetJson = r.GetString(3);
                return new FoundationCredentialRestrictedRecoveryAdoptionIntent(r.GetString(0), CustomerId, CellId, ParseRestrictedRecoveryAdoptionTarget(targetJson),
                    r.GetString(4), r.GetString(5), r.GetInt64(6), At(r.GetInt64(7)), r.GetInt64(8), r.GetString(9), r.GetString(10), At(r.GetInt64(11)),
                    At(r.GetInt64(14)), r.GetString(15), At(r.GetInt64(16)), r.GetString(12), r.GetString(13), null);
            }, ("$c", CustomerId), ("$id", requestId)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
            var approval = Rows(db, tx, """
                SELECT actor_key,actor_version,context_expires,digest,verification_sha256,verification_observed,created
                FROM credential_restricted_recovery_adoption_approvals WHERE customer_id=$c AND request_id=$id
                """, r => new FoundationCredentialRestrictedRecoveryAdoptionApproval(r.GetString(0), r.GetInt64(1), At(r.GetInt64(2)),
                    r.GetString(3), r.GetString(4), At(r.GetInt64(5)), At(r.GetInt64(6))), ("$c", CustomerId), ("$id", requestId)).SingleOrDefault();
            intent = intent with { Approval = approval };
            bool Counter(long value) => value is > 0 and <= 9007199254740991;
            var target = intent.Target; var source = target.RecoveryTarget.StageTarget.SourceNamespace; var scope = target.RecoveryTarget.StageTarget.Scopes[0];
            if (!ValidDatabaseInstanceId(intent.Id) || !ValidDatabaseInstanceId(intent.DatabaseInstanceId) || intent.DatabaseInstanceId != target.DatabaseInstanceId ||
                source.CustomerId != CustomerId || source.CellId != CellId || project != scope.ProjectId || environment != scope.EnvironmentId ||
                !Counter(intent.RequesterVersion) || !Counter(intent.PolicyVersion) || intent.PolicyVersion < target.RecoveryHold.PolicyVersion ||
                !MaintenanceHash(intent.PurposeSha256) || !MaintenanceHash(intent.TargetSha256) || Hash(targetJson!) != intent.TargetSha256 ||
                !MaintenanceHash(intent.PreparedVerificationSha256) || !MaintenanceHash(intent.Digest) || RestrictedRecoveryAdoptionDigest(intent) != intent.Digest ||
                intent.PreparedAt < target.RecoveryHold.CreatedAt || intent.PreparedAt < target.RecoveryTarget.Checkpoint.AcceptedUtc ||
                intent.ExpiresAt <= intent.PreparedAt || intent.ExpiresAt > intent.PreparedAt.AddMinutes(15) || intent.RequesterExpiresAt < intent.ExpiresAt ||
                intent.PreparedVerificationObservedUtc < target.RecoveryHold.CreatedAt || intent.PreparedVerificationObservedUtc < target.RecoveryTarget.Checkpoint.AcceptedUtc ||
                intent.PreparedVerificationObservedUtc > intent.PreparedAt || intent.State is not ("Prepared" or "Approved" or "Quarantined") ||
                intent.State == "Prepared" && approval is not null || intent.State == "Approved" && approval is null)
                throw new FoundationStoreException("RestrictedRecoveryAdoptionIntegrityFailed");
            RequireHuman(intent.RequesterKey); PublicRestrictedRecoveryString(intent.RequesterKey);
            if (approval is not null)
            {
                if (approval.IntentDigest != intent.Digest || approval.ApproverKey == intent.RequesterKey || !Counter(approval.ApproverVersion) ||
                    !MaintenanceHash(approval.StageVerificationSha256) || approval.ApprovedAt < intent.PreparedAt || approval.ApprovedAt >= intent.ExpiresAt ||
                    approval.ApprovedAt >= intent.RequesterExpiresAt || approval.ApprovedAt >= approval.ApproverExpiresAt ||
                    approval.VerificationObservedUtc < intent.PreparedAt || approval.VerificationObservedUtc > approval.ApprovedAt)
                    throw new FoundationStoreException("RestrictedRecoveryAdoptionIntegrityFailed");
                RequireHuman(approval.ApproverKey); PublicRestrictedRecoveryString(approval.ApproverKey);
            }
            return intent;
        }
        catch (FoundationStoreException error) when (error.Code != "NotFound") { throw new FoundationStoreException("RestrictedRecoveryAdoptionIntegrityFailed"); }
        catch (Exception error) when (error is JsonException or CredentialException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        { throw new FoundationStoreException("RestrictedRecoveryAdoptionIntegrityFailed"); }
    }
}
