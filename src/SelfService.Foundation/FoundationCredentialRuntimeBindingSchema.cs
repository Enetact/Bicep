using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const string SchemaV12 = """
        CREATE TABLE credential_runtime_bindings(customer_id TEXT NOT NULL,cell_id TEXT NOT NULL,database_instance_id TEXT NOT NULL,
          id TEXT NOT NULL,database_path_sha256 TEXT NOT NULL,namespace_json TEXT NOT NULL,activation_authority_json TEXT NOT NULL,
          activation_generation INTEGER NOT NULL CHECK(activation_generation>0),activation_binding_sha256 TEXT NOT NULL,
          created INTEGER NOT NULL,digest TEXT NOT NULL,
          PRIMARY KEY(customer_id,cell_id,database_instance_id),UNIQUE(customer_id,id));
        CREATE TRIGGER credential_runtime_bindings_no_update BEFORE UPDATE ON credential_runtime_bindings
          BEGIN SELECT RAISE(ABORT,'immutable credential runtime binding'); END;
        CREATE TRIGGER credential_runtime_bindings_no_delete BEFORE DELETE ON credential_runtime_bindings
          BEGIN SELECT RAISE(ABORT,'retained credential runtime binding'); END;
        """;

    static void ValidateCredentialRuntimeBindingSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=12 AND checksum=$sha", ("$sha", SchemaV12Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False"); reference.Open(); Exec(reference, null, SchemaV12);
        var actual = SnapshotSchemaObjects(db, tx);
        foreach (var expected in SnapshotSchemaObjects(reference).Values)
            if (!actual.TryGetValue(expected.Name, out var observed) || observed.Type != expected.Type || observed.Table != expected.Table ||
                NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(expected.Sql))
                throw new FoundationStoreException("MigrationSchemaMismatch");
        _ = ReadCredentialRuntimeBindings(db, tx);
    }
}
