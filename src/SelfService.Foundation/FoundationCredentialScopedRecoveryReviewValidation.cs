using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    sealed record ScopedRecoveryTargetDocument(string Schema, string PlanSha256, string ReceiptSha256, string SourceSetSha256, string SourceInventorySha256,
        CredentialVaultNamespaceIdentity SourceNamespace, string DestinationRootCommitmentSha256, CredentialRecoveryDestinationKeyIdentity DestinationKey,
        string StageId, string StagedVaultNamespaceId, string StagedAuthorityNamespaceId, string BindingSha256, string StateSha256,
        string CompletionCoreSha256, int ProfileCount, int AbsenceProofCount, FoundationCredentialRecoveryScope[] Scopes);
    sealed record ScopedRecoveryResultDocument(string RequestDigest, string TargetSha256, string StageVerificationSha256, string PlanSha256,
        string ReceiptSha256, string SourceSetSha256, DateTimeOffset ObservedAt, string Schema, string Status, bool InactiveRecoveryHold,
        bool PromotionAuthorized, bool LiveAdoptionAuthorized, bool ActivationPerformed, bool ExternalExecutionAuthorized, string IndependentBackupCoverage);

    internal static FoundationCredentialScopedRecoveryStageTarget CredentialScopedRecoveryStageReviewTarget(CredentialScopedRecoveryStagePlan plan, CredentialScopedRecoveryStageReceipt receipt)
    {
        try
        {
            var scopes = ScopedRecoveryScopes(plan);
            if (plan.Sha256 != CredentialScopedRecoveryStaging.PlanDigest(plan) || receipt is null || receipt.Sha256 != CredentialScopedRecoveryStaging.ReceiptDigest(receipt) ||
                receipt.PlanSha256 != plan.Sha256 || receipt.SourceSetSha256 != plan.SourceSetAuthority.ArtifactSha256 ||
                receipt.DestinationRootCommitmentSha256 != plan.DestinationRootCommitmentSha256 || receipt.DestinationKey != plan.DestinationKey ||
                receipt.Absences.Count != plan.SourceInventory.AbsenceScopes.Count || receipt.Profiles.Count != plan.SourceInventory.ProfileDependencies.Count ||
                plan.SourceSignature.ProfileCount != receipt.Profiles.Count || plan.SourceSignature.AbsenceProofCount != receipt.Absences.Count ||
                receipt.CompletionCoreSha256 != CredentialScopedRecoveryStaging.CompletionCoreDigest(plan, receipt.StageId,
                    receipt.StagedVaultNamespaceId, receipt.StagedAuthorityNamespaceId, receipt.BindingSha256, receipt.ReservedUtc, receipt.CompletedUtc, receipt.Profiles) ||
                plan.SourceSignature.SourceNamespace != plan.SourceNamespace || plan.SourceSignature.InventorySha256 != plan.SourceInventory.Sha256)
                throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
            CredentialScopedRecoveryStaging.ValidateCompletedReceiptTime(plan, receipt, DateTimeOffset.MaxValue);
            for (var i = 0; i < receipt.Profiles.Count; i++)
            {
                var profile = receipt.Profiles[i]; var original = plan.SourceInventory.ProfileDependencies[i];
                if (JsonSerializer.Serialize(profile.Source, CredentialRules.Json) != JsonSerializer.Serialize(original, CredentialRules.Json) ||
                    profile.PreservedProfileSha256 != plan.PreservedProfiles[i].ProfileSha256 || profile.Source.ProfileIdSha256 != plan.PreservedProfiles[i].ProfileIdSha256 ||
                    profile.Destination.SourceKeyId != plan.DestinationKey.KeyId || !CredentialScopedRecoveryStaging.PreservedDependency(profile.Source, profile.Destination))
                    throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
            }
            for (var i = 0; i < receipt.Absences.Count; i++)
            {
                var absence = receipt.Absences[i]; var original = plan.SourceInventory.AbsenceScopes[i];
                if (absence.Source != original || absence.Destination.SourceKeyId != plan.DestinationKey.KeyId ||
                    !CredentialScopedRecoveryStaging.PreservedAbsenceDependency(original, absence.Destination) || !MaintenanceHash(absence.Destination.ProofSha256))
                    throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
            }
            var target = new FoundationCredentialScopedRecoveryStageTarget(plan.Sha256, receipt.Sha256, receipt.SourceSetSha256, plan.SourceInventory.Sha256,
                plan.SourceNamespace, plan.DestinationRootCommitmentSha256, plan.DestinationKey, receipt.StageId, receipt.StagedVaultNamespaceId,
                receipt.StagedAuthorityNamespaceId, receipt.BindingSha256, receipt.StateSha256, receipt.CompletionCoreSha256,
                receipt.Profiles.Count, receipt.Absences.Count, scopes);
            ValidateScopedRecoveryTarget(target); return target;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is CredentialException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400); }
    }
    static IReadOnlyList<FoundationCredentialRecoveryScope> ScopedRecoveryScopes(CredentialScopedRecoveryStagePlan plan)
    {
        if (plan is null || plan.SourceInventory is null) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        plan.SourceInventory.ValidateIntegrity();
        CredentialScopedRecoverySets.RequireSignature(plan.SourceSignature);
        if (plan.SourceInventory.ProfileDependencies.Count + plan.SourceInventory.AbsenceScopes.Count == 0)
            throw new FoundationStoreException("ScopedRecoveryStageReviewEmptySource", 400);
        if (plan.SourceInventory.Sha256 != plan.SourceSignature.InventorySha256 || plan.SourceInventory.Namespace != plan.SourceNamespace ||
            plan.SourceSignature.SourceNamespace != plan.SourceNamespace || plan.Sha256 != CredentialScopedRecoveryStaging.PlanDigest(plan))
            throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        var scopes = plan.SourceInventory.ProfileDependencies.Select(p => new FoundationCredentialRecoveryScope(p.ProjectId, p.EnvironmentId))
            .Concat(plan.SourceInventory.AbsenceScopes.Select(p => new FoundationCredentialRecoveryScope(p.ProjectId, p.EnvironmentId))).Distinct()
            .OrderBy(s => s.ProjectId, StringComparer.Ordinal).ThenBy(s => s.EnvironmentId, StringComparer.Ordinal).Take(MaximumCredentialScopedRecoveryScopes + 1).ToArray();
        if (scopes.Length is < 1 or > MaximumCredentialScopedRecoveryScopes) throw new FoundationStoreException("ScopedRecoveryStageReviewScopeLimit", 400);
        if (!plan.AffectedScopes.SequenceEqual(scopes)) throw new FoundationStoreException("ScopedRecoveryStageReviewScopeMismatch", 400);
        return Array.AsReadOnly(scopes);
    }

    ScopedRecoveryActor ScopedRecoveryContextIdentity(IReadOnlyList<CustomerContext> contexts, IReadOnlyList<FoundationCredentialRecoveryScope> scopes, string capability)
    {
        if (contexts is null || contexts.Count != scopes.Count || contexts.Count is < 1 or > MaximumCredentialScopedRecoveryScopes)
            throw new FoundationStoreException("ScopedRecoveryStageReviewContextsRequired", 403);
        var seen = new HashSet<FoundationCredentialRecoveryScope>(); string? actor = null; long version = 0; var expires = DateTimeOffset.MaxValue;
        foreach (var context in contexts)
        {
            if (context is null || context.CustomerId != CustomerId || context.CellId != CellId || context.Kind != PrincipalKind.User || context.Capability != capability ||
                context.SupportApprovalId is not null || context.SupportPurpose is not null || context.SupportValidUntilUtc is not null ||
                context.RoleVersion is < 1 or > 9007199254740991) throw new FoundationStoreException("ScopedRecoveryStageReviewContextDenied", 403);
            RequireHuman(context.ActorKey); ValidateApplicationScopeId(context.ProjectId); ValidateApplicationScopeId(context.EnvironmentId);
            PublicScopedRecoveryString(context.ActorKey);
            if (actor is not null && (actor != context.ActorKey || version != context.RoleVersion)) throw new FoundationStoreException("ScopedRecoveryStageReviewActorMismatch", 403);
            actor = context.ActorKey; version = context.RoleVersion;
            if (!seen.Add(new(context.ProjectId, context.EnvironmentId))) throw new FoundationStoreException("ScopedRecoveryStageReviewDuplicateScope", 403);
            if (context.ExpiresUtc < expires) expires = context.ExpiresUtc;
        }
        if (!seen.SetEquals(scopes)) throw new FoundationStoreException("ScopedRecoveryStageReviewScopeMismatch", 403);
        return new(actor!, version, At(expires.ToUnixTimeMilliseconds()));
    }
    ScopedRecoveryActor ScopedRecoveryContexts(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<CustomerContext> contexts,
        IReadOnlyList<FoundationCredentialRecoveryScope> scopes, string capability, string[] roles)
    {
        var actor = ScopedRecoveryContextIdentity(contexts, scopes, capability);
        if (actor.ExpiresAt <= clock.GetUtcNow()) throw new FoundationStoreException("ScopedRecoveryStageReviewContextExpired", 403);
        foreach (var scope in scopes)
        {
            ScopedRecoveryStandingGrant(db, tx, actor.Key, scope, roles, actor.Version);
            if (Number(db, tx, "SELECT count(*) FROM scopes WHERE customer_id=$c AND project_id=$p AND environment_id=$e", ("$c", CustomerId), ("$p", scope.ProjectId), ("$e", scope.EnvironmentId)) != 1)
                throw new FoundationStoreException("ScopedRecoveryStageReviewScopeMismatch", 403);
        }
        return actor;
    }
    void ScopedRecoveryStandingGrant(SqliteConnection db, SqliteTransaction tx, string actor, FoundationCredentialRecoveryScope scope, string[] roles, long version)
    {
        ValidateMaintenanceStandingGrant(db, tx, actor, scope.ProjectId, scope.EnvironmentId);
        if (Require(db, tx, actor, scope.ProjectId, scope.EnvironmentId, roles, requireActive: false) != version)
            throw new FoundationStoreException("ScopedRecoveryStageReviewGrantChanged", 403);
    }
    void ScopedRecoveryReviewLifecycle(SqliteConnection db, SqliteTransaction tx)
    {
        var lifecycle = ReadLifecycle(db, tx);
        if (lifecycle.State is not ("Active" or "Suspended" or "Offboarding") || (lifecycle.State == "Active") != lifecycle.Enabled)
            throw new FoundationStoreException("ScopedRecoveryStageReviewLifecycleDenied", 403);
    }
    void RequireScopedRecoveryReviewAuthority(SqliteConnection db, SqliteTransaction tx, FoundationCredentialScopedRecoveryStageReview review)
    {
        var now = clock.GetUtcNow();
        if (review.State == "Quarantined") throw new FoundationStoreException("ScopedRecoveryStageReviewQuarantined", 403);
        if (now < review.PreparedAt || now >= review.ExpiresAt || now >= review.RequesterExpiresAt) throw new FoundationStoreException("ScopedRecoveryStageReviewExpired", 403);
        if (ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1") != review.DatabaseInstanceId) throw new FoundationStoreException("ScopedRecoveryStageReviewInstanceChanged", 403);
        if (Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") != review.PolicyVersion) throw new FoundationStoreException("PolicyChanged", 403);
        ScopedRecoveryReviewLifecycle(db, tx);
        foreach (var scope in review.Target.Scopes)
        {
            ScopedRecoveryStandingGrant(db, tx, review.RequesterKey, scope, ["Operator"], review.RequesterVersion);
            if (review.ApproverKey is not null) ScopedRecoveryStandingGrant(db, tx, review.ApproverKey, scope, ["Approver"], review.ApproverVersion!.Value);
        }
        if (review.ApproverKey is not null && (review.ApproverExpiresAt <= now || review.ApprovedAt > now)) throw new FoundationStoreException("ScopedRecoveryStageReviewApprovalExpired", 403);
    }
    void RequireScopedRecoveryReviewApproved(SqliteConnection db, SqliteTransaction tx, FoundationCredentialScopedRecoveryStageReview review, string executor)
    {
        if (review.State != "Approved" || review.ApproverKey is null) throw new FoundationStoreException("ScopedRecoveryStageReviewApprovalRequired", 403);
        if (review.Execution is not null) throw new FoundationStoreException("ScopedRecoveryStageReviewAlreadyConsumed");
        if (review.ApproverKey == executor) throw new FoundationStoreException("ApprovalSeparationRequired", 403);
        RequireScopedRecoveryReviewAuthority(db, tx, review);
    }
    FoundationCredentialScopedRecoveryStageReview RequireScopedRecoveryReviewAdmission(SqliteConnection db, SqliteTransaction tx,
        CredentialScopedRecoveryStageReviewAdmission admission, bool permitSucceeded = false)
    {
        if (admission is null || admission.Review.CustomerId != CustomerId || admission.Review.CellId != CellId) throw new FoundationStoreException("ScopedRecoveryStageReviewAdmissionMismatch", 403);
        var review = ReadScopedRecoveryReview(db, tx, admission.Review.Id); var execution = review.Execution;
        if (review.State == "Quarantined" || review.Digest != admission.Review.Digest || execution is null || execution.AdmissionId != admission.AdmissionId ||
            execution.ExecutorKey != admission.ExecutorKey || execution.ExecutorVersion != admission.ExecutorVersion || review.DatabaseInstanceId != admission.DatabaseInstanceId ||
            execution.State != "OutcomeUnknown" && !(permitSucceeded && execution.State == "Succeeded")) throw new FoundationStoreException("ScopedRecoveryStageReviewAdmissionMismatch", 403);
        return review;
    }
    void AuditScopedRecoveryReview(SqliteConnection db, SqliteTransaction tx, FoundationCredentialScopedRecoveryStageReview review, string actor,
        string action, string outcome, string reason, string correlation)
    { foreach (var scope in review.Target.Scopes) Audit(db, tx, actor, scope.ProjectId, scope.EnvironmentId, action, review.Id, outcome, reason, correlation); }

    static void ValidateScopedRecoveryTarget(FoundationCredentialScopedRecoveryStageTarget target)
    {
        if (target is null || target.Scopes is null || target.Scopes.Count is < 1 or > MaximumCredentialScopedRecoveryScopes) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        CredentialRecoverySets.RequireIdentity(target.SourceNamespace);
        if (target.SourceNamespace.AuthorityNamespaceId is null) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        foreach (var hash in new[] { target.PlanSha256, target.ReceiptSha256, target.SourceSetSha256, target.SourceInventorySha256,
            target.DestinationRootCommitmentSha256, target.BindingSha256, target.StateSha256, target.CompletionCoreSha256 }) RequireMaintenanceHash(hash);
        if (target.ProfileCount < 0 || target.AbsenceProofCount < 0 || target.ProfileCount + (long)target.AbsenceProofCount is < 1 or > 256 ||
            target.Scopes.Count > target.ProfileCount + target.AbsenceProofCount) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        if (target.DestinationKey is null || target.DestinationKey.CustomerId != target.SourceNamespace.CustomerId || target.DestinationKey.CellId != target.SourceNamespace.CellId ||
            !CredentialRules.ProfileId(target.DestinationKey.KeyId) || !MaintenanceHash(target.DestinationKey.CommitmentSha256)) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        PublicScopedRecoveryString(target.DestinationKey.KeyId);
        var ids = new[] { target.StageId, target.StagedVaultNamespaceId, target.StagedAuthorityNamespaceId };
        if (ids.Any(id => !ValidDatabaseInstanceId(id) || id == target.SourceNamespace.VaultNamespaceId || id == target.SourceNamespace.AuthorityNamespaceId) || ids.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new FoundationStoreException("ScopedRecoveryStageReviewTargetMismatch", 400);
        foreach (var scope in target.Scopes)
        {
            if (scope is null || !CustomerContextResolver.ValidId(scope.ProjectId) || !CustomerContextResolver.ValidId(scope.EnvironmentId)) throw new FoundationStoreException("ScopedRecoveryStageReviewScopeMismatch", 400);
            PublicScopedRecoveryString(scope.ProjectId); PublicScopedRecoveryString(scope.EnvironmentId);
        }
        if (!target.Scopes.SequenceEqual(target.Scopes.Distinct().OrderBy(s => s.ProjectId, StringComparer.Ordinal).ThenBy(s => s.EnvironmentId, StringComparer.Ordinal)))
            throw new FoundationStoreException("ScopedRecoveryStageReviewScopeMismatch", 400);
    }
    static string ScopedRecoveryTargetJson(FoundationCredentialScopedRecoveryStageTarget target)
    {
        ValidateScopedRecoveryTarget(target); var json = JsonSerializer.Serialize(target, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumCredentialScopedRecoveryTargetBytes) throw new FoundationStoreException("ScopedRecoveryStageReviewTargetLimit", 400);
        return json;
    }
    static FoundationCredentialScopedRecoveryStageTarget ParseScopedRecoveryTarget(string json)
    {
        try
        {
            if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumCredentialScopedRecoveryTargetBytes) throw new JsonException();
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(json));
            if (!doc.RootElement.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array || scopes.GetArrayLength() is < 1 or > MaximumCredentialScopedRecoveryScopes) throw new JsonException();
            var raw = doc.RootElement.Deserialize<ScopedRecoveryTargetDocument>(CredentialRecoverySets.Json) ?? throw new JsonException();
            var target = new FoundationCredentialScopedRecoveryStageTarget(raw.PlanSha256, raw.ReceiptSha256, raw.SourceSetSha256, raw.SourceInventorySha256, raw.SourceNamespace,
                raw.DestinationRootCommitmentSha256, raw.DestinationKey, raw.StageId, raw.StagedVaultNamespaceId, raw.StagedAuthorityNamespaceId, raw.BindingSha256,
                raw.StateSha256, raw.CompletionCoreSha256, raw.ProfileCount, raw.AbsenceProofCount, raw.Scopes);
            if (raw.Schema != target.Schema || ScopedRecoveryTargetJson(target) != json) throw new JsonException(); return target;
        }
        catch (Exception error) when (error is JsonException or CredentialException or FoundationStoreException or ArgumentException or NullReferenceException)
        { throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed"); }
    }
    static string ScopedRecoveryResultJson(FoundationCredentialScopedRecoveryStageReviewResult result)
    {
        var json = JsonSerializer.Serialize(result, CredentialRecoverySets.Json);
        if (Encoding.UTF8.GetByteCount(json) > 8192) throw new FoundationStoreException("ScopedRecoveryStageReviewResultLimit", 400); return json;
    }
    static FoundationCredentialScopedRecoveryStageReviewResult ParseScopedRecoveryResult(string json)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 8192) throw new JsonException();
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(json)); var raw = doc.RootElement.Deserialize<ScopedRecoveryResultDocument>(CredentialRecoverySets.Json) ?? throw new JsonException();
            var result = new FoundationCredentialScopedRecoveryStageReviewResult(raw.RequestDigest, raw.TargetSha256, raw.StageVerificationSha256, raw.PlanSha256, raw.ReceiptSha256, raw.SourceSetSha256, raw.ObservedAt);
            if (ScopedRecoveryResultJson(result) != json) throw new JsonException(); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException) { throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed"); }
    }
    static string ScopedRecoveryReviewDigest(FoundationCredentialScopedRecoveryStageReview q) => Hash(JsonSerializer.Serialize(new
    { schema = "platform-studio.credential-scoped-recovery-stage-review.v1", q.Id, q.CustomerId, q.CellId, q.ProjectId, q.EnvironmentId, q.TargetSha256,
        q.RequesterKey, q.RequesterVersion, requesterExpires = q.RequesterExpiresAt.ToUnixTimeMilliseconds(), q.PolicyVersion, q.DatabaseInstanceId,
        q.PurposeSha256, expires = q.ExpiresAt.ToUnixTimeMilliseconds(), prepared = q.PreparedAt.ToUnixTimeMilliseconds() }, CredentialRecoverySets.Json));
    static void PublicScopedRecoveryString(string value)
    {
        try { _ = CanonicalJson(JsonSerializer.Serialize(new { value }, Json)); }
        catch (FoundationStoreException) { throw new FoundationStoreException("ScopedRecoveryStageReviewPublicMetadataInvalid", 400); }
    }
    static string ScopedRecoveryReviewReason(string value) => value switch
    { "StagePreparationRejected" or "StageVerificationInterrupted" or "ReviewCompletionUnavailable" or "CurrentAuthorityChanged" or "CallerCancelled" or "StageChanged" => value,
        _ => "StageVerificationInterrupted" };

    FoundationCredentialScopedRecoveryStageReview ReadScopedRecoveryReview(SqliteConnection db, SqliteTransaction tx, string requestId)
    {
        ValidateId(requestId); string? targetJson = null, resultJson = null, executionInstance = null; long updated = 0;
        try
        {
            var review = Rows(db, tx, """
                SELECT id,project_id,environment_id,target,target_sha256,requester,requester_version,requester_expires,policy_version,
                  database_instance_id,purpose_sha256,expires,digest,state,created FROM credential_scoped_recovery_stage_review_requests WHERE customer_id=$c AND id=$id
                """, r =>
                {
                    targetJson = r.GetString(3);
                    return new FoundationCredentialScopedRecoveryStageReview(r.GetString(0), CustomerId, CellId, r.GetString(1), r.GetString(2), ParseScopedRecoveryTarget(targetJson),
                        r.GetString(4), r.GetString(5), r.GetInt64(6), At(r.GetInt64(7)), r.GetInt64(8), r.GetString(9), r.GetString(10), At(r.GetInt64(11)),
                        At(r.GetInt64(14)), r.GetString(12), r.GetString(13), null, null, null, null, null);
                }, ("$c", CustomerId), ("$id", requestId)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
            var approvals = Rows(db, tx, "SELECT actor_key,actor_version,context_expires,digest,created FROM credential_scoped_recovery_stage_review_approvals WHERE customer_id=$c AND request_id=$id",
                r => (Actor: r.GetString(0), Version: r.GetInt64(1), Expires: At(r.GetInt64(2)), Digest: r.GetString(3), Created: At(r.GetInt64(4))), ("$c", CustomerId), ("$id", requestId));
            if (approvals.Count > 1 || approvals.Count == 1 && approvals[0].Digest != review.Digest) throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
            var execution = Rows(db, tx, """
                SELECT admission_id,executor_key,executor_version,database_instance_id,state,reason,result_json,result_sha256,admitted,completed,updated
                FROM credential_scoped_recovery_stage_review_executions WHERE customer_id=$c AND request_id=$id
                """, r =>
                {
                    executionInstance = r.GetString(3); resultJson = r.IsDBNull(6) ? null : r.GetString(6); updated = r.GetInt64(10);
                    return new FoundationCredentialScopedRecoveryStageReviewExecution(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(4), r.GetString(5),
                        resultJson is null ? null : ParseScopedRecoveryResult(resultJson), r.IsDBNull(7) ? null : r.GetString(7), At(r.GetInt64(8)), r.IsDBNull(9) ? null : At(r.GetInt64(9)));
                }, ("$c", CustomerId), ("$id", requestId)).SingleOrDefault();
            review = review with
            {
                ApproverKey = approvals.Count == 0 ? null : approvals[0].Actor, ApproverVersion = approvals.Count == 0 ? null : approvals[0].Version,
                ApproverExpiresAt = approvals.Count == 0 ? null : approvals[0].Expires, ApprovedAt = approvals.Count == 0 ? null : approvals[0].Created, Execution = execution
            };
            ValidateScopedRecoveryReviewRead(review, targetJson!, resultJson, executionInstance, updated); return review;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is JsonException or CredentialException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        { throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed"); }
    }

    void ValidateScopedRecoveryReviewRead(FoundationCredentialScopedRecoveryStageReview review, string targetJson, string? resultJson, string? executionInstance, long updated)
    {
        bool ValidVersion(long value) => value is > 0 and <= 9007199254740991;
        if (!ValidDatabaseInstanceId(review.Id) || !ValidDatabaseInstanceId(review.DatabaseInstanceId) || !ValidVersion(review.RequesterVersion) || !ValidVersion(review.PolicyVersion) ||
            !MaintenanceHash(review.Digest) || !MaintenanceHash(review.TargetSha256) || !MaintenanceHash(review.PurposeSha256) ||
            review.Target.SourceNamespace.CustomerId != CustomerId || review.Target.SourceNamespace.CellId != CellId ||
            review.ProjectId != review.Target.Scopes[0].ProjectId || review.EnvironmentId != review.Target.Scopes[0].EnvironmentId ||
            Hash(targetJson) != review.TargetSha256 || ScopedRecoveryReviewDigest(review) != review.Digest ||
            review.ExpiresAt <= review.PreparedAt || review.ExpiresAt > review.PreparedAt.AddMinutes(15) || review.RequesterExpiresAt < review.ExpiresAt ||
            review.State is not ("Prepared" or "Approved" or "Quarantined")) throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        RequireHuman(review.RequesterKey); PublicScopedRecoveryString(review.RequesterKey);
        if (review.ApproverKey is null)
        {
            if (review.State == "Approved" || review.ApproverVersion is not null || review.ApproverExpiresAt is not null || review.ApprovedAt is not null || review.Execution is not null)
                throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        }
        else
        {
            RequireHuman(review.ApproverKey); PublicScopedRecoveryString(review.ApproverKey);
            if (review.State == "Prepared" || review.ApproverKey == review.RequesterKey || review.ApproverVersion is null || !ValidVersion(review.ApproverVersion.Value) ||
                review.ApproverExpiresAt is null || review.ApprovedAt is null || review.ApprovedAt < review.PreparedAt || review.ApprovedAt >= review.ExpiresAt ||
                review.ApproverExpiresAt <= review.ApprovedAt) throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        }
        var execution = review.Execution; if (execution is null) return;
        RequireHuman(execution.ExecutorKey); PublicScopedRecoveryString(execution.ExecutorKey);
        if (!ValidDatabaseInstanceId(execution.AdmissionId) || !ValidVersion(execution.ExecutorVersion) || executionInstance != review.DatabaseInstanceId ||
            execution.ExecutorKey == review.ApproverKey || execution.AdmittedAt < review.ApprovedAt || execution.AdmittedAt >= review.ExpiresAt ||
            execution.AdmittedAt >= review.ApproverExpiresAt || execution.AdmittedAt >= review.RequesterExpiresAt || updated < execution.AdmittedAt.ToUnixTimeMilliseconds())
            throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        if (execution.State == "OutcomeUnknown")
        {
            if (execution.Result is not null || execution.ResultSha256 is not null || execution.CompletedAt is not null ||
                execution.Reason != "AdmittedBeforeVerification" && ScopedRecoveryReviewReason(execution.Reason) != execution.Reason) throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        }
        else if (execution.State == "Succeeded")
        {
            var result = execution.Result;
            if (review.State == "Quarantined" || result is null || resultJson is null || execution.CompletedAt is null ||
                execution.Reason != "HeldStageVerificationRecorded" || !MaintenanceHash(execution.ResultSha256) || Hash(resultJson) != execution.ResultSha256 ||
                result.RequestDigest != review.Digest || result.TargetSha256 != review.TargetSha256 || !MaintenanceHash(result.StageVerificationSha256) ||
                result.PlanSha256 != review.Target.PlanSha256 || result.ReceiptSha256 != review.Target.ReceiptSha256 || result.SourceSetSha256 != review.Target.SourceSetSha256 ||
                !CredentialRecoverySets.Utc(result.ObservedAt) || result.ObservedAt < execution.AdmittedAt || result.ObservedAt > execution.CompletedAt ||
                execution.CompletedAt >= review.ExpiresAt || execution.CompletedAt >= review.ApproverExpiresAt || execution.CompletedAt >= review.RequesterExpiresAt ||
                updated != execution.CompletedAt.Value.ToUnixTimeMilliseconds()) throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
        }
        else throw new FoundationStoreException("ScopedRecoveryStageReviewIntegrityFailed");
    }
}
