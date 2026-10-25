using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationCellInstanceIdentity(string CustomerId, string CellId, string DatabaseInstanceId);

public sealed partial class FoundationStore
{
    static bool ValidDatabaseInstanceId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public FoundationCellInstanceIdentity GetCellInstanceIdentity()
    {
        using var db = Open();
        return ReadInstanceIdentity(db, null, CustomerId, CellId);
    }

    // Trusted startup preflight: read-only, current schema only, no creation/migration or role bootstrap.
    public static FoundationCellInstanceIdentity ReadCellInstanceIdentity(string databasePath, string expectedCustomerId, string expectedCellId)
    {
        ValidateId(expectedCustomerId); ValidateId(expectedCellId);
        var path = Path.GetFullPath(databasePath); RejectLinks(path);
        if (!File.Exists(path)) throw new FoundationStoreException("ExistingCellRequired");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var tx = db.BeginTransaction(deferred: true);
        var result = ReadInstanceIdentity(db, tx, expectedCustomerId, expectedCellId); tx.Commit(); return result;
    }

    static FoundationCellInstanceIdentity ReadInstanceIdentity(SqliteConnection db, SqliteTransaction? tx, string customerId, string cellId)
    {
        var binding = Rows(db, tx, "SELECT schema_version,customer_id,cell_id FROM cell WHERE singleton=1",
            r => (Version: r.GetInt32(0), Customer: r.GetString(1), Cell: r.GetString(2))).SingleOrDefault();
        if (binding.Customer != customerId || binding.Cell != cellId) throw new FoundationStoreException("CellBindingMismatch");
        if (binding.Version != CurrentSchemaVersion) throw new FoundationStoreException("CurrentSchemaRequired");
        var instance = ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1");
        if (!ValidDatabaseInstanceId(instance)) throw new FoundationStoreException("InvalidDatabaseInstanceIdentity");
        return new(customerId, cellId, instance);
    }

    void AssertActivation(SqliteConnection db, SqliteTransaction? tx)
    {
        if (activationGuard is null) return;
        var instance = ReadInstanceIdentity(db, tx, CustomerId, CellId);
        activationGuard(databasePath, instance.DatabaseInstanceId);
    }
}
