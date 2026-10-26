using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationSchemaStatus(int Version, int SupportedVersion, string[] AppliedMigrations);

public sealed partial class FoundationStore
{
    public const int CurrentSchemaVersion = 13;
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

    const string SchemaV5 = """
        ALTER TABLE cell ADD COLUMN database_instance_id TEXT;
        UPDATE cell SET database_instance_id=lower(hex(randomblob(16))) WHERE singleton=1;
        CREATE TRIGGER cell_instance_insert_guard BEFORE INSERT ON cell
          WHEN NEW.database_instance_id IS NULL OR length(NEW.database_instance_id)!=32 OR NEW.database_instance_id GLOB '*[^0-9a-f]*'
          BEGIN SELECT RAISE(ABORT,'invalid database instance identity'); END;
        CREATE TRIGGER cell_instance_update_guard BEFORE UPDATE OF database_instance_id ON cell
          WHEN NEW.database_instance_id IS NULL OR length(NEW.database_instance_id)!=32 OR NEW.database_instance_id GLOB '*[^0-9a-f]*'
          BEGIN SELECT RAISE(ABORT,'invalid database instance identity'); END;
        """;

    const string SchemaV6 = """
        CREATE TABLE credential_maintenance_intents(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,
          environment_id TEXT NOT NULL,operation TEXT NOT NULL CHECK(operation IN ('Revoke','Rewrap')),requester TEXT NOT NULL,
          requester_version INTEGER NOT NULL,target TEXT NOT NULL,target_sha256 TEXT NOT NULL,connection_id TEXT NOT NULL,
          connection_generation INTEGER NOT NULL,connection_sha256 TEXT NOT NULL,policy_version INTEGER NOT NULL,
          database_instance_id TEXT NOT NULL,purpose_sha256 TEXT NOT NULL,expires INTEGER NOT NULL,digest TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('Prepared','Approved','Quarantined')),created INTEGER NOT NULL,
          PRIMARY KEY(customer_id,id),FOREIGN KEY(customer_id,requester) REFERENCES actors(customer_id,actor_key),
          FOREIGN KEY(customer_id,project_id,environment_id) REFERENCES scopes(customer_id,project_id,environment_id),
          FOREIGN KEY(customer_id,connection_id) REFERENCES connections(customer_id,id));
        CREATE TABLE credential_maintenance_approvals(customer_id TEXT NOT NULL,intent_id TEXT NOT NULL,actor_key TEXT NOT NULL,
          actor_version INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,intent_id),
          FOREIGN KEY(customer_id,intent_id) REFERENCES credential_maintenance_intents(customer_id,id),
          FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key));
        CREATE TABLE credential_maintenance_executions(customer_id TEXT NOT NULL,intent_id TEXT NOT NULL,admission_id TEXT NOT NULL,
          executor_key TEXT NOT NULL,executor_version INTEGER NOT NULL,database_instance_id TEXT NOT NULL,expected_after_sha256 TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('OutcomeUnknown','Succeeded')),reason TEXT NOT NULL,result_json TEXT,result_sha256 TEXT,
          admitted INTEGER NOT NULL,completed INTEGER,updated INTEGER NOT NULL,PRIMARY KEY(customer_id,intent_id),
          FOREIGN KEY(customer_id,intent_id) REFERENCES credential_maintenance_intents(customer_id,id),
          FOREIGN KEY(customer_id,executor_key) REFERENCES actors(customer_id,actor_key));
        CREATE INDEX credential_maintenance_scope ON credential_maintenance_intents(customer_id,project_id,environment_id,state,expires);
        CREATE UNIQUE INDEX credential_maintenance_admission ON credential_maintenance_executions(customer_id,admission_id);
        CREATE INDEX credential_maintenance_execution_state ON credential_maintenance_executions(customer_id,state,admitted);
        CREATE TRIGGER credential_maintenance_intents_immutable BEFORE UPDATE OF customer_id,id,project_id,environment_id,operation,
          requester,requester_version,target,target_sha256,connection_id,connection_generation,connection_sha256,policy_version,
          database_instance_id,purpose_sha256,expires,digest,created ON credential_maintenance_intents
          BEGIN SELECT RAISE(ABORT,'immutable maintenance intent'); END;
        CREATE TRIGGER credential_maintenance_intents_no_delete BEFORE DELETE ON credential_maintenance_intents
          BEGIN SELECT RAISE(ABORT,'retained maintenance intent'); END;
        CREATE TRIGGER credential_maintenance_approvals_no_update BEFORE UPDATE ON credential_maintenance_approvals
          BEGIN SELECT RAISE(ABORT,'immutable maintenance approval'); END;
        CREATE TRIGGER credential_maintenance_approvals_no_delete BEFORE DELETE ON credential_maintenance_approvals
          BEGIN SELECT RAISE(ABORT,'immutable maintenance approval'); END;
        CREATE TRIGGER credential_maintenance_execution_immutable BEFORE UPDATE OF customer_id,intent_id,admission_id,executor_key,
          executor_version,database_instance_id,expected_after_sha256,admitted ON credential_maintenance_executions
          BEGIN SELECT RAISE(ABORT,'immutable maintenance admission'); END;
        CREATE TRIGGER credential_maintenance_success_immutable BEFORE UPDATE ON credential_maintenance_executions WHEN OLD.state='Succeeded'
          BEGIN SELECT RAISE(ABORT,'immutable maintenance result'); END;
        CREATE TRIGGER credential_maintenance_executions_no_delete BEFORE DELETE ON credential_maintenance_executions
          BEGIN SELECT RAISE(ABORT,'retained maintenance execution'); END;
        """;

    const string SchemaV7 = """
        CREATE TABLE credential_maintenance_observation_requests(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,
          environment_id TEXT NOT NULL,original_intent_id TEXT NOT NULL,original_intent_digest TEXT NOT NULL,original_admission_id TEXT NOT NULL,
          expected_after_sha256 TEXT NOT NULL,observation TEXT NOT NULL,observation_sha256 TEXT NOT NULL,requester TEXT NOT NULL,
          requester_version INTEGER NOT NULL,policy_version INTEGER NOT NULL,database_instance_id TEXT NOT NULL,purpose_sha256 TEXT NOT NULL,
          expires INTEGER NOT NULL,digest TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('Prepared','Approved','Quarantined')),created INTEGER NOT NULL,
          PRIMARY KEY(customer_id,id),FOREIGN KEY(customer_id,original_intent_id) REFERENCES credential_maintenance_intents(customer_id,id),
          FOREIGN KEY(customer_id,requester) REFERENCES actors(customer_id,actor_key),
          FOREIGN KEY(customer_id,project_id,environment_id) REFERENCES scopes(customer_id,project_id,environment_id));
        CREATE TABLE credential_maintenance_observation_approvals(customer_id TEXT NOT NULL,request_id TEXT NOT NULL,actor_key TEXT NOT NULL,
          actor_version INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(customer_id,request_id),
          FOREIGN KEY(customer_id,request_id) REFERENCES credential_maintenance_observation_requests(customer_id,id),
          FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key));
        CREATE TABLE credential_maintenance_observation_receipts(customer_id TEXT NOT NULL,request_id TEXT NOT NULL,id TEXT NOT NULL,
          recorder_key TEXT NOT NULL,recorder_version INTEGER NOT NULL,status TEXT NOT NULL CHECK(status='ObservedPublished'),
          request_digest TEXT NOT NULL,observation_sha256 TEXT NOT NULL,observed_at INTEGER NOT NULL,digest TEXT NOT NULL,
          PRIMARY KEY(customer_id,request_id),FOREIGN KEY(customer_id,request_id) REFERENCES credential_maintenance_observation_requests(customer_id,id),
          FOREIGN KEY(customer_id,recorder_key) REFERENCES actors(customer_id,actor_key));
        CREATE INDEX credential_maintenance_observation_scope ON credential_maintenance_observation_requests(customer_id,state,expires,project_id,environment_id);
        CREATE INDEX credential_maintenance_observation_original ON credential_maintenance_observation_requests(customer_id,original_intent_id,original_admission_id);
        CREATE UNIQUE INDEX credential_maintenance_observation_receipt_id ON credential_maintenance_observation_receipts(customer_id,id);
        CREATE TRIGGER credential_maintenance_observation_requests_immutable BEFORE UPDATE OF customer_id,id,project_id,environment_id,
          original_intent_id,original_intent_digest,original_admission_id,expected_after_sha256,observation,observation_sha256,
          requester,requester_version,policy_version,database_instance_id,purpose_sha256,expires,digest,created ON credential_maintenance_observation_requests
          BEGIN SELECT RAISE(ABORT,'immutable observation request'); END;
        CREATE TRIGGER credential_maintenance_observation_requests_no_delete BEFORE DELETE ON credential_maintenance_observation_requests
          BEGIN SELECT RAISE(ABORT,'retained observation request'); END;
        CREATE TRIGGER credential_maintenance_observation_recorded_immutable BEFORE UPDATE ON credential_maintenance_observation_requests
          WHEN EXISTS(SELECT 1 FROM credential_maintenance_observation_receipts r WHERE r.customer_id=OLD.customer_id AND r.request_id=OLD.id)
          BEGIN SELECT RAISE(ABORT,'immutable recorded observation request'); END;
        CREATE TRIGGER credential_maintenance_observation_approvals_no_update BEFORE UPDATE ON credential_maintenance_observation_approvals
          BEGIN SELECT RAISE(ABORT,'immutable observation approval'); END;
        CREATE TRIGGER credential_maintenance_observation_approvals_no_delete BEFORE DELETE ON credential_maintenance_observation_approvals
          BEGIN SELECT RAISE(ABORT,'retained observation approval'); END;
        CREATE TRIGGER credential_maintenance_observation_receipts_no_update BEFORE UPDATE ON credential_maintenance_observation_receipts
          BEGIN SELECT RAISE(ABORT,'immutable published observation'); END;
        CREATE TRIGGER credential_maintenance_observation_receipts_no_delete BEFORE DELETE ON credential_maintenance_observation_receipts
          BEGIN SELECT RAISE(ABORT,'retained published observation'); END;
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
        if (history.Count < 4 || history[3] != (4, SchemaV4Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='worker_heartbeats'") != 1 ||
            Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='worker_heartbeat_health'") != 1)
            throw new FoundationStoreException("MigrationSchemaMismatch");
        if (version <= 4)
        {
            if (history.Count != 4) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed");
            Exec(db, tx, SchemaV5);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(5,$checksum,$now); UPDATE cell SET schema_version=5 WHERE singleton=1",
                ("$checksum", SchemaV5Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            {
                var scope = BootstrapScope(db, tx);
                Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId,
                    "Succeeded", "SchemaV4ToV5", SchemaV5Checksum);
            }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 5 || history[4] != (5, SchemaV5Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        foreach (var trigger in new[] { "cell_instance_insert_guard", "cell_instance_update_guard" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name", ("$name", trigger)) != 1)
                throw new FoundationStoreException("MigrationSchemaMismatch");
        var instance = ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1");
        if (!ValidDatabaseInstanceId(instance)) throw new FoundationStoreException("MigrationSchemaMismatch");
        if (version <= 5)
        {
            if (history.Count != 5) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV6);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(6,$checksum,$now); UPDATE cell SET schema_version=6 WHERE singleton=1", ("$checksum", SchemaV6Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV5ToV6", SchemaV6Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 6 || history[5] != (6, SchemaV6Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialMaintenanceSchema(db, tx);
        if (version <= 6)
        {
            if (history.Count != 6) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV7);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(7,$checksum,$now); UPDATE cell SET schema_version=7 WHERE singleton=1", ("$checksum", SchemaV7Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV6ToV7", SchemaV7Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 7 || history[6] != (7, SchemaV7Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialMaintenanceObservationSchema(db, tx);
        if (version <= 7)
        {
            if (history.Count != 7) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV8);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(8,$checksum,$now); UPDATE cell SET schema_version=8 WHERE singleton=1",
                ("$checksum", SchemaV8Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV7ToV8", SchemaV8Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 8 || history[7] != (8, SchemaV8Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialRecoveryReviewSchema(db, tx);
        if (version <= 8)
        {
            if (history.Count != 8) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV9);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(9,$checksum,$now); UPDATE cell SET schema_version=9 WHERE singleton=1",
                ("$checksum", SchemaV9Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV8ToV9", SchemaV9Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 9 || history[8] != (9, SchemaV9Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialScopedRecoveryReviewSchema(db, tx);
        if (version <= 9)
        {
            if (history.Count != 9) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV10);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(10,$checksum,$now); UPDATE cell SET schema_version=10 WHERE singleton=1",
                ("$checksum", SchemaV10Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV9ToV10", SchemaV10Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 10 || history[9] != (10, SchemaV10Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialScopedRecoveryJointSchema(db, tx);
        if (version <= 10)
        {
            if (history.Count != 10) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV11);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(11,$checksum,$now); UPDATE cell SET schema_version=11 WHERE singleton=1",
                ("$checksum", SchemaV11Checksum), ("$now", Now));
            BackfillLegacyRecoveryExecutionHold(db, tx);
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV10ToV11", SchemaV11Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 11 || history[10] != (11, SchemaV11Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateRecoveryExecutionHoldSchema(db, tx);
        if (version <= 11)
        {
            if (history.Count != 11) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); Exec(db, tx, SchemaV12);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(12,$checksum,$now); UPDATE cell SET schema_version=12 WHERE singleton=1",
                ("$checksum", SchemaV12Checksum), ("$now", Now));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV11ToV12", SchemaV12Checksum); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count < 12 || history[11] != (12, SchemaV12Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialRuntimeBindingSchema(db, tx);
        if (version <= 12)
        {
            if (history.Count != 12) throw new FoundationStoreException("MigrationHistoryMismatch");
            if (ScalarText(db, tx, "PRAGMA quick_check") != "ok" || Rows(db, tx, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
                throw new FoundationStoreException("MigrationIntegrityFailed");
            ValidateAdministrativeAuditChain(db, tx, "MigrationIntegrityFailed"); var migratedAt = At(Now); Exec(db, tx, SchemaV13);
            Exec(db, tx, "INSERT INTO schema_migrations VALUES(13,$checksum,$now); UPDATE cell SET schema_version=13 WHERE singleton=1",
                ("$checksum", SchemaV13Checksum), ("$now", migratedAt.ToUnixTimeMilliseconds()));
            if (Number(db, tx, "SELECT count(*) FROM actors") > 0)
            { var scope = BootstrapScope(db, tx); RestrictedRecoveryAuditAt(db, tx, "cell-maintenance", scope.Project, scope.Environment, "SchemaMigrated", CellId, "Succeeded", "SchemaV12ToV13", SchemaV13Checksum, migratedAt); }
        }
        history = Rows(db, tx, "SELECT version,checksum FROM schema_migrations ORDER BY version", r => (Version: r.GetInt32(0), Checksum: r.GetString(1)));
        if (history.Count != 13 || history[12] != (13, SchemaV13Checksum)) throw new FoundationStoreException("MigrationHistoryMismatch");
        ValidateCredentialRestrictedRecoveryReviewSchema(db, tx);
    }

    static void ValidateCredentialMaintenanceObservationSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=7 AND checksum=$checksum", ("$checksum", SchemaV7Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        foreach (var name in new[] { "credential_maintenance_observation_requests", "credential_maintenance_observation_approvals", "credential_maintenance_observation_receipts" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
        foreach (var name in new[] { "credential_maintenance_observation_scope", "credential_maintenance_observation_original", "credential_maintenance_observation_receipt_id" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
        foreach (var name in new[] { "credential_maintenance_observation_requests_immutable", "credential_maintenance_observation_requests_no_delete", "credential_maintenance_observation_recorded_immutable",
            "credential_maintenance_observation_approvals_no_update", "credential_maintenance_observation_approvals_no_delete", "credential_maintenance_observation_receipts_no_update", "credential_maintenance_observation_receipts_no_delete" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
    }

    static void ValidateCredentialMaintenanceSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=6 AND checksum=$checksum", ("$checksum", SchemaV6Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        foreach (var name in new[] { "credential_maintenance_intents", "credential_maintenance_approvals", "credential_maintenance_executions" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
        foreach (var name in new[] { "credential_maintenance_scope", "credential_maintenance_admission", "credential_maintenance_execution_state" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
        foreach (var name in new[] { "credential_maintenance_intents_immutable", "credential_maintenance_intents_no_delete", "credential_maintenance_approvals_no_update",
            "credential_maintenance_approvals_no_delete", "credential_maintenance_execution_immutable", "credential_maintenance_success_immutable", "credential_maintenance_executions_no_delete" })
            if (Number(db, tx, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=$name", ("$name", name)) != 1) throw new FoundationStoreException("MigrationSchemaMismatch");
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
    static string SchemaV5Checksum => Hash(SchemaV5.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV6Checksum => Hash(SchemaV6.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV7Checksum => Hash(SchemaV7.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV8Checksum => Hash(SchemaV8.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV9Checksum => Hash(SchemaV9.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV10Checksum => Hash(SchemaV10.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV11Checksum => Hash(SchemaV11.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV12Checksum => Hash(SchemaV12.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string SchemaV13Checksum => Hash(SchemaV13.Replace("\r\n", "\n", StringComparison.Ordinal));
    static string ScalarText(SqliteConnection db, SqliteTransaction? tx, string sql)
    { using var command = Command(db, tx, sql); return command.ExecuteScalar()?.ToString() ?? ""; }

    public FoundationSchemaStatus SchemaStatus()
    {
        using var db = Open();
        var applied = Rows(db, null, "SELECT checksum FROM schema_migrations ORDER BY version", r => r.GetString(0));
        return new((int)Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1"), CurrentSchemaVersion, applied.ToArray());
    }
}
