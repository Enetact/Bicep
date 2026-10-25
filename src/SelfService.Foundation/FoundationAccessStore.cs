using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationSupportAccess(string CustomerId, string CellId, string ActorKey, string ProjectId,
    string EnvironmentId, long Version, DateTimeOffset IssuedUtc, DateTimeOffset ValidUntilUtc, string Purpose,
    string ApprovalId, bool Enabled);
public sealed record FoundationAccessRevocation(string CustomerId, string ProjectId, string EnvironmentId,
    string ActorKey, bool StandingMembershipDisabled, bool SupportAccessDisabled, string PurposeDigest);

public sealed partial class FoundationStore
{
    sealed record MembershipChangePayload(string TargetActor, string[] Roles, bool Enabled, string Purpose, long ExpectedTargetVersion);
    sealed record SupportAccessPayload(string TargetActor, DateTimeOffset ValidUntilUtc, string Purpose, long ExpectedTargetVersion);

    public FoundationAdministrativePlan PrepareMembershipChange(string customerId, string requesterKey, string projectId,
        string environmentId, string targetActor, string[] roles, bool enabled, string purpose)
    {
        Scope(customerId); RequireHuman(requesterKey); ValidateId(targetActor); ValidateApplicationScopeId(projectId);
        ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose); ValidateReviewedRoles(targetActor, roles);
        if (requesterKey == targetActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, requesterKey, projectId, environmentId, "CustomerAdmin");
        EnsureNotLastAdministrator(db, tx, targetActor, projectId, environmentId, enabled && roles.Contains("CustomerAdmin", StringComparer.Ordinal));
        var plan = CreateAdministrativePlan(db, tx, requesterKey, projectId, environmentId, "membership.change",
            JsonSerializer.Serialize(new MembershipChangePayload(targetActor, roles.Order(StringComparer.Ordinal).ToArray(),
                enabled && roles.Length > 0, purpose, ActorVersion(db, tx, targetActor)), Json));
        tx.Commit(); return plan;
    }

    public FoundationMembership ExecuteMembershipChange(string customerId, string executorKey, string id, string expectedDigest)
    {
        Scope(customerId); RequireHuman(executorKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, "membership.change");
        var payload = JsonSerializer.Deserialize<MembershipChangePayload>(plan.PayloadJson, Json)!;
        if (executorKey == payload.TargetActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        ValidateReviewedRoles(payload.TargetActor, payload.Roles);
        if (ActorVersion(db, tx, payload.TargetActor) != payload.ExpectedTargetVersion) throw new FoundationStoreException("TargetGrantChanged");
        EnsureNotLastAdministrator(db, tx, payload.TargetActor, plan.ProjectId, plan.EnvironmentId,
            payload.Enabled && payload.Roles.Contains("CustomerAdmin", StringComparer.Ordinal));
        Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1) ON CONFLICT(customer_id,actor_key) DO UPDATE SET version=version+1",
            ("$c", CustomerId), ("$a", payload.TargetActor));
        Exec(db, tx, """
            INSERT INTO memberships VALUES($c,$a,$p,$e,$roles,$enabled)
            ON CONFLICT(customer_id,actor_key,project_id,environment_id) DO UPDATE SET roles=$roles,enabled=$enabled
            """, ("$c", CustomerId), ("$a", payload.TargetActor), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId),
            ("$roles", JsonSerializer.Serialize(payload.Roles, Json)), ("$enabled", payload.Enabled ? 1 : 0));
        Exec(db, tx, "UPDATE support_grants SET enabled=0 WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e",
            ("$c", CustomerId), ("$a", payload.TargetActor), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId));
        var version = ActorVersion(db, tx, payload.TargetActor);
        CompleteAdministrativePlan(db, tx, executorKey, plan, "ReviewedMembershipChanged");
        Audit(db, tx, executorKey, plan.ProjectId, plan.EnvironmentId, "MembershipChanged", payload.TargetActor,
            "Succeeded", "DistinctReviewerApproved", plan.Id);
        tx.Commit(); return new(CustomerId, CellId, payload.TargetActor, plan.ProjectId, plan.EnvironmentId, payload.Roles, version, payload.Enabled);
    }

    public FoundationAdministrativePlan PrepareSupportAccess(string customerId, string requesterKey, string projectId,
        string environmentId, string targetActor, DateTimeOffset validUntilUtc, string purpose)
    {
        Scope(customerId); RequireHuman(requesterKey); RequireHuman(targetActor); ValidateApplicationScopeId(projectId);
        ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose);
        if (requesterKey == targetActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        if (validUntilUtc <= clock.GetUtcNow() || validUntilUtc > clock.GetUtcNow().AddDays(1)) throw new FoundationStoreException("InvalidSupportLifetime", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, requesterKey, projectId, environmentId, "CustomerAdmin");
        EnsureNoStandingSupportPrivileges(db, tx, targetActor, projectId, environmentId);
        if (Number(db, tx, "SELECT count(*) FROM support_grants WHERE customer_id=$c AND actor_key=$a AND enabled=1 AND expires>$now",
            ("$c", CustomerId), ("$a", targetActor), ("$now", Now)) >= 32)
            throw new FoundationStoreException("SupportScopeCapacityExceeded", 429);
        var plan = CreateAdministrativePlan(db, tx, requesterKey, projectId, environmentId, "support.access",
            JsonSerializer.Serialize(new SupportAccessPayload(targetActor, At(validUntilUtc.ToUnixTimeMilliseconds()), purpose,
                ActorVersion(db, tx, targetActor)), Json));
        tx.Commit(); return plan;
    }

    public FoundationSupportAccess ExecuteSupportAccess(string customerId, string executorKey, string id, string expectedDigest)
    {
        Scope(customerId); RequireHuman(executorKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, "support.access");
        var payload = JsonSerializer.Deserialize<SupportAccessPayload>(plan.PayloadJson, Json)!;
        RequireHuman(payload.TargetActor);
        if (executorKey == payload.TargetActor) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        if (payload.ValidUntilUtc <= clock.GetUtcNow() || payload.ValidUntilUtc > clock.GetUtcNow().AddDays(1))
            throw new FoundationStoreException("InvalidSupportLifetime", 400);
        if (ActorVersion(db, tx, payload.TargetActor) != payload.ExpectedTargetVersion) throw new FoundationStoreException("TargetGrantChanged");
        EnsureNoStandingSupportPrivileges(db, tx, payload.TargetActor, plan.ProjectId, plan.EnvironmentId);
        Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1) ON CONFLICT(customer_id,actor_key) DO UPDATE SET version=version+1",
            ("$c", CustomerId), ("$a", payload.TargetActor));
        var version = ActorVersion(db, tx, payload.TargetActor); var issued = Now;
        Exec(db, tx, """
            INSERT INTO support_grants VALUES($c,$a,$p,$e,$version,$issued,$expires,$purpose,$approval,1)
            ON CONFLICT(customer_id,actor_key,project_id,environment_id) DO UPDATE SET actor_version=$version,issued=$issued,
              expires=$expires,purpose=$purpose,approved_plan=$approval,enabled=1
            """, ("$c", CustomerId), ("$a", payload.TargetActor), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId),
            ("$version", version), ("$issued", issued), ("$expires", payload.ValidUntilUtc.ToUnixTimeMilliseconds()), ("$purpose", payload.Purpose), ("$approval", id));
        CompleteAdministrativePlan(db, tx, executorKey, plan, "ReviewedSupportGranted");
        Audit(db, tx, executorKey, plan.ProjectId, plan.EnvironmentId, "SupportAccessGranted", payload.TargetActor, "Succeeded", "PurposeAndExpiryBound", id);
        tx.Commit(); return new(CustomerId, CellId, payload.TargetActor, plan.ProjectId, plan.EnvironmentId, version,
            At(issued), payload.ValidUntilUtc, payload.Purpose, id, true);
    }

    public FoundationAccessRevocation RevokeScopedAccess(string customerId, string administratorKey, string projectId,
        string environmentId, string targetActor, string purpose)
    {
        Scope(customerId); RequireHuman(administratorKey); ValidateId(targetActor); ValidateApplicationScopeId(projectId);
        ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, administratorKey, projectId, environmentId, ["CustomerAdmin"], requireActive: false);
        EnsureNotLastAdministrator(db, tx, targetActor, projectId, environmentId, false);
        var membership = Exec(db, tx, "UPDATE memberships SET enabled=0 WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e AND enabled=1",
            ("$c", CustomerId), ("$a", targetActor), ("$p", projectId), ("$e", environmentId)) > 0;
        var support = Exec(db, tx, "UPDATE support_grants SET enabled=0 WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e AND enabled=1",
            ("$c", CustomerId), ("$a", targetActor), ("$p", projectId), ("$e", environmentId)) > 0;
        if (membership || support) Exec(db, tx, "UPDATE actors SET version=version+1 WHERE customer_id=$c AND actor_key=$a", ("$c", CustomerId), ("$a", targetActor));
        var reason = Hash(purpose);
        Audit(db, tx, administratorKey, projectId, environmentId, "ScopedAccessRevoked", targetActor,
            "Succeeded", "UrgentDenyOnly", reason);
        tx.Commit(); return new(CustomerId, projectId, environmentId, targetActor, membership, support, reason);
    }

    public FoundationSupportAccess? SupportAccessStatus(string customerId, string actorKey, string projectId,
        string environmentId, string targetActor)
    {
        Scope(customerId); RequireHuman(actorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId); ValidateId(targetActor);
        using var db = Open(); Require(db, null, actorKey, projectId, environmentId, ["CustomerAdmin", "Approver", "Auditor"], requireActive: false);
        var grant = ReadSupportGrant(db, null, targetActor, projectId, environmentId);
        return grant is null ? null : grant with { Enabled = ValidSupportGrant(db, null, grant) };
    }

    public void RecordSupportAccess(CustomerContext context)
    {
        Scope(context.CustomerId);
        if (context.CellId != CellId || context.Kind != PrincipalKind.User || context.SupportApprovalId is null ||
            context.Capability is not (FoundationCapabilities.CatalogRead or FoundationCapabilities.EvidenceRead))
            throw new FoundationStoreException("SupportContextRequired", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var grant = ReadSupportGrant(db, tx, context.ActorKey, context.ProjectId, context.EnvironmentId);
        if (grant is null || !ValidSupportGrant(db, tx, grant) || grant.ApprovalId != context.SupportApprovalId ||
            grant.Version != context.RoleVersion || grant.Purpose != context.SupportPurpose || grant.ValidUntilUtc != context.SupportValidUntilUtc ||
            context.ExpiresUtc <= clock.GetUtcNow())
            throw new FoundationStoreException("SupportAccessExpiredOrRevoked", 403);
        Audit(db, tx, context.ActorKey, context.ProjectId, context.EnvironmentId, "SupportEvidenceAccess", context.SupportApprovalId,
            "Succeeded", "PurposeBoundRead", context.SupportApprovalId); tx.Commit();
    }

    IReadOnlyList<FoundationMembership> MergeSupportMemberships(SqliteConnection db, string actor,
        List<FoundationMembership> standing, bool active)
    {
        if (!active || actor.StartsWith("workload:", StringComparison.Ordinal)) return standing;
        var leases = Rows(db, null, """
            SELECT project_id,environment_id FROM support_grants WHERE customer_id=$c AND actor_key=$a AND enabled=1 AND expires>$now LIMIT 33
            """, r => (Project: r.GetString(0), Environment: r.GetString(1)), ("$c", CustomerId), ("$a", actor), ("$now", Now));
        if (leases.Count > 32) throw new FoundationStoreException("SupportScopeCapacityExceeded", 403);
        foreach (var scope in leases)
        {
            var grant = ReadSupportGrant(db, null, actor, scope.Project, scope.Environment);
            if (grant is null || !ValidSupportGrant(db, null, grant)) continue;
            var existing = standing.SingleOrDefault(m => m.ProjectId == scope.Project && m.EnvironmentId == scope.Environment);
            if (existing is { Enabled: true } && existing.Roles.Length > 0) continue;
            standing.RemoveAll(m => m.ProjectId == scope.Project && m.EnvironmentId == scope.Environment);
            standing.Add(new(CustomerId, CellId, actor, scope.Project, scope.Environment, ["SupportDelegate"], grant.Version, true,
                grant.ValidUntilUtc, grant.Purpose, grant.ApprovalId));
        }
        return standing;
    }

    bool TryRequireSupportRead(SqliteConnection db, SqliteTransaction? tx, string actor, string project, string environment, out long version)
    {
        version = 0;
        if (actor.StartsWith("workload:", StringComparison.Ordinal)) return false;
        if (tx is null)
        {
            var existing = ReadSupportGrant(db, null, actor, project, environment);
            if (existing is null || !existing.Enabled || existing.ValidUntilUtc <= clock.GetUtcNow()) return false;
            using var supportTx = db.BeginTransaction(deferred: false);
            var result = TryRequireSupportRead(db, supportTx, actor, project, environment, out version);
            supportTx.Commit(); return result;
        }
        var grant = ReadSupportGrant(db, tx, actor, project, environment);
        if (grant is null || !ValidSupportGrant(db, tx, grant)) return false;
        Audit(db, tx, actor, project, environment, "SupportEvidenceAccess", grant.ApprovalId, "Succeeded", "PurposeBoundRead", grant.ApprovalId);
        version = grant.Version; return true;
    }

    FoundationSupportAccess? ReadSupportGrant(SqliteConnection db, SqliteTransaction? tx, string actor, string project, string environment) =>
        Rows(db, tx, """
            SELECT actor_version,issued,expires,purpose,approved_plan,enabled FROM support_grants
            WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e
            """, r => new FoundationSupportAccess(CustomerId, CellId, actor, project, environment, r.GetInt64(0), At(r.GetInt64(1)), At(r.GetInt64(2)),
                r.GetString(3), r.GetString(4), r.GetInt64(5) == 1), ("$c", CustomerId), ("$a", actor), ("$p", project), ("$e", environment)).SingleOrDefault();

    bool ValidSupportGrant(SqliteConnection db, SqliteTransaction? tx, FoundationSupportAccess grant)
    {
        if (!grant.Enabled || grant.ValidUntilUtc <= clock.GetUtcNow() || grant.IssuedUtc > clock.GetUtcNow() ||
            grant.ValidUntilUtc - grant.IssuedUtc > TimeSpan.FromDays(1) || grant.ActorKey.StartsWith("workload:", StringComparison.Ordinal) ||
            ActorVersion(db, tx, grant.ActorKey) != grant.Version || Number(db, tx, "SELECT enabled FROM cell WHERE singleton=1") != 1) return false;
        try
        {
            var plan = ReadAdministrativePlan(db, tx, grant.ApprovalId); var payload = JsonSerializer.Deserialize<SupportAccessPayload>(plan.PayloadJson, Json)!;
            MatchDigest(AdministrativeDigest(plan), plan.Digest);
            if (plan.Kind != "support.access" || plan.State != "Executed" || plan.ProjectId != grant.ProjectId || plan.EnvironmentId != grant.EnvironmentId ||
                payload.TargetActor != grant.ActorKey || payload.ValidUntilUtc != grant.ValidUntilUtc || payload.Purpose != grant.Purpose ||
                payload.ExpectedTargetVersion == long.MaxValue || grant.Version != payload.ExpectedTargetVersion + 1 ||
                grant.IssuedUtc > plan.ExpiresAt ||
                plan.RequesterKey == grant.ActorKey || plan.ApproverKey is null || plan.ApproverKey == grant.ActorKey ||
                plan.PolicyVersion != Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") ||
                Require(db, tx, plan.RequesterKey, plan.ProjectId, plan.EnvironmentId, "CustomerAdmin") != plan.RequesterVersion ||
                Require(db, tx, plan.ApproverKey, plan.ProjectId, plan.EnvironmentId, "Approver") != plan.ApproverVersion) return false;
            return Number(db, tx, """
                SELECT count(*) FROM administrative_approvals WHERE customer_id=$c AND plan_id=$id AND actor_key=$a AND actor_version=$v AND digest=$digest
                """, ("$c", CustomerId), ("$id", plan.Id), ("$a", plan.ApproverKey), ("$v", plan.ApproverVersion), ("$digest", plan.Digest)) == 1;
        }
        catch (Exception e) when (e is FoundationStoreException or JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    void EnsureNoStandingSupportPrivileges(SqliteConnection db, SqliteTransaction tx, string actor, string project, string environment)
    {
        if (Rows(db, tx, "SELECT roles FROM memberships WHERE customer_id=$c AND actor_key=$a AND project_id=$p AND environment_id=$e AND enabled=1",
            r => JsonSerializer.Deserialize<string[]>(r.GetString(0))!, ("$c", CustomerId), ("$a", actor), ("$p", project), ("$e", environment)).Any(roles => roles.Length > 0))
            throw new FoundationStoreException("SupportMustBeReadOnly", 403);
    }

    void EnsureNotLastAdministrator(SqliteConnection db, SqliteTransaction tx, string target, string project, string environment, bool retainsAdministrator)
    {
        if (retainsAdministrator) return;
        var admins = Rows(db, tx, "SELECT actor_key,roles FROM memberships WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND enabled=1",
            r => (Actor: r.GetString(0), Roles: JsonSerializer.Deserialize<string[]>(r.GetString(1))!), ("$c", CustomerId), ("$p", project), ("$e", environment))
            .Where(m => m.Roles.Contains("CustomerAdmin", StringComparer.Ordinal)).ToArray();
        if (admins.Length == 1 && admins[0].Actor == target) throw new FoundationStoreException("LastAdministratorProtected", 403);
    }

    static void ValidateReviewedRoles(string target, string[] roles)
    {
        if (roles is null || roles.Length > KnownRoles.Count || roles.Any(role => !KnownRoles.Contains(role) || role == "SupportDelegate") ||
            roles.Distinct(StringComparer.Ordinal).Count() != roles.Length || target.StartsWith("workload:", StringComparison.Ordinal) &&
            roles.Any(role => role is not ("Operator" or "PlatformOperator"))) throw new FoundationStoreException("InvalidRoles", 400);
    }

    static string? AccessPlanTarget(FoundationAdministrativePlan plan) => plan.Kind switch
    {
        "membership.change" => JsonSerializer.Deserialize<MembershipChangePayload>(plan.PayloadJson, Json)!.TargetActor,
        "support.access" => JsonSerializer.Deserialize<SupportAccessPayload>(plan.PayloadJson, Json)!.TargetActor,
        _ => null
    };

    void ValidateAccessTargetSnapshot(SqliteConnection db, SqliteTransaction tx, FoundationAdministrativePlan plan)
    {
        var expected = plan.Kind switch
        {
            "membership.change" => JsonSerializer.Deserialize<MembershipChangePayload>(plan.PayloadJson, Json)!.ExpectedTargetVersion,
            "support.access" => JsonSerializer.Deserialize<SupportAccessPayload>(plan.PayloadJson, Json)!.ExpectedTargetVersion,
            _ => (long?)null
        };
        if (expected is not null && ActorVersion(db, tx, AccessPlanTarget(plan)!) != expected)
            throw new FoundationStoreException("TargetGrantChanged");
    }
}
