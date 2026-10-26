using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const string SchemaV13 = """
        CREATE TABLE credential_restricted_recovery_stage_review_requests(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,
          environment_id TEXT NOT NULL,target TEXT NOT NULL,target_sha256 TEXT NOT NULL,requester TEXT NOT NULL,
          requester_version INTEGER NOT NULL CHECK(requester_version>0),requester_expires INTEGER NOT NULL,policy_version INTEGER NOT NULL CHECK(policy_version>0),
          database_instance_id TEXT NOT NULL,purpose_sha256 TEXT NOT NULL,expires INTEGER NOT NULL,digest TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('Prepared','Approved','Quarantined')),created INTEGER NOT NULL,
          CHECK(expires>created AND requester_expires>=expires),PRIMARY KEY(customer_id,id),
          FOREIGN KEY(customer_id,requester) REFERENCES actors(customer_id,actor_key),
          FOREIGN KEY(customer_id,project_id,environment_id) REFERENCES scopes(customer_id,project_id,environment_id));
        CREATE TABLE credential_restricted_recovery_stage_review_approvals(customer_id TEXT NOT NULL,request_id TEXT NOT NULL,actor_key TEXT NOT NULL,
          actor_version INTEGER NOT NULL CHECK(actor_version>0),context_expires INTEGER NOT NULL,digest TEXT NOT NULL,created INTEGER NOT NULL,
          PRIMARY KEY(customer_id,request_id),FOREIGN KEY(customer_id,request_id) REFERENCES credential_restricted_recovery_stage_review_requests(customer_id,id),
          FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key));
        CREATE TABLE credential_restricted_recovery_stage_review_executions(customer_id TEXT NOT NULL,request_id TEXT NOT NULL,admission_id TEXT NOT NULL,
          executor_key TEXT NOT NULL,executor_version INTEGER NOT NULL CHECK(executor_version>0),database_instance_id TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('OutcomeUnknown','Succeeded')),reason TEXT NOT NULL,result_json TEXT,result_sha256 TEXT,
          admitted INTEGER NOT NULL,completed INTEGER,updated INTEGER NOT NULL,
          CHECK((state='OutcomeUnknown' AND result_json IS NULL AND result_sha256 IS NULL AND completed IS NULL) OR
            (state='Succeeded' AND result_json IS NOT NULL AND result_sha256 IS NOT NULL AND completed IS NOT NULL)),
          PRIMARY KEY(customer_id,request_id),FOREIGN KEY(customer_id,request_id) REFERENCES credential_restricted_recovery_stage_review_requests(customer_id,id),
          FOREIGN KEY(customer_id,executor_key) REFERENCES actors(customer_id,actor_key));
        CREATE INDEX credential_restricted_recovery_review_scope ON credential_restricted_recovery_stage_review_requests(customer_id,state,expires,project_id,environment_id);
        CREATE UNIQUE INDEX credential_restricted_recovery_review_admission ON credential_restricted_recovery_stage_review_executions(customer_id,admission_id);
        CREATE INDEX credential_restricted_recovery_review_execution_state ON credential_restricted_recovery_stage_review_executions(customer_id,state,admitted);
        CREATE TRIGGER credential_restricted_recovery_review_requests_immutable BEFORE UPDATE OF customer_id,id,project_id,environment_id,target,target_sha256,
          requester,requester_version,requester_expires,policy_version,database_instance_id,purpose_sha256,expires,digest,created ON credential_restricted_recovery_stage_review_requests
          BEGIN SELECT RAISE(ABORT,'immutable recovery review'); END;
        CREATE TRIGGER credential_restricted_recovery_review_request_state BEFORE UPDATE OF state ON credential_restricted_recovery_stage_review_requests
          WHEN NOT ((OLD.state='Prepared' AND NEW.state='Approved') OR (OLD.state IN ('Prepared','Approved') AND NEW.state='Quarantined'))
          BEGIN SELECT RAISE(ABORT,'invalid recovery review transition'); END;
        CREATE TRIGGER credential_restricted_recovery_review_requests_no_delete BEFORE DELETE ON credential_restricted_recovery_stage_review_requests
          BEGIN SELECT RAISE(ABORT,'retained recovery review'); END;
        CREATE TRIGGER credential_restricted_recovery_review_approvals_no_update BEFORE UPDATE ON credential_restricted_recovery_stage_review_approvals
          BEGIN SELECT RAISE(ABORT,'immutable recovery approval'); END;
        CREATE TRIGGER credential_restricted_recovery_review_approvals_no_delete BEFORE DELETE ON credential_restricted_recovery_stage_review_approvals
          BEGIN SELECT RAISE(ABORT,'retained recovery approval'); END;
        CREATE TRIGGER credential_restricted_recovery_review_execution_immutable BEFORE UPDATE OF customer_id,request_id,admission_id,executor_key,
          executor_version,database_instance_id,admitted ON credential_restricted_recovery_stage_review_executions
          BEGIN SELECT RAISE(ABORT,'immutable recovery admission'); END;
        CREATE TRIGGER credential_restricted_recovery_review_execution_state_guard BEFORE UPDATE OF state ON credential_restricted_recovery_stage_review_executions
          WHEN OLD.state!='OutcomeUnknown' OR NEW.state NOT IN ('OutcomeUnknown','Succeeded')
          BEGIN SELECT RAISE(ABORT,'invalid recovery execution transition'); END;
        CREATE TRIGGER credential_restricted_recovery_review_success_immutable BEFORE UPDATE ON credential_restricted_recovery_stage_review_executions WHEN OLD.state='Succeeded'
          BEGIN SELECT RAISE(ABORT,'immutable recovery verification'); END;
        CREATE TRIGGER credential_restricted_recovery_review_executions_no_delete BEFORE DELETE ON credential_restricted_recovery_stage_review_executions
          BEGIN SELECT RAISE(ABORT,'retained recovery execution'); END;
        """;

    static void ValidateCredentialRestrictedRecoveryReviewSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=13 AND checksum=$checksum", ("$checksum", SchemaV13Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False"); reference.Open(); Exec(reference, null, SchemaV13);
        var expected = SnapshotSchemaObjects(reference); var actual = SnapshotSchemaObjects(db, tx);
        foreach (var item in expected.Values)
            if (!actual.TryGetValue(item.Name, out var observed) || observed.Type != item.Type || observed.Table != item.Table || NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(item.Sql))
                throw new FoundationStoreException("MigrationSchemaMismatch");
    }
}
