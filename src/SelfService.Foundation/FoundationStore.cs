using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed class FoundationStoreException(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record FoundationMembership(string CustomerId, string CellId, string ActorKey,
    string ProjectId, string EnvironmentId, string[] Roles, long Version, bool Enabled,
    DateTimeOffset? ValidUntilUtc = null, string? Purpose = null, string? ApprovalId = null);
public sealed record FoundationConnection(string Id, string CustomerId, string ProjectId, string EnvironmentId,
    string Provider, string ConfigurationJson, long Generation, bool Enabled);
public sealed record FoundationPlanInput(string ProjectId, string EnvironmentId, string ConnectionId,
    string Operation, string PayloadJson, bool ChangesExternalState, DateTimeOffset ExpiresAt,
    string SourceRevision, string CatalogDigest = "");
public sealed record FoundationPlan(string Id, string CustomerId, string CellId, string ProjectId,
    string EnvironmentId, string RequesterKey, long RequesterVersion, string ConnectionId,
    long ConnectionGeneration, long PolicyVersion, string Operation, string PayloadJson,
    bool ChangesExternalState, DateTimeOffset ExpiresAt, string SourceRevision, string CatalogDigest,
    string Digest, string? ApproverKey, long? ApproverVersion, DateTimeOffset? ApprovedAt);

/// <summary>One customer cell per database. All external effects belong to a separately authorized adapter.</summary>
public sealed partial class FoundationStore
{
    readonly string connectionString;
    readonly TimeProvider clock;
    readonly string databasePath;
    readonly Action<string, string>? activationGuard;
    public string CellId { get; }
    public string CustomerId { get; }
    static readonly HashSet<string> KnownRoles = ["Viewer", "Requester", "Approver", "Operator", "CustomerAdmin", "Auditor", "PlatformOperator", "SupportDelegate"];
    static readonly Regex Identifier = new("\\A[A-Za-z0-9][A-Za-z0-9._:-]{0,255}\\z", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    const string SchemaV1 = """
            CREATE TABLE IF NOT EXISTS cell(singleton INTEGER PRIMARY KEY CHECK(singleton=1),schema_version INTEGER NOT NULL,
              cell_id TEXT NOT NULL,customer_id TEXT NOT NULL,enabled INTEGER NOT NULL,policy_version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS actors(customer_id TEXT NOT NULL,actor_key TEXT NOT NULL,version INTEGER NOT NULL,
              PRIMARY KEY(customer_id,actor_key));
            CREATE TABLE IF NOT EXISTS memberships(customer_id TEXT NOT NULL,actor_key TEXT NOT NULL,project_id TEXT NOT NULL,
              environment_id TEXT NOT NULL,roles TEXT NOT NULL,enabled INTEGER NOT NULL,
              PRIMARY KEY(customer_id,actor_key,project_id,environment_id),
              FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key));
            CREATE TABLE IF NOT EXISTS connections(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,
              environment_id TEXT NOT NULL,provider TEXT NOT NULL,configuration TEXT NOT NULL,generation INTEGER NOT NULL,enabled INTEGER NOT NULL,
              PRIMARY KEY(customer_id,id));
            CREATE TABLE IF NOT EXISTS plans(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
              requester TEXT NOT NULL,requester_version INTEGER NOT NULL,connection_id TEXT NOT NULL,connection_generation INTEGER NOT NULL,
              policy_version INTEGER NOT NULL,operation TEXT NOT NULL,payload TEXT NOT NULL,changes_state INTEGER NOT NULL,expires INTEGER NOT NULL,
              source_revision TEXT NOT NULL,catalog_digest TEXT NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,
              PRIMARY KEY(customer_id,id),FOREIGN KEY(customer_id,connection_id) REFERENCES connections(customer_id,id));
            CREATE TABLE IF NOT EXISTS approvals(customer_id TEXT NOT NULL,plan_id TEXT NOT NULL,actor_key TEXT NOT NULL,
              actor_version INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,plan_id),
              FOREIGN KEY(customer_id,plan_id) REFERENCES plans(customer_id,id));
            CREATE TABLE IF NOT EXISTS jobs(customer_id TEXT NOT NULL,id TEXT NOT NULL,plan_id TEXT NOT NULL,operator_key TEXT NOT NULL,
              operator_version INTEGER NOT NULL,idempotency_key TEXT NOT NULL,state TEXT NOT NULL,worker_id TEXT,lease_until INTEGER,
              fence INTEGER NOT NULL,attempt INTEGER NOT NULL,receipt TEXT,reason TEXT,created INTEGER NOT NULL,updated INTEGER NOT NULL,
              PRIMARY KEY(customer_id,id),UNIQUE(customer_id,idempotency_key),FOREIGN KEY(customer_id,plan_id) REFERENCES plans(customer_id,id));
            CREATE TABLE IF NOT EXISTS outbox(customer_id TEXT NOT NULL,event_id TEXT NOT NULL,job_id TEXT NOT NULL,kind TEXT NOT NULL,
              created INTEGER NOT NULL,delivered INTEGER,PRIMARY KEY(customer_id,event_id),
              FOREIGN KEY(customer_id,job_id) REFERENCES jobs(customer_id,id));
            CREATE TABLE IF NOT EXISTS inbox(customer_id TEXT NOT NULL,event_id TEXT NOT NULL,received INTEGER NOT NULL,
              PRIMARY KEY(customer_id,event_id),FOREIGN KEY(customer_id,event_id) REFERENCES outbox(customer_id,event_id));
            CREATE TABLE IF NOT EXISTS audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT,customer_id TEXT NOT NULL,cell_id TEXT NOT NULL,
              event_id TEXT NOT NULL UNIQUE,actor_key TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
              action TEXT NOT NULL,target_id TEXT NOT NULL,outcome TEXT NOT NULL,reason TEXT NOT NULL,correlation TEXT NOT NULL,
              created INTEGER NOT NULL,previous_hash TEXT NOT NULL,event_hash TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit BEGIN SELECT RAISE(ABORT,'append-only audit'); END;
            CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit BEGIN SELECT RAISE(ABORT,'append-only audit'); END;
            CREATE TABLE IF NOT EXISTS artifacts(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
              name TEXT NOT NULL,content BLOB NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,id));
            CREATE INDEX IF NOT EXISTS jobs_ready ON jobs(customer_id,state,created);
            CREATE UNIQUE INDEX IF NOT EXISTS jobs_one_execution_per_plan ON jobs(customer_id,plan_id);
            """;

    public FoundationStore(string databasePath, string cellId, string customerId, TimeProvider? timeProvider = null, bool initializeSchema = true,
        Action<string, string>? activationGuard = null)
    {
        ValidateId(cellId); ValidateId(customerId);
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A cell database path is required.", nameof(databasePath));
        CellId = cellId; CustomerId = customerId; clock = timeProvider ?? TimeProvider.System;
        if (activationGuard is not null && initializeSchema) throw new ArgumentException("An activation-guarded runtime cannot initialize or migrate schema.");
        var path = Path.GetFullPath(databasePath); RejectLinks(path); this.databasePath = path; this.activationGuard = activationGuard;
        if (!initializeSchema && !File.Exists(path)) throw new FoundationStoreException("ExistingCellRequired");
        InspectExistingBinding(path, cellId, customerId);
        if (initializeSchema) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = initializeSchema ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 15 }.ToString();
        using var db = Open();
        if (!initializeSchema)
        {
            using var validation = db.BeginTransaction(deferred: false);
            if (Number(db, validation, "SELECT schema_version FROM cell WHERE singleton=1") != CurrentSchemaVersion)
                throw new FoundationStoreException("CurrentSchemaRequired");
            foreach (var table in new[] { "actors", "memberships", "connections", "plans", "approvals", "jobs", "outbox", "inbox", "audit", "artifacts" })
                if (Number(db, validation, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", table)) != 1)
                    throw new FoundationStoreException("MigrationSchemaMismatch");
            foreach (var trigger in new[] { "audit_no_update", "audit_no_delete" })
                if (Number(db, validation, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name", ("$name", trigger)) != 1)
                    throw new FoundationStoreException("MigrationSchemaMismatch");
            MigrateSchema(db, validation, CurrentSchemaVersion); // Validation only: this version executes no schema DDL.
            validation.Commit(); return;
        }
        Exec(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        using var tx = db.BeginTransaction(deferred: false);
        Exec(db, tx, SchemaV1);
        Exec(db, tx, "INSERT INTO cell(singleton,schema_version,cell_id,customer_id,enabled,policy_version) SELECT 1,1,$cell,$customer,1,1 WHERE NOT EXISTS(SELECT 1 FROM cell WHERE singleton=1)", ("$cell", CellId), ("$customer", CustomerId));
        var schemaVersion = 0;
        using var command = Command(db, tx, "SELECT schema_version,cell_id,customer_id FROM cell WHERE singleton=1");
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) is < 1 or > CurrentSchemaVersion || reader.GetString(1) != CellId || reader.GetString(2) != CustomerId)
                throw new FoundationStoreException("CellBindingMismatch");
            schemaVersion = reader.GetInt32(0);
        }
        MigrateSchema(db, tx, schemaVersion);
        tx.Commit();
    }

    public void BootstrapAdministrator(string actorKey, string projectId = "default", string environmentId = "dev", string? initialApproverKey = null)
    {
        RequireHuman(actorKey); ValidateId(projectId); ValidateId(environmentId);
        if (initialApproverKey is not null)
        {
            RequireHuman(initialApproverKey);
            if (initialApproverKey == actorKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        }
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        if (Number(db, tx, "SELECT count(*) FROM actors") != 0)
        {
            // Reopening a configured cell never restores a revoked bootstrap grant.
            var original = Rows(db, tx, "SELECT actor_key,project_id,environment_id FROM audit WHERE action='CustomerBootstrapped' ORDER BY sequence LIMIT 1",
                r => (Actor: r.GetString(0), Project: r.GetString(1), Environment: r.GetString(2))).SingleOrDefault();
            if (original.Actor != actorKey || original.Project != projectId || original.Environment != environmentId)
                throw new FoundationStoreException("BootstrapAlreadyCompleted");
            var reviewer = Rows(db, tx, "SELECT target_id FROM audit WHERE action='BootstrapReviewerConfigured' ORDER BY sequence LIMIT 1", r => r.GetString(0)).SingleOrDefault();
            if (reviewer != initialApproverKey) throw new FoundationStoreException("BootstrapReviewerMismatch");
            tx.Commit(); return;
        }
        Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1)", ("$c", CustomerId), ("$a", actorKey));
        Exec(db, tx, "INSERT INTO memberships VALUES($c,$a,$p,$e,$r,1)", ("$c", CustomerId), ("$a", actorKey),
            ("$p", projectId), ("$e", environmentId), ("$r", "[\"CustomerAdmin\"]"));
        Exec(db, tx, "INSERT OR IGNORE INTO scopes VALUES($c,$p,$e,$now)",
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$now", Now));
        Audit(db, tx, actorKey, projectId, environmentId, "CustomerBootstrapped", CustomerId, "Succeeded", "BootstrapConfigured", NewId());
        if (initialApproverKey is not null)
        {
            Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1)", ("$c", CustomerId), ("$a", initialApproverKey));
            Exec(db, tx, "INSERT INTO memberships VALUES($c,$a,$p,$e,'[\"Approver\"]',1)",
                ("$c", CustomerId), ("$a", initialApproverKey), ("$p", projectId), ("$e", environmentId));
            Audit(db, tx, actorKey, projectId, environmentId, "BootstrapReviewerConfigured", initialApproverKey, "Succeeded", "DistinctInitialReviewer", NewId());
        }
        tx.Commit();
    }

    public FoundationMembership SetMembership(string customerId, string changedBy, string actorKey, string projectId,
        string environmentId, string[] roles, bool enabled = true)
    {
        Scope(customerId); ValidateId(changedBy); ValidateId(actorKey); ValidateId(projectId); ValidateId(environmentId);
        if (changedBy == actorKey) throw new FoundationStoreException("SelfMembershipChangeDenied", 403);
        if (roles is null || roles.Length > KnownRoles.Count || roles.Any(r => !KnownRoles.Contains(r) || r == "SupportDelegate") || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new FoundationStoreException("InvalidRoles", 400);
        var normalized = roles.Order(StringComparer.Ordinal).ToArray();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, changedBy, projectId, environmentId, "CustomerAdmin");
        Exec(db, tx, "INSERT INTO actors VALUES($c,$a,1) ON CONFLICT(customer_id,actor_key) DO UPDATE SET version=version+1",
            ("$c", CustomerId), ("$a", actorKey));
        Exec(db, tx, """
            INSERT INTO memberships VALUES($c,$a,$p,$e,$r,$enabled)
            ON CONFLICT(customer_id,actor_key,project_id,environment_id) DO UPDATE SET roles=$r,enabled=$enabled
            """, ("$c", CustomerId), ("$a", actorKey), ("$p", projectId), ("$e", environmentId),
            ("$r", JsonSerializer.Serialize(normalized)), ("$enabled", enabled ? 1 : 0));
        var version = ActorVersion(db, tx, actorKey);
        Audit(db, tx, changedBy, projectId, environmentId, "MembershipChanged", actorKey, "Succeeded", enabled ? "GrantUpdated" : "GrantDisabled", NewId());
        tx.Commit();
        return new(CustomerId, CellId, actorKey, projectId, environmentId, normalized, version, enabled);
    }

    public IReadOnlyList<FoundationMembership> GetMemberships(string customerId, string actorKey)
    {
        Scope(customerId); ValidateId(actorKey); using var db = Open();
        var active = Number(db, null, "SELECT enabled FROM cell WHERE singleton=1") == 1;
        var result = Rows(db, null, """
            SELECT m.project_id,m.environment_id,m.roles,a.version,m.enabled FROM memberships m
            JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key WHERE m.customer_id=$c AND m.actor_key=$a
            ORDER BY m.project_id,m.environment_id
            """, r => new FoundationMembership(CustomerId, CellId, actorKey, r.GetString(0), r.GetString(1),
                JsonSerializer.Deserialize<string[]>(r.GetString(2))!, r.GetInt64(3), active && r.GetInt64(4) == 1),
            ("$c", CustomerId), ("$a", actorKey));
        return MergeSupportMemberships(db, actorKey, result, active);
    }

    public void SetCustomerEnabled(string customerId, string changedBy, bool enabled, string projectId = "default", string environmentId = "dev")
    {
        Scope(customerId); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, changedBy, projectId, environmentId, ["CustomerAdmin"], requireActive: false);
        var lifecycle = ReadLifecycle(db, tx);
        if (lifecycle.State is "Offboarding" or "Offboarded") throw new FoundationStoreException("LifecycleReviewRequired", 403);
        Exec(db, tx, "UPDATE cell SET enabled=$e,policy_version=policy_version+1 WHERE singleton=1", ("$e", enabled ? 1 : 0));
        Exec(db, tx, "UPDATE customer_lifecycle SET state=$state,version=version+1 WHERE singleton=1", ("$state", enabled ? "Active" : "Suspended"));
        Audit(db, tx, changedBy, projectId, environmentId, enabled ? "CustomerResumed" : "CustomerSuspended", CustomerId, "Succeeded", "PolicyVersionChanged", NewId());
        tx.Commit();
    }

    public FoundationConnection PutConnection(string customerId, string changedBy, string connectionId, string provider,
        string configurationJson, string projectId = "default", string environmentId = "dev", bool enabled = true)
    {
        Scope(customerId); ValidateId(connectionId); ValidateId(provider); ValidateId(projectId); ValidateId(environmentId);
        var configuration = CanonicalJson(configurationJson);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, changedBy, projectId, environmentId, "CustomerAdmin");
        var existing = FindConnection(db, tx, connectionId);
        if (existing is not null && (existing.ProjectId != projectId || existing.EnvironmentId != environmentId))
            throw new FoundationStoreException("ConnectionScopeMismatch", 403);
        var generation = (existing?.Generation ?? 0) + 1;
        Exec(db, tx, """
            INSERT INTO connections VALUES($c,$id,$p,$e,$provider,$configuration,$generation,$enabled)
            ON CONFLICT(customer_id,id) DO UPDATE SET provider=$provider,configuration=$configuration,generation=$generation,enabled=$enabled
            """, ("$c", CustomerId), ("$id", connectionId), ("$p", projectId), ("$e", environmentId),
            ("$provider", provider), ("$configuration", configuration), ("$generation", generation), ("$enabled", enabled ? 1 : 0));
        Audit(db, tx, changedBy, projectId, environmentId, "ConnectionChanged", connectionId, "Succeeded", "GenerationChanged", NewId());
        tx.Commit(); return new(connectionId, CustomerId, projectId, environmentId, provider, configuration, generation, enabled);
    }

    public FoundationConnection GetConnection(string customerId, string actorKey, string connectionId, string? projectId = null, string? environmentId = null)
    {
        Scope(customerId); ValidateId(connectionId); using var db = Open();
        var connection = FindConnection(db, null, connectionId) ?? throw new FoundationStoreException("NotFound", 404);
        ReadScope(projectId, environmentId, connection.ProjectId, connection.EnvironmentId);
        Require(db, null, actorKey, connection.ProjectId, connection.EnvironmentId, ["Viewer", "Requester", "Approver", "Operator", "CustomerAdmin", "Auditor"]);
        return connection;
    }

    public FoundationPlan CreatePlan(string customerId, string requesterKey, FoundationPlanInput input)
    {
        Scope(customerId); ValidateId(requesterKey); ValidateId(input.ProjectId); ValidateId(input.EnvironmentId);
        RequireHuman(requesterKey);
        ValidateId(input.ConnectionId); ValidateId(input.Operation);
        if (input.ExpiresAt <= clock.GetUtcNow() || input.ExpiresAt > clock.GetUtcNow().AddHours(1) ||
            string.IsNullOrWhiteSpace(input.SourceRevision) || input.SourceRevision.Length > 128 || input.SourceRevision.Any(char.IsControl) ||
            input.CatalogDigest is null || input.CatalogDigest.Length != 0 && !Regex.IsMatch(input.CatalogDigest, "\\A[a-f0-9]{64}\\z"))
            throw new FoundationStoreException("InvalidPlan", 400);
        var payload = CanonicalJson(input.PayloadJson); var id = NewId(); var now = Now;
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var version = Require(db, tx, requesterKey, input.ProjectId, input.EnvironmentId, "Requester");
        if (Number(db, tx, "SELECT count(*) FROM plans WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumPlansPerCell)
            throw new FoundationStoreException("PlanCapacityExceeded", 429);
        var connection = FindConnection(db, tx, input.ConnectionId) ?? throw new FoundationStoreException("NotFound", 404);
        if (!connection.Enabled || connection.ProjectId != input.ProjectId || connection.EnvironmentId != input.EnvironmentId)
            throw new FoundationStoreException("ConnectionScopeMismatch", 403);
        var policy = Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1");
        var draft = new FoundationPlan(id, CustomerId, CellId, input.ProjectId, input.EnvironmentId, requesterKey,
            version, connection.Id, connection.Generation, policy, input.Operation, payload, input.ChangesExternalState,
            At(input.ExpiresAt.ToUnixTimeMilliseconds()), input.SourceRevision, input.CatalogDigest, "", null, null, null);
        var digest = PlanDigest(draft, connection);
        Exec(db, tx, """
            INSERT INTO plans VALUES($c,$id,$p,$e,$requester,$rv,$connection,$cg,$policy,$op,$payload,$changes,$expires,$source,$catalog,$digest,$created)
            """, ("$c", CustomerId), ("$id", id), ("$p", input.ProjectId), ("$e", input.EnvironmentId), ("$requester", requesterKey),
            ("$rv", version), ("$connection", connection.Id), ("$cg", connection.Generation), ("$policy", policy),
            ("$op", input.Operation), ("$payload", payload), ("$changes", input.ChangesExternalState ? 1 : 0),
            ("$expires", input.ExpiresAt.ToUnixTimeMilliseconds()), ("$source", input.SourceRevision), ("$catalog", input.CatalogDigest),
            ("$digest", digest), ("$created", now));
        Audit(db, tx, requesterKey, input.ProjectId, input.EnvironmentId, "PlanCreated", id, "Succeeded", "ImmutablePlan", id);
        var result = ReadPlan(db, tx, id); tx.Commit(); return result;
    }

    public FoundationPlan GetPlan(string customerId, string actorKey, string planId, string? projectId = null, string? environmentId = null)
    {
        Scope(customerId); ValidateId(planId); using var db = Open(); var plan = ReadPlan(db, null, planId);
        ReadScope(projectId, environmentId, plan.ProjectId, plan.EnvironmentId);
        Require(db, null, actorKey, plan.ProjectId, plan.EnvironmentId, ["Viewer", "Requester", "Approver", "Operator", "Auditor"]);
        return plan;
    }

    public FoundationPlan ApprovePlan(string customerId, string approverKey, string planId, string expectedDigest)
    {
        Scope(customerId); ValidateId(planId); RequireHuman(approverKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = ReadPlan(db, tx, planId); var version = Require(db, tx, approverKey, plan.ProjectId, plan.EnvironmentId, "Approver");
        if (approverKey == plan.RequesterKey) throw new FoundationStoreException("SelfApprovalDenied", 403);
        MatchDigest(plan.Digest, expectedDigest); ValidatePlanAuthority(db, tx, plan);
        if (plan.ApproverKey is not null) throw new FoundationStoreException("ApprovalAlreadyRecorded");
        Exec(db, tx, "INSERT INTO approvals VALUES($c,$id,$a,$v,$d,$now)", ("$c", CustomerId), ("$id", planId),
            ("$a", approverKey), ("$v", version), ("$d", plan.Digest), ("$now", Now));
        Audit(db, tx, approverKey, plan.ProjectId, plan.EnvironmentId, "PlanApproved", planId, "Succeeded", "ExactDigestApproved", planId);
        var result = ReadPlan(db, tx, planId); tx.Commit(); return result;
    }

    void ValidatePlanAuthority(SqliteConnection db, SqliteTransaction? tx, FoundationPlan plan)
    {
        if (plan.ExpiresAt.ToUnixTimeMilliseconds() <= Now) throw new FoundationStoreException("PlanExpired");
        if (Number(db, tx, "SELECT enabled FROM cell WHERE singleton=1") != 1) throw new FoundationStoreException("CustomerSuspended", 403);
        if (Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1") != plan.PolicyVersion) throw new FoundationStoreException("PolicyChanged");
        if (Require(db, tx, plan.RequesterKey, plan.ProjectId, plan.EnvironmentId, "Requester") != plan.RequesterVersion)
            throw new FoundationStoreException("RequesterGrantChanged", 403);
        var connection = FindConnection(db, tx, plan.ConnectionId);
        if (connection is null || !connection.Enabled || connection.Generation != plan.ConnectionGeneration ||
            connection.ProjectId != plan.ProjectId || connection.EnvironmentId != plan.EnvironmentId)
            throw new FoundationStoreException("ConnectionChanged", 403);
        MatchDigest(plan.Digest, PlanDigest(plan, connection));
        if (plan.ApproverKey is not null && Number(db, tx,
            "SELECT count(*) FROM approvals WHERE customer_id=$c AND plan_id=$p AND digest=$digest",
            ("$c", CustomerId), ("$p", plan.Id), ("$digest", plan.Digest)) != 1)
            throw new FoundationStoreException("ApprovalDigestMismatch");
        if (plan.ApproverKey is not null && Require(db, tx, plan.ApproverKey, plan.ProjectId, plan.EnvironmentId, "Approver") != plan.ApproverVersion)
            throw new FoundationStoreException("ApproverGrantChanged", 403);
    }

    static string PlanDigest(FoundationPlan plan, FoundationConnection connection) =>
        Hash(JsonSerializer.Serialize(new { plan.CustomerId, plan.CellId, PlanId = plan.Id, plan.ProjectId,
            plan.EnvironmentId, plan.RequesterKey, plan.RequesterVersion, plan.ConnectionId,
            plan.ConnectionGeneration, ConnectionProvider = connection.Provider,
            ConnectionDigest = Hash(CanonicalJson(connection.ConfigurationJson)), plan.PolicyVersion, plan.Operation,
            Payload = CanonicalJson(plan.PayloadJson), plan.ChangesExternalState,
            Expires = plan.ExpiresAt.ToUnixTimeMilliseconds(), plan.SourceRevision, plan.CatalogDigest }, Json));

    FoundationPlan ReadPlan(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        return Rows(db, tx, """
            SELECT p.id,p.project_id,p.environment_id,p.requester,p.requester_version,p.connection_id,p.connection_generation,
              p.policy_version,p.operation,p.payload,p.changes_state,p.expires,p.source_revision,p.catalog_digest,p.digest,
              a.actor_key,a.actor_version,a.created FROM plans p LEFT JOIN approvals a ON a.customer_id=p.customer_id AND a.plan_id=p.id
            WHERE p.customer_id=$c AND p.id=$id
            """, r => new FoundationPlan(r.GetString(0), CustomerId, CellId, r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                r.GetString(5), r.GetInt64(6), r.GetInt64(7), r.GetString(8), r.GetString(9), r.GetInt64(10) == 1,
                At(r.GetInt64(11)), r.GetString(12), r.GetString(13), r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetInt64(16), r.IsDBNull(17) ? null : At(r.GetInt64(17))),
            ("$c", CustomerId), ("$id", id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
    }

    FoundationConnection? FindConnection(SqliteConnection db, SqliteTransaction? tx, string id) =>
        Rows(db, tx, "SELECT id,project_id,environment_id,provider,configuration,generation,enabled FROM connections WHERE customer_id=$c AND id=$id",
            r => new FoundationConnection(r.GetString(0), CustomerId, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6) == 1),
            ("$c", CustomerId), ("$id", id)).SingleOrDefault();

    long Require(SqliteConnection db, SqliteTransaction? tx, string actorKey, string projectId, string environmentId, params string[] roles) =>
        Require(db, tx, actorKey, projectId, environmentId, roles, true);
    long Require(SqliteConnection db, SqliteTransaction? tx, string actorKey, string projectId, string environmentId, string[] roles, bool requireActive)
    {
        ValidateId(actorKey);
        if (requireActive && Number(db, tx, "SELECT enabled FROM cell WHERE singleton=1") != 1)
            throw new FoundationStoreException("CustomerSuspended", 403);
        var grants = Rows(db, tx, """
            SELECT m.roles,m.enabled,a.version FROM memberships m JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key
            WHERE m.customer_id=$c AND m.actor_key=$a AND m.project_id=$p AND m.environment_id=$e
            """, r => (Roles: JsonSerializer.Deserialize<string[]>(r.GetString(0))!, Enabled: r.GetInt64(1) == 1, Version: r.GetInt64(2)),
            ("$c", CustomerId), ("$a", actorKey), ("$p", projectId), ("$e", environmentId));
        if (roles.Contains("Viewer", StringComparer.Ordinal) && TryRequireSupportRead(db, tx, actorKey, projectId, environmentId, out var supportVersion))
            return supportVersion;
        if (grants.Count != 1 || !grants[0].Enabled || !grants[0].Roles.Any(r => roles.Contains(r, StringComparer.Ordinal)))
            throw new FoundationStoreException("RoleDenied", 403);
        return grants[0].Version;
    }

    long ActorVersion(SqliteConnection db, SqliteTransaction? tx, string actorKey) =>
        Number(db, tx, "SELECT version FROM actors WHERE customer_id=$c AND actor_key=$a", ("$c", CustomerId), ("$a", actorKey));
    void Scope(string customerId)
    {
        if (!string.Equals(customerId, CustomerId, StringComparison.Ordinal)) throw new FoundationStoreException("CustomerScopeDenied", 403);
    }
    static void ValidateId(string value)
    {
        if (value is null || !Identifier.IsMatch(value)) throw new FoundationStoreException("InvalidIdentifier", 400);
    }
    static void ReadScope(string? expectedProject, string? expectedEnvironment, string actualProject, string actualEnvironment)
    {
        if (expectedProject is null && expectedEnvironment is null) return;
        if (expectedProject is null || expectedEnvironment is null) throw new FoundationStoreException("InvalidScope", 400);
        ValidateId(expectedProject); ValidateId(expectedEnvironment);
        if (expectedProject != actualProject || expectedEnvironment != actualEnvironment) throw new FoundationStoreException("NotFound", 404);
    }
    static void RequireHuman(string actorKey)
    {
        ValidateId(actorKey);
        if (actorKey.StartsWith("workload:", StringComparison.Ordinal)) throw new FoundationStoreException("HumanPrincipalRequired", 403);
    }
    static void MatchDigest(string actual, string supplied)
    {
        if (supplied is null || !Regex.IsMatch(supplied, "\\A[a-f0-9]{64}\\z") ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(supplied)))
            throw new FoundationStoreException("DigestMismatch");
    }
    long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    static DateTimeOffset At(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
    static string NewId() => Guid.NewGuid().ToString("N");
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); Exec(db, null, "PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL;"); AssertActivation(db, null); return db; }
        catch { db.Dispose(); throw; }
    }
    static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    static int Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(db, tx, sql, values); return command.ExecuteNonQuery(); }
    static long Number(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(db, tx, sql, values); var value = command.ExecuteScalar(); return value is null || value is DBNull ? 0 : Convert.ToInt64(value); }
    static List<T> Rows<T>(SqliteConnection db, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] values)
    {
        using var command = Command(db, tx, sql, values); using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(map(reader)); return result;
    }
    static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new FoundationStoreException("LinkedStorePathDenied", 400);
    }

    static void InspectExistingBinding(string path, string cellId, string customerId)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        if (Number(db, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='cell'") != 1)
            throw new FoundationStoreException("CellBindingMismatch");
        var rows = Rows(db, null, "SELECT schema_version,cell_id,customer_id FROM cell WHERE singleton=1",
            r => (Version: r.GetInt64(0), Cell: r.GetString(1), Customer: r.GetString(2)));
        if (rows.Count != 1 || rows[0].Version is < 1 or > CurrentSchemaVersion || rows[0].Cell != cellId || rows[0].Customer != customerId)
            throw new FoundationStoreException("CellBindingMismatch");
    }

    public static string CanonicalJson(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > 65536) throw new FoundationStoreException("PayloadTooLarge", 400);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FoundationStoreException("ObjectPayloadRequired", 400);
            using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) Write(doc.RootElement, writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException) { throw new FoundationStoreException("InvalidJson", 400); }
        static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var properties = element.EnumerateObject().ToArray();
                if (properties.Length > 256 || properties.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length)
                    throw new FoundationStoreException("AmbiguousPayload", 400);
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (Regex.IsMatch(property.Name, "(?i)(password|secret|token|authorization|credential|connection.?string|api.?key|private.?key)"))
                        throw new FoundationStoreException("CredentialPayloadDenied", 400);
                    writer.WritePropertyName(property.Name); Write(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                if (element.GetArrayLength() > 1024) throw new FoundationStoreException("PayloadTooLarge", 400);
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray();
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString()!;
                if (value.Length > 16384 || Regex.IsMatch(value, "(?i)(bearer\\s+[A-Za-z0-9._-]+|-----BEGIN [A-Z ]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+)"))
                    throw new FoundationStoreException("CredentialPayloadDenied", 400);
                writer.WriteStringValue(value);
            }
            else element.WriteTo(writer);
        }
    }
}
