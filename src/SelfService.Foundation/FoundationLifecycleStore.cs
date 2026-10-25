using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationAdministrativePlan(string Id, string CustomerId, string CellId, string Kind,
    string ProjectId, string EnvironmentId, string RequesterKey, long RequesterVersion, long PolicyVersion,
    string PayloadJson, string Digest, DateTimeOffset ExpiresAt, string State, string? ApproverKey, long? ApproverVersion);
public sealed record FoundationCustomerLifecycle(string CustomerId, string CellId, string State, long Version, bool Enabled);
public sealed record FoundationProjectScope(string CustomerId, string ProjectId, string EnvironmentId, DateTimeOffset CreatedAt);
public sealed record FoundationScopeExport(string Id, string CustomerId, string CellId, string ProjectId,
    string EnvironmentId, string ManifestJson, string Sha256, DateTimeOffset CreatedAt);

public sealed partial class FoundationStore
{
    sealed record ScopeOnboardingPayload(string ProjectId, string EnvironmentId, string InitialAdministratorActor, string Purpose,
        string? InitialApproverActor = null);
    sealed record LifecycleChangePayload(string Action, string ExpectedState, long ExpectedLifecycleVersion, string Purpose);

    public IReadOnlyList<FoundationMembership> GetAdministrativeMemberships(string customerId, string actorKey)
    {
        // Host uses this source only for user-only lifecycle capabilities, never ordinary data/dispatch requests.
        Scope(customerId); ValidateId(actorKey); RequireHuman(actorKey); using var db = Open();
        return Rows(db, null, """
            SELECT m.project_id,m.environment_id,m.roles,a.version,m.enabled FROM memberships m
            JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key WHERE m.customer_id=$c AND m.actor_key=$a
            ORDER BY m.project_id,m.environment_id
            """, r => new FoundationMembership(CustomerId, CellId, actorKey, r.GetString(0), r.GetString(1),
                JsonSerializer.Deserialize<string[]>(r.GetString(2))!.Where(role => role is "CustomerAdmin" or "Approver" or "Auditor").ToArray(),
                r.GetInt64(3), r.GetInt64(4) == 1), ("$c", CustomerId), ("$a", actorKey))
            .Where(m => m.Roles.Length > 0).ToArray();
    }

    public FoundationCustomerLifecycle CustomerLifecycle(string customerId, string actorKey)
    {
        Scope(customerId); RequireHuman(actorKey); using var db = Open(); var scope = BootstrapScope(db, null);
        Require(db, null, actorKey, scope.Project, scope.Environment, ["CustomerAdmin", "Approver", "Auditor"], requireActive: false);
        return ReadLifecycle(db, null);
    }

    public IReadOnlyList<FoundationProjectScope> ListProjectScopes(string customerId, string actorKey)
    {
        Scope(customerId); RequireHuman(actorKey); using var db = Open(); var scope = BootstrapScope(db, null);
        Require(db, null, actorKey, scope.Project, scope.Environment, ["CustomerAdmin", "Approver", "Auditor"], requireActive: false);
        var result = Rows(db, null, """
            SELECT project_id,environment_id,created FROM scopes WHERE customer_id=$c
            UNION SELECT m.project_id,m.environment_id,0 FROM memberships m WHERE m.customer_id=$c
              AND NOT EXISTS(SELECT 1 FROM scopes s WHERE s.customer_id=m.customer_id AND s.project_id=m.project_id AND s.environment_id=m.environment_id)
            ORDER BY project_id,environment_id LIMIT 1001
            """, r => new FoundationProjectScope(CustomerId, r.GetString(0), r.GetString(1), At(r.GetInt64(2))), ("$c", CustomerId));
        if (result.Count > 1000) throw new FoundationStoreException("ScopePageRequired", 413);
        return result;
    }

    public FoundationAdministrativePlan PrepareScopeOnboarding(string customerId, string requesterKey, string projectId,
        string environmentId, string initialAdministratorActor, string purpose, string? initialApproverActor = null)
    {
        Scope(customerId); RequireHuman(requesterKey); RequireHuman(initialAdministratorActor);
        ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose);
        if (requesterKey == initialAdministratorActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        if (initialApproverActor is not null)
        {
            RequireHuman(initialApproverActor);
            if (initialApproverActor == initialAdministratorActor || initialApproverActor == requesterKey)
                throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        }
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var authority = BootstrapScope(db, tx);
        Require(db, tx, requesterKey, authority.Project, authority.Environment, "CustomerAdmin");
        if (ScopeExists(db, tx, projectId, environmentId)) throw new FoundationStoreException("ScopeAlreadyExists");
        var result = CreateAdministrativePlan(db, tx, requesterKey, authority.Project, authority.Environment, "scope.onboard",
            JsonSerializer.Serialize(new ScopeOnboardingPayload(projectId, environmentId, initialAdministratorActor, purpose, initialApproverActor), Json));
        tx.Commit(); return result;
    }

    public FoundationAdministrativePlan PrepareLifecycleChange(string customerId, string requesterKey, string action, string purpose)
    {
        Scope(customerId); RequireHuman(requesterKey); ValidatePurpose(purpose);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); var authority = BootstrapScope(db, tx);
        Require(db, tx, requesterKey, authority.Project, authority.Environment, ["CustomerAdmin"], requireActive: false);
        var lifecycle = ReadLifecycle(db, tx); _ = NextLifecycleState(lifecycle.State, action);
        var result = CreateAdministrativePlan(db, tx, requesterKey, authority.Project, authority.Environment, "cell.lifecycle",
            JsonSerializer.Serialize(new LifecycleChangePayload(action, lifecycle.State, lifecycle.Version, purpose), Json));
        tx.Commit(); return result;
    }

    public FoundationAdministrativePlan GetAdministrativePlan(string customerId, string actorKey, string id,
        string projectId, string environmentId)
    {
        Scope(customerId); ValidateId(id); RequireHuman(actorKey); using var db = Open(); var plan = ReadAdministrativePlan(db, null, id);
        ReadScope(projectId, environmentId, plan.ProjectId, plan.EnvironmentId);
        Require(db, null, actorKey, plan.ProjectId, plan.EnvironmentId, ["CustomerAdmin", "Approver", "Auditor"], requireActive: false);
        return plan;
    }

    public FoundationAdministrativePlan ApproveAdministrativePlan(string customerId, string approverKey, string id, string expectedDigest)
    {
        Scope(customerId); ValidateId(id); RequireHuman(approverKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = ReadAdministrativePlan(db, tx, id); MatchDigest(plan.Digest, expectedDigest); ValidateAdministrativeAuthority(db, tx, plan);
        if (plan.State != "Prepared") throw new FoundationStoreException("AdministrativePlanNotPrepared");
        var onboard = plan.Kind == "scope.onboard" ? JsonSerializer.Deserialize<ScopeOnboardingPayload>(plan.PayloadJson, Json) : null;
        if (plan.RequesterKey == approverKey || onboard is not null &&
            (onboard.InitialAdministratorActor == approverKey || onboard.InitialApproverActor == approverKey) || AccessPlanTarget(plan) == approverKey)
            throw new FoundationStoreException("SelfApprovalDenied", 403);
        var version = Require(db, tx, approverKey, plan.ProjectId, plan.EnvironmentId, ["Approver"], requireActive: !AdministrativeWhileInactive(plan.Kind));
        Exec(db, tx, "INSERT INTO administrative_approvals VALUES($c,$id,$a,$v,$digest,$now)",
            ("$c", CustomerId), ("$id", id), ("$a", approverKey), ("$v", version), ("$digest", plan.Digest), ("$now", Now));
        Exec(db, tx, "UPDATE administrative_plans SET state='Approved',approver=$a,approver_version=$v WHERE customer_id=$c AND id=$id",
            ("$a", approverKey), ("$v", version), ("$c", CustomerId), ("$id", id));
        Audit(db, tx, approverKey, plan.ProjectId, plan.EnvironmentId, "AdministrativePlanApproved", id, "Succeeded", "ExactDigestApproved", id);
        var result = ReadAdministrativePlan(db, tx, id); tx.Commit(); return result;
    }

    public FoundationProjectScope ExecuteScopeOnboarding(string customerId, string executorKey, string id, string expectedDigest)
    {
        Scope(customerId); RequireHuman(executorKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, "scope.onboard");
        var payload = JsonSerializer.Deserialize<ScopeOnboardingPayload>(plan.PayloadJson, Json)!;
        if (executorKey == payload.InitialAdministratorActor || executorKey == payload.InitialApproverActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        if (ScopeExists(db, tx, payload.ProjectId, payload.EnvironmentId)) throw new FoundationStoreException("ScopeAlreadyExists");
        Exec(db, tx, "INSERT INTO scopes VALUES($c,$p,$e,$now)", ("$c", CustomerId), ("$p", payload.ProjectId), ("$e", payload.EnvironmentId), ("$now", Now));
        Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1) ON CONFLICT(customer_id,actor_key) DO UPDATE SET version=version+1",
            ("$c", CustomerId), ("$a", payload.InitialAdministratorActor));
        Exec(db, tx, "INSERT INTO memberships VALUES($c,$a,$p,$e,'[\"CustomerAdmin\"]',1)",
            ("$c", CustomerId), ("$a", payload.InitialAdministratorActor), ("$p", payload.ProjectId), ("$e", payload.EnvironmentId));
        if (payload.InitialApproverActor is not null)
        {
            Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1) ON CONFLICT(customer_id,actor_key) DO UPDATE SET version=version+1",
                ("$c", CustomerId), ("$a", payload.InitialApproverActor));
            Exec(db, tx, "INSERT INTO memberships VALUES($c,$a,$p,$e,'[\"Approver\"]',1)",
                ("$c", CustomerId), ("$a", payload.InitialApproverActor), ("$p", payload.ProjectId), ("$e", payload.EnvironmentId));
            Audit(db, tx, executorKey, payload.ProjectId, payload.EnvironmentId, "ScopeReviewerOnboarded", payload.InitialApproverActor,
                "Succeeded", "ReviewedInitialApprover", plan.Id);
        }
        CompleteAdministrativePlan(db, tx, executorKey, plan, "ScopeOnboarded");
        Audit(db, tx, executorKey, payload.ProjectId, payload.EnvironmentId, "ScopeOnboarded", payload.InitialAdministratorActor,
            "Succeeded", "ReviewedInitialAdministrator", plan.Id);
        var result = new FoundationProjectScope(CustomerId, payload.ProjectId, payload.EnvironmentId, At(Now)); tx.Commit(); return result;
    }

    public FoundationCustomerLifecycle ExecuteLifecycleChange(string customerId, string executorKey, string id, string expectedDigest)
    {
        Scope(customerId); RequireHuman(executorKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, "cell.lifecycle");
        var payload = JsonSerializer.Deserialize<LifecycleChangePayload>(plan.PayloadJson, Json)!; var current = ReadLifecycle(db, tx);
        if (current.State != payload.ExpectedState || current.Version != payload.ExpectedLifecycleVersion)
            throw new FoundationStoreException("LifecycleChanged");
        var next = NextLifecycleState(current.State, payload.Action);
        if (next == "Offboarded" && Number(db, tx, "SELECT count(*) FROM jobs WHERE customer_id=$c AND state='OutcomeUnknown'", ("$c", CustomerId)) != 0)
            throw new FoundationStoreException("ReconciliationRequired");
        var enabled = next == "Active";
        Exec(db, tx, "UPDATE customer_lifecycle SET state=$state,version=version+1 WHERE singleton=1", ("$state", next));
        Exec(db, tx, "UPDATE cell SET enabled=$enabled,policy_version=policy_version+1 WHERE singleton=1", ("$enabled", enabled ? 1 : 0));
        var pendingJobs = Rows(db, tx, "SELECT id FROM jobs WHERE customer_id=$c AND state IN ('Queued','Claimed','Dispatched')", r => r.GetString(0), ("$c", CustomerId));
        foreach (var job in pendingJobs.Select(jobId => ReadJob(db, tx, jobId)))
        {
            var state = job.State == "Dispatched" ? "OutcomeUnknown" : "Cancelled";
            Exec(db, tx, """
                UPDATE jobs SET state=$state,worker_id=NULL,lease_until=NULL,fence=fence+1,reason='CustomerLifecycleChanged',updated=$now
                WHERE customer_id=$c AND id=$id
                """, ("$state", state), ("$now", Now), ("$c", CustomerId), ("$id", job.Id));
            Audit(db, tx, executorKey, job.ProjectId, job.EnvironmentId, "LifecycleJobHeld", job.Id, state,
                state == "OutcomeUnknown" ? "ReconciliationRequired" : "CancelledBeforeDispatch", plan.Id);
        }
        if (next is "Offboarding" or "Offboarded")
            Exec(db, tx, "UPDATE connections SET enabled=0,generation=generation+1 WHERE customer_id=$c", ("$c", CustomerId));
        if (next == "Offboarded")
        {
            // Keep reviewed administration/audit available for export and local retention. Normal application
            // requests remain denied by the inactive cell, and all operational roles are removed.
            var grants = Rows(db, tx, "SELECT actor_key,project_id,environment_id,roles,enabled FROM memberships WHERE customer_id=$c",
                r => (Actor: r.GetString(0), Project: r.GetString(1), Environment: r.GetString(2), Roles: JsonSerializer.Deserialize<string[]>(r.GetString(3))!, Enabled: r.GetInt64(4)), ("$c", CustomerId));
            foreach (var grant in grants)
            {
                var retained = grant.Roles.Where(role => role is "CustomerAdmin" or "Approver" or "Auditor").ToArray();
                Exec(db, tx, "UPDATE memberships SET roles=$roles,enabled=$enabled WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e",
                    ("$roles", JsonSerializer.Serialize(retained, Json)), ("$enabled", retained.Length > 0 ? grant.Enabled : 0),
                    ("$c", CustomerId), ("$a", grant.Actor), ("$p", grant.Project), ("$e", grant.Environment));
            }
            Exec(db, tx, "UPDATE actors SET version=version+1 WHERE customer_id=$c", ("$c", CustomerId));
        }
        CompleteAdministrativePlan(db, tx, executorKey, plan, "CustomerLifecycleChanged");
        var result = ReadLifecycle(db, tx); tx.Commit(); return result;
    }

    public FoundationScopeExport ExportScope(string customerId, string auditorKey, string projectId, string environmentId)
    {
        Scope(customerId); RequireHuman(auditorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, auditorKey, projectId, environmentId, ["Auditor"], requireActive: false);
        ValidateAdministrativeAuditChain(db, tx, "AuditIntegrityFailed");
        var artifacts = Rows(db, tx, "SELECT id,digest,length(content),created FROM artifacts WHERE customer_id=$c AND project_id=$p AND environment_id=$e ORDER BY id LIMIT 1001",
            r => new { id = r.GetString(0), sha256 = r.GetString(1), length = r.GetInt64(2), createdUtc = At(r.GetInt64(3)) },
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId));
        var plans = Rows(db, tx, "SELECT id,digest,connection_generation,policy_version,expires FROM plans WHERE customer_id=$c AND project_id=$p AND environment_id=$e ORDER BY id LIMIT 1001",
            r => new { id = r.GetString(0), digest = r.GetString(1), connectionGeneration = r.GetInt64(2), policyVersion = r.GetInt64(3), expiresUtc = At(r.GetInt64(4)) },
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId));
        var audit = Rows(db, tx, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit WHERE customer_id=$c AND project_id=$p AND environment_id=$e ORDER BY sequence LIMIT 1001
            """, AuditRow, ("$c", CustomerId), ("$p", projectId), ("$e", environmentId));
        if (artifacts.Count > 1000 || plans.Count > 1000 || audit.Count > 1000) throw new FoundationStoreException("ExportPageRequired", 413);
        var id = NewId(); var manifest = JsonSerializer.Serialize(new
        {
            schema = "platform-studio.scope-metadata-export.v1", customerId = CustomerId, cellId = CellId, projectId, environmentId,
            containsArtifactContents = false, containsConnectionConfiguration = false, containsPlanPayloads = false,
            containsProviderReceipts = false, auditIntegrityVerified = true, artifacts, plans, audit
        }, Json);
        if (Encoding.UTF8.GetByteCount(manifest) > 1048576) throw new FoundationStoreException("ExportPageRequired", 413);
        if (Number(db, tx, "SELECT coalesce(sum(length(CAST(manifest AS BLOB))),0) FROM scope_exports WHERE customer_id=$c", ("$c", CustomerId)) +
            Encoding.UTF8.GetByteCount(manifest) > MaximumExportStorageBytes)
            throw new FoundationStoreException("ExportStorageCapacityExceeded", 429);
        var digest = Hash(manifest); var now = Now;
        Exec(db, tx, "INSERT INTO scope_exports VALUES($c,$id,$p,$e,$manifest,$digest,$now)", ("$c", CustomerId), ("$id", id),
            ("$p", projectId), ("$e", environmentId), ("$manifest", manifest), ("$digest", digest), ("$now", now));
        Audit(db, tx, auditorKey, projectId, environmentId, "ScopeExported", id, "Succeeded", "MetadataOnlyExport", id);
        tx.Commit(); return new(id, CustomerId, CellId, projectId, environmentId, manifest, digest, At(now));
    }

    public FoundationScopeExport GetScopeExport(string customerId, string auditorKey, string id, string projectId, string environmentId)
    {
        Scope(customerId); ValidateId(id); RequireHuman(auditorKey); using var db = Open();
        Require(db, null, auditorKey, projectId, environmentId, ["Auditor"], requireActive: false);
        // Paged snapshots have creator/cursor/expiry/version commitments; the legacy raw endpoint must not bypass them.
        if (id.StartsWith(PagedExportPrefix, StringComparison.Ordinal)) throw new FoundationStoreException("NotFound", 404);
        var result = Rows(db, null, """
            SELECT manifest,digest,created FROM scope_exports WHERE customer_id=$c AND id=$id AND project_id=$p AND environment_id=$e
            """, r => new FoundationScopeExport(id, CustomerId, CellId, projectId, environmentId, r.GetString(0), r.GetString(1), At(r.GetInt64(2))),
            ("$c", CustomerId), ("$id", id), ("$p", projectId), ("$e", environmentId)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
        MatchDigest(Hash(result.ManifestJson), result.Sha256); return result;
    }

    FoundationAdministrativePlan CreateAdministrativePlan(SqliteConnection db, SqliteTransaction tx, string requester,
        string project, string environment, string kind, string payload)
    {
        var normalized = CanonicalJson(payload); var id = NewId(); var version = ActorVersion(db, tx, requester);
        if (Number(db, tx, "SELECT count(*) FROM administrative_plans WHERE customer_id=$c AND state IN ('Prepared','Approved') AND expires>$now", ("$c", CustomerId), ("$now", Now)) >= 64)
            throw new FoundationStoreException("AdministrativeCapacityExceeded", 429);
        var policy = Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"); var expiry = clock.GetUtcNow().AddMinutes(15);
        var draft = new FoundationAdministrativePlan(id, CustomerId, CellId, kind, project, environment, requester, version, policy,
            normalized, "", At(expiry.ToUnixTimeMilliseconds()), "Prepared", null, null);
        var digest = AdministrativeDigest(draft);
        Exec(db, tx, "INSERT INTO administrative_plans VALUES($c,$id,$kind,$p,$e,$requester,$version,$policy,$payload,$digest,$expires,'Prepared',NULL,NULL,$now)",
            ("$c", CustomerId), ("$id", id), ("$kind", kind), ("$p", project), ("$e", environment), ("$requester", requester),
            ("$version", version), ("$policy", policy), ("$payload", normalized), ("$digest", digest), ("$expires", draft.ExpiresAt.ToUnixTimeMilliseconds()), ("$now", Now));
        Audit(db, tx, requester, project, environment, "AdministrativePlanPrepared", id, "Succeeded", "ExactReviewRequired", id);
        return draft with { Digest = digest };
    }

    FoundationAdministrativePlan ReadAdministrativePlan(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        ValidateId(id);
        return Rows(db, tx, """
            SELECT id,kind,project_id,environment_id,requester,requester_version,policy_version,payload,digest,expires,state,approver,approver_version
            FROM administrative_plans WHERE customer_id=$c AND id=$id
            """, r => new FoundationAdministrativePlan(r.GetString(0), CustomerId, CellId, r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.GetString(8), At(r.GetInt64(9)), r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetInt64(12)), ("$c", CustomerId), ("$id", id)).SingleOrDefault()
            ?? throw new FoundationStoreException("NotFound", 404);
    }

    void ValidateAdministrativeAuthority(SqliteConnection db, SqliteTransaction tx, FoundationAdministrativePlan plan)
    {
        MatchDigest(AdministrativeDigest(plan), plan.Digest);
        if (plan.ExpiresAt.ToUnixTimeMilliseconds() <= Now) throw new FoundationStoreException("AdministrativePlanExpired");
        if (Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") != plan.PolicyVersion) throw new FoundationStoreException("PolicyChanged");
        var active = !AdministrativeWhileInactive(plan.Kind);
        if (Require(db, tx, plan.RequesterKey, plan.ProjectId, plan.EnvironmentId, ["CustomerAdmin"], requireActive: active) != plan.RequesterVersion)
            throw new FoundationStoreException("RequesterGrantChanged", 403);
        ValidateAccessTargetSnapshot(db, tx, plan);
        if (plan.ApproverKey is not null && (plan.ApproverKey == plan.RequesterKey ||
            Require(db, tx, plan.ApproverKey, plan.ProjectId, plan.EnvironmentId, ["Approver"], requireActive: active) != plan.ApproverVersion))
            throw new FoundationStoreException("ApproverGrantChanged", 403);
        if (plan.ApproverKey is not null && Number(db, tx, """
            SELECT count(*) FROM administrative_approvals WHERE customer_id=$c AND plan_id=$id AND actor_key=$a AND actor_version=$v AND digest=$digest
            """, ("$c", CustomerId), ("$id", plan.Id), ("$a", plan.ApproverKey), ("$v", plan.ApproverVersion), ("$digest", plan.Digest)) != 1)
            throw new FoundationStoreException("AdministrativeApprovalBindingChanged");
    }

    FoundationAdministrativePlan RequireAdministrativeExecution(SqliteConnection db, SqliteTransaction tx,
        string executor, string id, string digest, string kind)
    {
        var plan = ReadAdministrativePlan(db, tx, id); MatchDigest(plan.Digest, digest);
        if (plan.Kind != kind || plan.State != "Approved" || plan.ApproverKey is null) throw new FoundationStoreException("AdministrativeApprovalRequired", 403);
        ValidateAdministrativeAuthority(db, tx, plan);
        Require(db, tx, executor, plan.ProjectId, plan.EnvironmentId, ["CustomerAdmin"], requireActive: !AdministrativeWhileInactive(kind));
        return plan;
    }

    void CompleteAdministrativePlan(SqliteConnection db, SqliteTransaction tx, string actor, FoundationAdministrativePlan plan, string action)
    {
        Exec(db, tx, "UPDATE administrative_plans SET state='Executed' WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", plan.Id));
        Audit(db, tx, actor, plan.ProjectId, plan.EnvironmentId, action, plan.Id, "Succeeded", "ReviewedAdministrativeChange", plan.Id);
    }
    static string AdministrativeDigest(FoundationAdministrativePlan plan) => Hash(JsonSerializer.Serialize(new
    { plan.Id, plan.CustomerId, plan.CellId, plan.Kind, plan.ProjectId, plan.EnvironmentId, plan.RequesterKey, plan.RequesterVersion,
        plan.PolicyVersion, payload = CanonicalJson(plan.PayloadJson), expires = plan.ExpiresAt.ToUnixTimeMilliseconds() }, Json));
    (string Project, string Environment) BootstrapScope(SqliteConnection db, SqliteTransaction? tx)
    {
        var scope = Rows(db, tx, "SELECT project_id,environment_id FROM audit WHERE action='CustomerBootstrapped' ORDER BY sequence LIMIT 1",
            r => (r.GetString(0), r.GetString(1))).SingleOrDefault();
        return scope.Item1 is not null ? scope : ("default", "dev");
    }
    bool ScopeExists(SqliteConnection db, SqliteTransaction tx, string project, string environment) =>
        Number(db, tx, "SELECT count(*) FROM scopes WHERE customer_id=$c AND project_id=$p AND environment_id=$e", ("$c", CustomerId), ("$p", project), ("$e", environment)) != 0 ||
        Number(db, tx, "SELECT count(*) FROM memberships WHERE customer_id=$c AND project_id=$p AND environment_id=$e", ("$c", CustomerId), ("$p", project), ("$e", environment)) != 0;
    FoundationCustomerLifecycle ReadLifecycle(SqliteConnection db, SqliteTransaction? tx) =>
        Rows(db, tx, "SELECT state,version,(SELECT enabled FROM cell WHERE singleton=1) FROM customer_lifecycle WHERE singleton=1",
            r => new FoundationCustomerLifecycle(CustomerId, CellId, r.GetString(0), r.GetInt64(1), r.GetInt64(2) == 1)).Single();
    static string NextLifecycleState(string current, string action) => (current, action) switch
    {
        ("Active", "Suspend") => "Suspended", ("Suspended", "Resume") => "Active",
        ("Active" or "Suspended", "BeginOffboarding") => "Offboarding", ("Offboarding", "CancelOffboarding") => "Suspended",
        ("Offboarding", "CompleteOffboarding") => "Offboarded", _ => throw new FoundationStoreException("InvalidLifecycleTransition", 400)
    };
    static bool AdministrativeWhileInactive(string kind) => kind is "cell.lifecycle" or "artifact.retention";
    static void ValidateApplicationScopeId(string value)
    {
        if (!CustomerContextResolver.ValidId(value)) throw new FoundationStoreException("InvalidScope", 400);
    }
    static void ValidatePurpose(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 8 or > 256 || value.Any(char.IsControl))
            throw new FoundationStoreException("AdministrativePurposeRequired", 400);
        _ = CanonicalJson(JsonSerializer.Serialize(new { purpose = value }, Json));
    }
}
