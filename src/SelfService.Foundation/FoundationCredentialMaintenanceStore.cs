using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialMaintenanceIntents = 2048, MaximumPendingCredentialMaintenanceIntents = 64;
    const long MaintenanceCounterLimit = 9007199254740991;

    public IReadOnlyList<FoundationMembership> GetCredentialMaintenanceMemberships(string customerId, string actorKey)
    {
        Scope(customerId); RequireHuman(actorKey); using var db = Open();
        return Rows(db, null, """
            SELECT m.project_id,m.environment_id,m.roles,a.version,m.enabled FROM memberships m
            JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key
            WHERE m.customer_id=$c AND m.actor_key=$a ORDER BY m.project_id,m.environment_id
            """, r => new FoundationMembership(CustomerId, CellId, actorKey, r.GetString(0), r.GetString(1),
                CheckedMaintenanceRoles(r.GetString(2), skipSupportOnly: true).Where(role => role is "Operator" or "Approver" or "Auditor").ToArray(),
                r.GetInt64(3), r.GetInt64(4) == 1), ("$c", CustomerId), ("$a", actorKey)).Where(m => m.Roles.Length > 0).ToArray();
    }

    public FoundationCredentialMaintenanceIntent PrepareCredentialMaintenance(CustomerContext requester, CredentialMaintenanceOperation operation,
        FoundationCredentialMaintenanceTarget target, string purpose, DateTimeOffset expiresUtc)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContext(db, tx, requester, FoundationCapabilities.CredentialMaintenancePrepare, ["Operator"]);
        var scope = ValidateMaintenanceTarget(target, CustomerId, CellId); MaintenanceScope(requester, scope); MaintenanceLifecycle(db, tx, operation);
        ValidatePurpose(purpose);
        if (expiresUtc <= clock.GetUtcNow() || expiresUtc > clock.GetUtcNow().AddMinutes(15)) throw new FoundationStoreException("InvalidMaintenanceExpiry", 400);
        if (Number(db, tx, "SELECT count(*) FROM credential_maintenance_intents WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumCredentialMaintenanceIntents)
            throw new FoundationStoreException("MaintenanceHistoryCapacityExceeded", 429);
        if (Number(db, tx, """
            SELECT count(*) FROM credential_maintenance_intents i WHERE i.customer_id=$c AND i.state IN ('Prepared','Approved') AND i.expires>$now
            AND NOT EXISTS(SELECT 1 FROM credential_maintenance_executions e WHERE e.customer_id=i.customer_id AND e.intent_id=i.id)
            """, ("$c", CustomerId), ("$now", Now)) >= MaximumPendingCredentialMaintenanceIntents)
            throw new FoundationStoreException("MaintenancePendingCapacityExceeded", 429);
        var connection = MaintenanceConnection(db, tx, scope, target, operation);
        var id = NewId(); var canonical = MaintenanceTargetJson(target); var targetSha = Hash(canonical);
        var draft = new FoundationCredentialMaintenanceIntent(id, CustomerId, CellId, requester.ProjectId, requester.EnvironmentId,
            operation, target, targetSha, requester.ActorKey, requester.RoleVersion, connection.Id, connection.Generation,
            Hash(CanonicalJson(connection.ConfigurationJson)), Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"),
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), Hash(purpose), At(expiresUtc.ToUnixTimeMilliseconds()),
            "", "Prepared", null, null, null);
        draft = draft with { Digest = MaintenanceDigest(draft) };
        Exec(db, tx, """
            INSERT INTO credential_maintenance_intents VALUES($c,$id,$p,$e,$op,$actor,$version,$target,$targetsha,$conn,$cg,$cs,$policy,$instance,$purpose,$expires,$digest,'Prepared',$now)
            """, ("$c", CustomerId), ("$id", id), ("$p", draft.ProjectId), ("$e", draft.EnvironmentId), ("$op", operation.ToString()),
            ("$actor", requester.ActorKey), ("$version", requester.RoleVersion), ("$target", canonical), ("$targetsha", targetSha),
            ("$conn", connection.Id), ("$cg", connection.Generation), ("$cs", draft.ConnectionSha256), ("$policy", draft.PolicyVersion),
            ("$instance", draft.DatabaseInstanceId), ("$purpose", draft.PurposeSha256), ("$expires", draft.ExpiresAt.ToUnixTimeMilliseconds()), ("$digest", draft.Digest), ("$now", Now));
        Audit(db, tx, requester.ActorKey, draft.ProjectId, draft.EnvironmentId, "CredentialMaintenancePrepared", id, "Prepared", "ExactReviewRequired", draft.Digest);
        tx.Commit(); return draft;
    }

    public FoundationCredentialMaintenanceIntent ApproveCredentialMaintenance(CustomerContext approver, string intentId, string expectedDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContext(db, tx, approver, FoundationCapabilities.CredentialMaintenanceApprove, ["Approver"]);
        var intent = ReadMaintenance(db, tx, intentId); MaintenanceScope(approver, intent); MatchDigest(intent.Digest, expectedDigest);
        if (intent.State != "Prepared" || intent.ApproverKey is not null || intent.Execution is not null) throw new FoundationStoreException("MaintenanceApprovalAlreadyRecorded");
        if (approver.ActorKey == intent.RequesterKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        ValidateMaintenanceAuthority(db, tx, intent);
        Exec(db, tx, "INSERT INTO credential_maintenance_approvals VALUES($c,$id,$actor,$version,$digest,$now)",
            ("$c", CustomerId), ("$id", intent.Id), ("$actor", approver.ActorKey), ("$version", approver.RoleVersion), ("$digest", intent.Digest), ("$now", Now));
        Exec(db, tx, "UPDATE credential_maintenance_intents SET state='Approved' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", intent.Id));
        Audit(db, tx, approver.ActorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceApproved", intent.Id, "Approved", "DistinctApproverExactDigest", intent.Digest);
        var result = ReadMaintenance(db, tx, intent.Id); tx.Commit(); return result;
    }

    public FoundationCredentialMaintenanceIntent GetCredentialMaintenance(CustomerContext reader, string intentId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, reader, FoundationCapabilities.CredentialMaintenanceRead, ["Operator", "Approver", "Auditor"]);
        var intent = ReadMaintenance(db, tx, intentId); MaintenanceScope(reader, intent); tx.Commit(); return intent;
    }
    internal FoundationCredentialMaintenanceIntent GetCredentialMaintenanceForExecution(CustomerContext executor, string intentId)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, executor, FoundationCapabilities.CredentialMaintenanceExecute, ["Operator"]);
        var intent = ReadMaintenance(db, tx, intentId); MaintenanceScope(executor, intent); RequireMaintenanceApproved(db, tx, intent, executor);
        tx.Commit(); return intent;
    }

    internal CredentialMaintenanceAdmission AdmitCredentialMaintenance(CustomerContext executor, string intentId, string expectedDigest,
        FoundationCredentialMaintenanceTarget actualTarget, string expectedAfterSha256)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        FoundationCredentialMaintenanceIntent? intent = null;
        try
        {
            AssertActivation(db, tx);
            MaintenanceContext(db, tx, executor, FoundationCapabilities.CredentialMaintenanceExecute, ["Operator"]);
            intent = ReadMaintenance(db, tx, intentId); MaintenanceScope(executor, intent); MatchDigest(intent.Digest, expectedDigest);
            RequireMaintenanceApproved(db, tx, intent, executor); ValidateMaintenanceTarget(actualTarget, CustomerId, CellId);
            MatchDigest(intent.TargetSha256, Hash(MaintenanceTargetJson(actualTarget))); RequireMaintenanceHash(expectedAfterSha256);
            var claim = new CredentialMaintenanceAdmission(intent, NewId(), executor.ActorKey, executor.RoleVersion, intent.DatabaseInstanceId, expectedAfterSha256);
            Exec(db, tx, """
                INSERT INTO credential_maintenance_executions VALUES($c,$id,$claim,$actor,$version,$instance,$after,'OutcomeUnknown','AdmittedBeforeLocalEffect',NULL,NULL,$now,NULL,$now)
                """, ("$c", CustomerId), ("$id", intent.Id), ("$claim", claim.AdmissionId), ("$actor", executor.ActorKey), ("$version", executor.RoleVersion),
                ("$instance", intent.DatabaseInstanceId), ("$after", expectedAfterSha256), ("$now", Now));
            Audit(db, tx, executor.ActorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceAdmitted", intent.Id,
                "OutcomeUnknown", "OneUseBeforeLocalEffect", intent.Digest);
            tx.Commit(); return claim;
        }
        catch (FoundationStoreException)
        {
            if (intent is not null && executor is not null && executor.CustomerId == CustomerId && executor.CellId == CellId &&
                executor.ProjectId == intent.ProjectId && executor.EnvironmentId == intent.EnvironmentId)
            { Audit(db, tx, executor.ActorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceDenied", intent.Id, "Denied", "CurrentAuthorityRejected", intent.Digest); tx.Commit(); }
            throw;
        }
    }

    internal FoundationCredentialMaintenanceIntent CompleteCredentialMaintenance(CredentialMaintenanceAdmission admission, FoundationCredentialMaintenanceResult result)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var intent = RequireMaintenanceAdmission(db, tx, admission);
        var json = MaintenanceResultJson(intent, result, admission.ExpectedAfterSha256);
        Exec(db, tx, """
            UPDATE credential_maintenance_executions SET state='Succeeded',reason='ExactLocalEffectRecorded',result_json=$json,result_sha256=$sha,completed=$now,updated=$now
            WHERE customer_id=$c AND intent_id=$id AND state='OutcomeUnknown'
            """, ("$json", json), ("$sha", Hash(json)), ("$now", Now), ("$c", CustomerId), ("$id", intent.Id));
        Audit(db, tx, admission.ExecutorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceCompleted", intent.Id, "Succeeded", "ExactLocalEffectRecorded", Hash(json));
        var completed = ReadMaintenance(db, tx, intent.Id); tx.Commit(); return completed;
    }
    static string MaintenanceResultJson(FoundationCredentialMaintenanceIntent intent, FoundationCredentialMaintenanceResult result, string expectedAfterSha256)
    {
        var before = intent.Target;
        if (result is null || result.Operation != intent.Operation || result.TargetSha256 != intent.TargetSha256 || result.BeforeGeneration != before.ProfileGeneration ||
            result.AfterGeneration != before.ProfileGeneration + 1 || result.BeforeEnvelopeSha256 != before.EnvelopeSha256 ||
            result.AfterEnvelopeSha256 != expectedAfterSha256 || result.NamespaceCommitmentSha256 != before.NamespaceCommitmentSha256 ||
            (before.AuthorityNamespaceId is null ? result.AuthorityRevision != 0 || result.AuthorityStateSha256 is not null :
                result.AuthorityRevision != before.AuthorityRevision + 2 || !MaintenanceHash(result.AuthorityStateSha256)))
            throw new FoundationStoreException("MaintenanceResultMismatch");
        RequireMaintenanceHash(result.AfterEnvelopeSha256); var json = CanonicalJson(JsonSerializer.Serialize(result, Json));
        if (Encoding.UTF8.GetByteCount(json) > 8192) throw new FoundationStoreException("MaintenanceResultTooLarge", 400);
        return json;
    }
    internal FoundationCredentialMaintenanceIntent RecordCredentialMaintenanceUnknown(CredentialMaintenanceAdmission admission, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var intent = RequireMaintenanceAdmission(db, tx, admission, permitSucceeded: true);
        if (intent.Execution!.State == "Succeeded") { tx.Commit(); return intent; }
        var reason = MaintenanceReason(reasonCode);
        Exec(db, tx, "UPDATE credential_maintenance_executions SET reason=$reason,updated=$now WHERE customer_id=$c AND intent_id=$id AND state='OutcomeUnknown'",
            ("$reason", reason), ("$now", Now), ("$c", CustomerId), ("$id", intent.Id));
        Audit(db, tx, admission.ExecutorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceUnknown", intent.Id, "OutcomeUnknown", reason, intent.Digest);
        var result = ReadMaintenance(db, tx, intent.Id); tx.Commit(); return result;
    }
    internal void RecordCredentialMaintenanceDenied(CustomerContext executor, string intentId, string expectedDigest, string reasonCode)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        MaintenanceContextIdentity(executor, FoundationCapabilities.CredentialMaintenanceExecute);
        var intent = ReadMaintenance(db, tx, intentId); MaintenanceScope(executor, intent); MatchDigest(intent.Digest, expectedDigest);
        Audit(db, tx, executor.ActorKey, intent.ProjectId, intent.EnvironmentId, "CredentialMaintenanceDenied", intent.Id, "Denied", MaintenanceReason(reasonCode), intent.Digest); tx.Commit();
    }

    void RequireMaintenanceApproved(SqliteConnection db, SqliteTransaction tx, FoundationCredentialMaintenanceIntent intent, CustomerContext executor)
    {
        if (intent.State != "Approved" || intent.ApproverKey is null) throw new FoundationStoreException("MaintenanceApprovalRequired", 403);
        if (intent.Execution is not null) throw new FoundationStoreException("MaintenanceAlreadyConsumed");
        if (intent.ApproverKey == executor.ActorKey) throw new FoundationStoreException("ApprovalSeparationRequired", 403);
        ValidateMaintenanceAuthority(db, tx, intent);
    }
    void ValidateMaintenanceAuthority(SqliteConnection db, SqliteTransaction tx, FoundationCredentialMaintenanceIntent intent)
    {
        MatchDigest(MaintenanceDigest(intent), intent.Digest); MatchDigest(Hash(MaintenanceTargetJson(intent.Target)), intent.TargetSha256);
        if (intent.State == "Quarantined") throw new FoundationStoreException("MaintenanceQuarantined");
        if (intent.ExpiresAt <= clock.GetUtcNow()) throw new FoundationStoreException("MaintenanceIntentExpired");
        if (ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1") != intent.DatabaseInstanceId) throw new FoundationStoreException("MaintenanceInstanceChanged");
        if (Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") != intent.PolicyVersion) throw new FoundationStoreException("PolicyChanged");
        MaintenanceLifecycle(db, tx, intent.Operation);
        ValidateMaintenanceStandingGrant(db, tx, intent.RequesterKey, intent.ProjectId, intent.EnvironmentId);
        if (Require(db, tx, intent.RequesterKey, intent.ProjectId, intent.EnvironmentId, ["Operator"], requireActive: false) != intent.RequesterVersion)
            throw new FoundationStoreException("RequesterGrantChanged", 403);
        var scope = ValidateMaintenanceTarget(intent.Target, CustomerId, CellId); var connection = MaintenanceConnection(db, tx, scope, intent.Target, intent.Operation);
        if (connection.Generation != intent.ConnectionGeneration || Hash(CanonicalJson(connection.ConfigurationJson)) != intent.ConnectionSha256) throw new FoundationStoreException("ConnectionChanged");
        if (intent.ApproverKey is not null) ValidateMaintenanceStandingGrant(db, tx, intent.ApproverKey, intent.ProjectId, intent.EnvironmentId);
        if (intent.ApproverKey is not null && (intent.ApproverKey == intent.RequesterKey ||
            Require(db, tx, intent.ApproverKey, intent.ProjectId, intent.EnvironmentId, ["Approver"], requireActive: false) != intent.ApproverVersion))
            throw new FoundationStoreException("ApproverGrantChanged", 403);
    }
    FoundationCredentialMaintenanceIntent RequireMaintenanceAdmission(SqliteConnection db, SqliteTransaction tx, CredentialMaintenanceAdmission admission, bool permitSucceeded = false)
    {
        if (admission is null || admission.Intent.CustomerId != CustomerId || admission.Intent.CellId != CellId) throw new FoundationStoreException("MaintenanceAdmissionMismatch", 403);
        var intent = ReadMaintenance(db, tx, admission.Intent.Id); var e = intent.Execution;
        if (intent.Digest != admission.Intent.Digest || intent.State == "Quarantined" || e is null || e.AdmissionId != admission.AdmissionId ||
            e.ExecutorKey != admission.ExecutorKey || e.ExecutorVersion != admission.ExecutorVersion || e.ExpectedAfterSha256 != admission.ExpectedAfterSha256 ||
            intent.DatabaseInstanceId != admission.DatabaseInstanceId || Number(db, tx, "SELECT count(*) FROM credential_maintenance_executions WHERE customer_id=$c AND intent_id=$id AND database_instance_id=$instance",
                ("$c", CustomerId), ("$id", intent.Id), ("$instance", admission.DatabaseInstanceId)) != 1 || ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1") != admission.DatabaseInstanceId ||
            e.State != "OutcomeUnknown" && !(permitSucceeded && e.State == "Succeeded")) throw new FoundationStoreException("MaintenanceAdmissionMismatch", 403);
        return intent;
    }
    void MaintenanceContext(SqliteConnection db, SqliteTransaction tx, CustomerContext context, string capability, string[] roles)
    {
        MaintenanceContextIdentity(context, capability);
        if (context.ExpiresUtc <= clock.GetUtcNow()) throw new FoundationStoreException("MaintenanceContextExpired", 403);
        ValidateMaintenanceStandingGrant(db, tx, context.ActorKey, context.ProjectId, context.EnvironmentId);
        if (Require(db, tx, context.ActorKey, context.ProjectId, context.EnvironmentId, roles, requireActive: false) != context.RoleVersion)
            throw new FoundationStoreException("MaintenanceContextGrantChanged", 403);
    }
    static string[] CheckedMaintenanceRoles(string json, bool skipSupportOnly = false)
    {
        string[]? roles;
        try { roles = JsonSerializer.Deserialize<string[]>(json, Json); }
        catch (JsonException) { throw new FoundationStoreException("MaintenanceMembershipInvalid", 403); }
        if (skipSupportOnly && roles is ["SupportDelegate"]) return [];
        if (roles is null || roles.Length > KnownRoles.Count || roles.Any(role => role is null || !KnownRoles.Contains(role) || role == "SupportDelegate") ||
            roles.Distinct(StringComparer.Ordinal).Count() != roles.Length) throw new FoundationStoreException("MaintenanceMembershipInvalid", 403);
        return roles;
    }
    void ValidateMaintenanceStandingGrant(SqliteConnection db, SqliteTransaction tx, string actor, string project, string environment)
    {
        foreach (var roles in Rows(db, tx, "SELECT roles FROM memberships WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e",
            r => r.GetString(0), ("$c", CustomerId), ("$a", actor), ("$p", project), ("$e", environment))) _ = CheckedMaintenanceRoles(roles);
    }
    void MaintenanceContextIdentity(CustomerContext context, string capability)
    {
        if (context is null || context.CustomerId != CustomerId || context.CellId != CellId || context.Kind != PrincipalKind.User || context.Capability != capability ||
            context.SupportApprovalId is not null) throw new FoundationStoreException("MaintenanceContextDenied", 403);
        RequireHuman(context.ActorKey); ValidateApplicationScopeId(context.ProjectId); ValidateApplicationScopeId(context.EnvironmentId);
    }
    static void MaintenanceScope(CustomerContext c, FoundationCredentialMaintenanceIntent i) => MaintenanceScope(c, i.CustomerId, i.CellId, i.ProjectId, i.EnvironmentId);
    static void MaintenanceScope(CustomerContext c, CredentialScope s) => MaintenanceScope(c, s.CustomerId, s.CellId, s.ProjectId, s.EnvironmentId);
    static void MaintenanceScope(CustomerContext c, string customer, string cell, string project, string environment)
    { if (c.CustomerId != customer || c.CellId != cell || c.ProjectId != project || c.EnvironmentId != environment) throw new FoundationStoreException("MaintenanceScopeMismatch", 403); }
    void MaintenanceLifecycle(SqliteConnection db, SqliteTransaction tx, CredentialMaintenanceOperation operation)
    {
        var lifecycle = ReadLifecycle(db, tx);
        if (!Enum.IsDefined(operation) || (operation == CredentialMaintenanceOperation.Rewrap ? lifecycle.State != "Active" || !lifecycle.Enabled :
            lifecycle.State is not ("Active" or "Suspended" or "Offboarding") || (lifecycle.State == "Active") != lifecycle.Enabled))
            throw new FoundationStoreException("MaintenanceLifecycleDenied", 403);
    }
    static CredentialScope ValidateMaintenanceTarget(FoundationCredentialMaintenanceTarget target, string customerId, string cellId, bool readOnlyObservation = false)
    {
        if (target is null) throw new FoundationStoreException("InvalidMaintenanceTarget", 400);
        _ = MaintenanceTargetJson(target);
        CredentialScope scope;
        try
        {
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(target.ProfileScopeJson));
            scope = CredentialRules.Snapshot(doc.RootElement.Deserialize<CredentialScope>(CredentialRules.Json)!);
        }
        catch (Exception e) when (e is JsonException or CredentialException or ArgumentException or NullReferenceException)
        { throw new FoundationStoreException("InvalidMaintenanceTarget", 400); }
        var canonicalScope = JsonSerializer.Serialize(scope, CredentialRules.Json);
        if (target.ProfileScopeJson != canonicalScope || Hash(canonicalScope) != target.ProfileScopeSha256 || Hash(scope.AuthProfileId) != target.ProfileIdSha256 ||
            scope.CustomerId != customerId || scope.CellId != cellId || !ValidDatabaseInstanceId(target.VaultNamespaceId) || !MaintenanceHash(target.NamespaceCommitmentSha256) ||
            target.ProfileGeneration <= 0 || target.ProfileGeneration > MaintenanceCounterLimit - (readOnlyObservation ? 0 : 1) || !MaintenanceHash(target.EnvelopeSha256) || target.EnvelopeLength is < 1 or > 131072 ||
            !CredentialRules.ProfileId(target.SourceKeyId) || !CredentialRules.ProfileId(target.DestinationKeyId) || target.ExpiresUnixMilliseconds < 0 ||
            target.ExpiresUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            (target.AuthorityNamespaceId is null ? target.AuthorityBindingSha256 is not null || target.AuthorityRevision != 0 || target.AuthorityStateSha256 is not null :
                !ValidDatabaseInstanceId(target.AuthorityNamespaceId) || !MaintenanceHash(target.AuthorityBindingSha256) || !MaintenanceHash(target.AuthorityStateSha256) || target.AuthorityRevision < 0 || target.AuthorityRevision > MaintenanceCounterLimit - (readOnlyObservation ? 0 : 2)))
            throw new FoundationStoreException("InvalidMaintenanceTarget", 400);
        return scope;
    }
    FoundationConnection MaintenanceConnection(SqliteConnection db, SqliteTransaction tx, CredentialScope scope, FoundationCredentialMaintenanceTarget target, CredentialMaintenanceOperation operation)
    {
        var connection = FindConnection(db, tx, scope.ConnectionId) ?? throw new FoundationStoreException("NotFound", 404);
        if (connection.ProjectId != scope.ProjectId || connection.EnvironmentId != scope.EnvironmentId || connection.Provider != scope.Provider) throw new FoundationStoreException("ConnectionScopeMismatch", 403);
        using var doc = JsonDocument.Parse(CanonicalJson(connection.ConfigurationJson)); var c = doc.RootElement;
        if (!c.TryGetProperty("authProfileId", out var profile) || profile.ValueKind != JsonValueKind.String || Hash(profile.GetString()!) != target.ProfileIdSha256)
            throw new FoundationStoreException("MaintenanceProfileReferenceMismatch", 403);
        if (operation == CredentialMaintenanceOperation.Rewrap)
        {
            if (!connection.Enabled || connection.Generation != scope.ConnectionGeneration || target.SourceKeyId == target.DestinationKeyId) throw new FoundationStoreException("MaintenanceConnectionChanged");
            string Text(string name) => c.GetProperty(name).GetString()!;
            string[] List(string name) => c.GetProperty(name).EnumerateArray().Select(p => p.GetString()!).ToArray();
            var typed = new PipelineConnection(CustomerId, CellId, connection.Id, connection.Provider, Text("organization"),
                c.GetProperty("project").ValueKind == JsonValueKind.Null ? null : Text("project"), Text("repository"), Text("pipelineOrWorkflowId"),
                List("allowedRefs"), List("allowedEnvironments"), List("allowedInputKeys"), Text("authProfileId"), connection.Generation)
            { EnforcesSourceRevision = c.TryGetProperty("enforcesSourceRevision", out var guard) && guard.GetBoolean(),
                PipelineVersion = c.TryGetProperty("pipelineVersion", out var version) && version.ValueKind != JsonValueKind.Null ? version.GetInt32() : null };
            if (PipelineRequestFactory.ConnectionDigest(typed) != scope.ConnectionDigest) throw new FoundationStoreException("MaintenanceConnectionChanged");
        }
        return connection;
    }
    static string MaintenanceTargetJson(FoundationCredentialMaintenanceTarget target)
    {
        var json = CanonicalJson(JsonSerializer.Serialize(target, Json));
        if (Encoding.UTF8.GetByteCount(json) > 16384) throw new FoundationStoreException("InvalidMaintenanceTarget", 400);
        return json;
    }
    static string MaintenanceDigest(FoundationCredentialMaintenanceIntent i) => Hash(JsonSerializer.Serialize(new
    { schema = "platform-studio.credential-maintenance.v1", i.Id, i.CustomerId, i.CellId, i.ProjectId, i.EnvironmentId, i.Operation,
        i.TargetSha256, i.RequesterKey, i.RequesterVersion, i.ConnectionId, i.ConnectionGeneration, i.ConnectionSha256,
        i.PolicyVersion, i.DatabaseInstanceId, i.PurposeSha256, expires = i.ExpiresAt.ToUnixTimeMilliseconds() }, Json));
    static bool MaintenanceHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static void RequireMaintenanceHash(string value) { if (!MaintenanceHash(value)) throw new FoundationStoreException("InvalidMaintenanceDigest", 400); }
    static string MaintenanceReason(string value) => value switch
    { "PreparationRejected" or "VaultCommitInterrupted" or "CompletionAuditUnavailable" or "CandidateMismatch" or "CallerCancelled" => value, _ => "MaintenanceExecutionInterrupted" };

    FoundationCredentialMaintenanceIntent ReadMaintenance(SqliteConnection db, SqliteTransaction tx, string id)
    {
        ValidateId(id); string? targetJson = null, resultJson = null, executionInstance = null;
        var intent = Rows(db, tx, """
            SELECT id,project_id,environment_id,operation,requester,requester_version,target,target_sha256,connection_id,connection_generation,
              connection_sha256,policy_version,database_instance_id,purpose_sha256,expires,digest,state FROM credential_maintenance_intents WHERE customer_id=$c AND id=$id
            """, r => { targetJson = r.GetString(6); return new FoundationCredentialMaintenanceIntent(r.GetString(0), CustomerId, CellId, r.GetString(1), r.GetString(2),
                Enum.Parse<CredentialMaintenanceOperation>(r.GetString(3)), DeserializeMaintenance<FoundationCredentialMaintenanceTarget>(targetJson, 16384),
                r.GetString(7), r.GetString(4), r.GetInt64(5), r.GetString(8), r.GetInt64(9), r.GetString(10), r.GetInt64(11), r.GetString(12), r.GetString(13),
                At(r.GetInt64(14)), r.GetString(15), r.GetString(16), null, null, null); }, ("$c", CustomerId), ("$id", id)).SingleOrDefault()
            ?? throw new FoundationStoreException("NotFound", 404);
        var approvals = Rows(db, tx, "SELECT actor_key,actor_version,digest FROM credential_maintenance_approvals WHERE customer_id=$c AND intent_id=$id",
            r => (Actor: r.GetString(0), Version: r.GetInt64(1), Digest: r.GetString(2)), ("$c", CustomerId), ("$id", id));
        if (approvals.Count > 1 || approvals.Count == 1 && approvals[0].Digest != intent.Digest) throw new FoundationStoreException("MaintenanceApprovalBindingChanged");
        var execution = Rows(db, tx, """
            SELECT admission_id,executor_key,executor_version,state,reason,expected_after_sha256,result_json,result_sha256,admitted,completed,database_instance_id
            FROM credential_maintenance_executions WHERE customer_id=$c AND intent_id=$id
            """, r => { resultJson = r.IsDBNull(6) ? null : r.GetString(6); executionInstance = r.GetString(10);
                return new FoundationCredentialMaintenanceExecution(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                resultJson is null ? null : DeserializeMaintenance<FoundationCredentialMaintenanceResult>(resultJson, 8192), r.IsDBNull(7) ? null : r.GetString(7),
                At(r.GetInt64(8)), r.IsDBNull(9) ? null : At(r.GetInt64(9))); }, ("$c", CustomerId), ("$id", id)).SingleOrDefault();
        var result = intent with { ApproverKey = approvals.Count == 0 ? null : approvals[0].Actor, ApproverVersion = approvals.Count == 0 ? null : approvals[0].Version, Execution = execution };
        ValidateMaintenanceReadIntegrity(result, targetJson!, resultJson, executionInstance); return result;
    }

    static void ValidateMaintenanceReadIntegrity(FoundationCredentialMaintenanceIntent intent, string targetJson, string? resultJson, string? executionInstance)
    {
        var scope = ValidateMaintenanceTarget(intent.Target, intent.CustomerId, intent.CellId);
        if (!Enum.IsDefined(intent.Operation) || intent.RequesterVersion <= 0 || intent.PolicyVersion <= 0 || intent.ConnectionGeneration <= 0 ||
            !ValidDatabaseInstanceId(intent.DatabaseInstanceId) || !MaintenanceHash(intent.ConnectionSha256) || !MaintenanceHash(intent.PurposeSha256) ||
            intent.ProjectId != scope.ProjectId || intent.EnvironmentId != scope.EnvironmentId || intent.ConnectionId != scope.ConnectionId ||
            CanonicalJson(targetJson) != MaintenanceTargetJson(intent.Target) || Hash(MaintenanceTargetJson(intent.Target)) != intent.TargetSha256 ||
            MaintenanceDigest(intent) != intent.Digest || intent.State is not ("Prepared" or "Approved" or "Quarantined") ||
            intent.Operation == CredentialMaintenanceOperation.Rewrap && intent.Target.SourceKeyId == intent.Target.DestinationKeyId)
            throw new FoundationStoreException("MaintenanceIntegrityFailed");
        RequireHuman(intent.RequesterKey);
        if (intent.ApproverKey is null ? intent.ApproverVersion is not null || intent.State == "Approved" || intent.Execution is not null :
            intent.ApproverVersion is null or <= 0 || intent.ApproverKey == intent.RequesterKey || intent.State == "Prepared")
            throw new FoundationStoreException("MaintenanceIntegrityFailed");
        if (intent.ApproverKey is not null) RequireHuman(intent.ApproverKey);
        if (intent.Execution is not { } e) return;
        RequireHuman(e.ExecutorKey);
        if (!ValidDatabaseInstanceId(e.AdmissionId) || e.ExecutorVersion <= 0 || e.ExecutorKey == intent.ApproverKey ||
            executionInstance != intent.DatabaseInstanceId || !MaintenanceHash(e.ExpectedAfterSha256) ||
            e.State is not ("OutcomeUnknown" or "Succeeded") || e.AdmittedAt >= intent.ExpiresAt)
            throw new FoundationStoreException("MaintenanceIntegrityFailed");
        if (e.State == "OutcomeUnknown")
        {
            if (e.Result is not null || e.ResultSha256 is not null || e.CompletedAt is not null || resultJson is not null) throw new FoundationStoreException("MaintenanceIntegrityFailed");
        }
        else
        {
            if (intent.State != "Approved" || e.Result is null || e.CompletedAt is null || e.CompletedAt < e.AdmittedAt || resultJson is null)
                throw new FoundationStoreException("MaintenanceIntegrityFailed");
            var canonical = MaintenanceResultJson(intent, e.Result, e.ExpectedAfterSha256);
            if (CanonicalJson(resultJson) != canonical || Hash(canonical) != e.ResultSha256) throw new FoundationStoreException("MaintenanceIntegrityFailed");
        }
    }
    static T DeserializeMaintenance<T>(string json, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(json) > maximum) throw new FoundationStoreException("MaintenanceIntegrityFailed");
        _ = CanonicalJson(json);
        try { using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(json)); return document.RootElement.Deserialize<T>(CredentialRules.Json) ?? throw new JsonException(); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new FoundationStoreException("MaintenanceIntegrityFailed"); }
    }
}
