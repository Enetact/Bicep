using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const string SchemaV15 = """
        CREATE TABLE credential_restricted_recovery_adoption_intents(customer_id TEXT NOT NULL,id TEXT NOT NULL,project_id TEXT NOT NULL,
          environment_id TEXT NOT NULL,target TEXT NOT NULL,target_sha256 TEXT NOT NULL,requester TEXT NOT NULL,
          requester_version INTEGER NOT NULL CHECK(requester_version>0),requester_expires INTEGER NOT NULL,policy_version INTEGER NOT NULL CHECK(policy_version>0),
          database_instance_id TEXT NOT NULL,purpose_sha256 TEXT NOT NULL,expires INTEGER NOT NULL,digest TEXT NOT NULL,
          state TEXT NOT NULL CHECK(state IN ('Prepared','Approved','Quarantined')),created INTEGER NOT NULL,
          verification_sha256 TEXT NOT NULL,verification_observed INTEGER NOT NULL,
          CHECK(expires>created AND requester_expires>=expires AND verification_observed<=created),PRIMARY KEY(customer_id,id),
          FOREIGN KEY(customer_id,requester) REFERENCES actors(customer_id,actor_key),
          FOREIGN KEY(customer_id,project_id,environment_id) REFERENCES scopes(customer_id,project_id,environment_id));
        CREATE TABLE credential_restricted_recovery_adoption_approvals(customer_id TEXT NOT NULL,request_id TEXT NOT NULL,actor_key TEXT NOT NULL,
          actor_version INTEGER NOT NULL CHECK(actor_version>0),context_expires INTEGER NOT NULL,digest TEXT NOT NULL,
          verification_sha256 TEXT NOT NULL,verification_observed INTEGER NOT NULL,created INTEGER NOT NULL,
          CHECK(context_expires>created AND verification_observed<=created),PRIMARY KEY(customer_id,request_id),
          FOREIGN KEY(customer_id,request_id) REFERENCES credential_restricted_recovery_adoption_intents(customer_id,id),
          FOREIGN KEY(customer_id,actor_key) REFERENCES actors(customer_id,actor_key));
        CREATE INDEX credential_restricted_recovery_adoption_pending ON credential_restricted_recovery_adoption_intents(customer_id,state,expires);
        CREATE TRIGGER credential_restricted_recovery_adoption_intent_immutable BEFORE UPDATE OF customer_id,id,project_id,environment_id,target,target_sha256,
          requester,requester_version,requester_expires,policy_version,database_instance_id,purpose_sha256,expires,digest,created,verification_sha256,verification_observed
          ON credential_restricted_recovery_adoption_intents BEGIN SELECT RAISE(ABORT,'immutable adoption intent'); END;
        CREATE TRIGGER credential_restricted_recovery_adoption_state_guard BEFORE UPDATE OF state ON credential_restricted_recovery_adoption_intents
          WHEN NOT ((OLD.state='Prepared' AND NEW.state='Approved') OR (OLD.state IN ('Prepared','Approved') AND NEW.state='Quarantined'))
          BEGIN SELECT RAISE(ABORT,'invalid adoption intent transition'); END;
        CREATE TRIGGER credential_restricted_recovery_adoption_no_delete BEFORE DELETE ON credential_restricted_recovery_adoption_intents
          BEGIN SELECT RAISE(ABORT,'retained adoption intent'); END;
        CREATE TRIGGER credential_restricted_recovery_adoption_approval_no_update BEFORE UPDATE ON credential_restricted_recovery_adoption_approvals
          BEGIN SELECT RAISE(ABORT,'immutable adoption intent approval'); END;
        CREATE TRIGGER credential_restricted_recovery_adoption_approval_no_delete BEFORE DELETE ON credential_restricted_recovery_adoption_approvals
          BEGIN SELECT RAISE(ABORT,'retained adoption intent approval'); END;
        """;

    static void ValidateCredentialRestrictedRecoveryAdoptionSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=15 AND checksum=$checksum", ("$checksum", SchemaV15Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False"); reference.Open(); Exec(reference, null, SchemaV15);
        var actual = SnapshotSchemaObjects(db, tx);
        foreach (var expected in SnapshotSchemaObjects(reference).Values)
            if (!actual.TryGetValue(expected.Name, out var observed) || observed.Type != expected.Type || observed.Table != expected.Table || NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(expected.Sql))
                throw new FoundationStoreException("MigrationSchemaMismatch");
    }
}
