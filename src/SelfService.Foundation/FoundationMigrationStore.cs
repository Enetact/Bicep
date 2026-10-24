using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationSchemaStatus(int Version, int SupportedVersion, string[] AppliedMigrations);

public sealed partial class FoundationStore
{
    public const int CurrentSchemaVersion = 4;
    const string SchemaV2 = """
        CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,checksum TEXT NOT NULL,applied INTEGER NOT NULL);
        CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
        CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
        CREATE TABLE scopes(customer_id TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
          created INTEGER NOT NULL,PRIMARY KEY(customer_id,project_id,environment_id));
        CREATE TABLE customer_lifecycle(singleton INTEGER PRIMARY KEY CHECK(singleton=1),state TEXT NOT NULL
          CHECK(state IN ('Active','Suspended','Offboarding','Offboarded')),version INTEGER NOT NULL CHECK(version>0));
        CREATE TABLE administrative_plans(customer_id TEXT NOT NULL,id TEXT NOT NULL,kind TEXT NOT NULL,
          project_id TEXT NOT NULL,environment_id TEXT NOT NULL,requester TEXT NOT NULL,requester_version INTEGER NOT NULL,
          policy_version INTEGER NOT NULL,payload TEXT NOT NULL,digest TEXT NOT NULL,expires INTEGER NOT NULL,state TEXT NOT NULL,
          approver TEXT,approver_version INTEGER,created INTEGER NOT NULL,PRIMARY KEY(customer_id,id));
        CREATE TABLE administrative_approvals(customer_id TEXT NOT NULL,plan_id TEXT NOT NULL,actor_key TEXT NOT NULL,
          actor_version INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,plan_id),
          FOREIGN KEY(customer_id,plan_id) REFERENCES administrative_plans(customer_id,id));
        CREATE TRIGGER administrative_approvals_no_update BEFORE UPDATE ON administrative_approvals BEGIN SELECT RAISE(ABORT,'immutable administrative approval'); END;
        CREATE TRIGGER administrative_approvals_no_delete BEFORE DELETE ON administrative_approvals BEGIN SELECT RAISE(ABORT,'immutable administrative approval'); END;
        CREATE TABLE scope_exports(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
          manifest TEXT NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,id));
        CREATE TABLE artifact_tombstones(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
          name TEXT NOT NULL,digest TEXT NOT NULL,content_length INTEGER NOT NULL,created INTEGER NOT NULL,archived INTEGER NOT NULL,
          administrative_plan TEXT NOT NULL,PRIMARY KEY(customer_id,id));
        CREATE INDEX administrative_ready ON administrative_plans(customer_id,state,expires);
        """;
    const string SchemaV3 = """
        CREATE TABLE support_grants(customer_id TEXT NOT NULL,actor_key TEXT NOT NULL,project_id TEXT NOT NULL,environment_id TEXT NOT NULL,
          actor_version INTEGER NOT NULL,issued INTEGER NOT NULL,expires INTEGER NOT NULL,purpose TEXT NOT NULL,approved_plan TEXT NOT NULL,
          enabled INTEGER NOT NULL,CHECK(expires>issued AND expires-issued<=86400000),
          PRIMARY KEY(customer_id,actor_key,project_id,environment_id),
          FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key),
          FOREIGN KEY(customer_id,approved_plan) REFERENCES administrative_plans(customer_id,id));
        CREATE INDEX support_active ON support_grants(customer_id,actor_key,enabled,expires);
        """;

    void MigrateSchema(SqliteConnection db, SqliteTransaction tx, int version)
    {
        if (version is < 1 or > CurrentSchemaVersion) throw new FoundationStoreException("UnsupportedSchemaVersion");
        if (version == 1)
        {
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed");
            Exec(db, tx, SchemaV2);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(1,$checksum,$now),(2,$v2,$now)",
                ("$checksum", Hash("platform-studio.foundation.schema.v1")), ("$v2", SchemaV2Checksum), ("$now", Now));
            Exec(db, tx, """
                INSERT INTO scopes SELECT customer_id,project_id,environment_id,$now FROM memberships GROUP BY customer_id,project_id,environment_id;
                INSERT OR IGNORE INTO scopes SELECT customer_id,project_id,environment_id,$now FROM connections GROUP BY customer_id,project_id,environment_id;
                INSERT INTO customer_lifecycle SELECT 1,CASE enabled WHEN 1 THEN 'Active' ELSE 'Suspended' END,1 FROM cell WHERE singleton=1;
                UPDATE cell SET schema_version=2 WHERE singleton=1;
                """, ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            {
                var scope = BootstrapScope(db, tx);
                Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId,
                    "Succeeded", "SchemaV1ToV2", SchemaV2Checksum);
            }
        }
        var history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 2 || history[0] != (1, Hash("platform-studio.foundation.schema.v1")) || history[1] != (2, SchemaV2Checksum))
            throw new FoundationStoreException("MigrationHistoryMismatch");
        foreach (var table in new[] { "schema_migrations", "scopes", "customer_lifecycle", "administrative_plans", "administrative_approvals", "scope_exports", "artifact_tombstones" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", table)) != 1)
                throw new FoundationStoreException("MigrationSchemaMismatch");
        foreach (var trigger in new[] { "schema_migrations_no_update", "schema_migrations_no_delete", "administrative_approvals_no_update", "administrative_approvals_no_delete" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name", ("$name", trigger)) != 1)
                throw new FoundationStoreException("MigrationSchemaMismatch");
        if (version <= 2)
        {
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed");
            Exec(db, tx, SchemaV3);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(3,$checksum,$now); UPDATE cell SET schema_version=3 WHERE singleton=1",
                ("$checksum", SchemaV3Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            {
                var scope = BootstrapScope(db, tx);
                Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId,
                    "Succeeded", "SchemaV2ToV3", SchemaV3Checksum);
            }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 3 || history[2] != (3, SchemaV3Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='support_grants'") != 1)
            throw new FoundationStoreException("MigrationSchemaMismatch");
        if (version <= 3)
        {
            if (history.Count != 3) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed");
            Exec(db, tx, WorkerOperationsSchemaV4);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(4,$checksum,$now); UPDATE cell SET schema_version=4 WHERE singleton=1",
                ("$checksum", SchemaV4Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            {
                var scope = BootstrapScope(db, tx);
                Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId,
                    "Succeeded", "SchemaV3ToV4", SchemaV4Checksum);
            }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count != 4 || history[3] != (4, SchemaV4Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='worker_heartbeats'") != 1 ||
            Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='worker_heartbeat_health'") != 1)
            throw new FoundationStoreException("MigrationSchemaMismatch");
    }

    void ValidateAdministrativeAuditChain(SqliteConnection db, SqliteTransaction tx, string errorCode)
    {
        var rows = Rows(db, tx, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit ORDER BY sequence LIMIT 100001
            """, AuditRow);
        if (rows.Count > 100000) throw new FoundationStoreException("AuditVerificationLimit");
        var previous = ""; long sequence = 0;
        foreach (var row in rows)
        {
            if (row.Sequence != ++sequence || row.CustomerId != CustomerId || row.CellId != CellId || row.PreviousHash != previous ||
                row.EventHash != AuditHash(row.Sequence, row.CustomerId, row.CellId, row.EventId, row.ActorKey, row.ProjectId,
                    row.EnvironmentId, row.Action, row.TargetId, row.Outcome, row.Reason, row.CorrelationId,
                    row.CreatedAt.ToUnixTimeMilliseconds(), row.PreviousHash)) throw new FoundationStoreException(errorCode);
            previous = row.EventHash;
        }
    }

    static string SchemaV2Checksum => Hash(SchemaV2.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV3Checksum => Hash(SchemaV3.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV4Checksum => Hash(WorkerOperationsSchemaV4.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string ScalarText(SqliteConnection db, SqliteTransaction? tx, string sql)
    { using var command = Command(db, tx, sql); return command.ExecuteScalar()?.ToString() ?? ""; }

    public FoundationSchemaStatus SchemaStatus()
    {
        using var db = Open();
        var applied = Rows(db, null, "SELECT checksum FROM schema_migrations ORDER BY version", r => r.GetString(0));
        return new((int)Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1"), CurrentSchemaVersion, applied.ToArray());
    }
}
