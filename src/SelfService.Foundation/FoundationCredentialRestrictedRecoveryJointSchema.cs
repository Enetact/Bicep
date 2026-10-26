using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const string SchemaV14 = """
        CREATE UNIQUE INDEX credential_restricted_recovery_joint_parent ON credential_restricted_recovery_stage_review_executions(customer_id,request_id,admission_id);
        CREATE TABLE credential_restricted_recovery_joint_executions(customer_id TEXT NOT NULL,review_id TEXT NOT NULL,
          admission_id TEXT NOT NULL,target TEXT NOT NULL,target_sha256 TEXT NOT NULL,digest TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('OutcomeUnknown','Succeeded')),reason TEXT NOT NULL,result_json TEXT,result_sha256 TEXT,
          admitted INTEGER NOT NULL,completed INTEGER,updated INTEGER NOT NULL,
          CHECK((state='OutcomeUnknown' AND result_json IS NULL AND result_sha256 IS NULL AND completed IS NULL) OR
            (state='Succeeded' AND result_json IS NOT NULL AND result_sha256 IS NOT NULL AND completed IS NOT NULL)),
          PRIMARY KEY(customer_id,review_id),UNIQUE(customer_id,admission_id),
          FOREIGN KEY(customer_id,review_id,admission_id) REFERENCES credential_restricted_recovery_stage_review_executions(customer_id,request_id,admission_id));
        CREATE INDEX credential_restricted_recovery_joint_state ON credential_restricted_recovery_joint_executions(customer_id,state,admitted);
        CREATE TRIGGER credential_restricted_recovery_joint_immutable BEFORE UPDATE OF customer_id,review_id,admission_id,target,target_sha256,digest,admitted
          ON credential_restricted_recovery_joint_executions BEGIN SELECT RAISE(ABORT,'immutable joint verification admission'); END;
        CREATE TRIGGER credential_restricted_recovery_joint_state_guard BEFORE UPDATE OF state ON credential_restricted_recovery_joint_executions
          WHEN OLD.state!='OutcomeUnknown' OR NEW.state NOT IN ('OutcomeUnknown','Succeeded')
          BEGIN SELECT RAISE(ABORT,'invalid joint verification transition'); END;
        CREATE TRIGGER credential_restricted_recovery_joint_success_immutable BEFORE UPDATE ON credential_restricted_recovery_joint_executions WHEN OLD.state='Succeeded'
          BEGIN SELECT RAISE(ABORT,'immutable joint verification result'); END;
        CREATE TRIGGER credential_restricted_recovery_joint_no_delete BEFORE DELETE ON credential_restricted_recovery_joint_executions
          BEGIN SELECT RAISE(ABORT,'retained joint verification'); END;
        """;

    static void ValidateCredentialRestrictedRecoveryJointSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=14 AND checksum=$checksum", ("$checksum", SchemaV14Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False"); reference.Open(); Exec(reference, null, SchemaV13);
        var prior = SnapshotSchemaObjects(reference); Exec(reference, null, SchemaV14);
        var expected = SnapshotSchemaObjects(reference).Where(item => !prior.ContainsKey(item.Key)).Select(item => item.Value);
        var actual = SnapshotSchemaObjects(db, tx);
        foreach (var item in expected)
            if (!actual.TryGetValue(item.Name, out var observed) || observed.Type != item.Type || observed.Table != item.Table || NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(item.Sql))
                throw new FoundationStoreException("MigrationSchemaMismatch");
    }
}
