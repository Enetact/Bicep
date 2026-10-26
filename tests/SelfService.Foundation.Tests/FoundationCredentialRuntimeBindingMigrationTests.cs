using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialRuntimeBindingMigrationTests
{
    [Fact]
    public void Genuine_v11_upgrade_preserves_prior_history_jobs_instance_and_adds_no_implicit_binding()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var instance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        Downgrade(f); var history = History(f); var bytes = File.ReadAllBytes(f.Path);
        Assert.Equal(11, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); var upgraded = f.Reopen();
        Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version); Assert.Equal(history, History(f).Take(11));
        Assert.Equal(instance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_runtime_bindings")); Assert.False(upgraded.GetCredentialRuntimeBindingStatus().BindingRequired);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV11ToV12'"));
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("audit")]
    public void V12_upgrade_failure_rolls_back_schema_history_and_all_audit_bytes(string fault)
    {
        using var f = new StoreFixture(); Downgrade(f); var history = History(f); var audit = f.Scalar("SELECT count(*) FROM audit");
        if (fault == "table") f.Execute("CREATE TABLE credential_runtime_bindings(unknown TEXT)");
        else f.Execute("CREATE TRIGGER synthetic_binding_migration_fault BEFORE INSERT ON audit WHEN NEW.reason='SchemaV11ToV12' BEGIN SELECT RAISE(ABORT,'synthetic unavailable audit'); END");
        Assert.Throws<SqliteException>(() => f.Reopen());
        Assert.Equal(11, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(history, History(f)); Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_runtime_bindings_no_update'"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("trigger")]
    public void Missing_current_v12_schema_is_refused_without_repair(string kind)
    {
        using var f = new StoreFixture();
        f.Execute(kind == "table" ? "DROP TABLE credential_runtime_bindings" : "DROP TRIGGER credential_runtime_bindings_no_delete");
        var before = File.ReadAllBytes(f.Path); Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(before, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void Rewritten_v12_checksum_is_refused_without_overwriting_history()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=12; CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=12 AND checksum='modified'"));
    }

    static string[] History(StoreFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(reader.GetString(0)); return rows.ToArray();
    }
    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 11);
        f.Execute("DROP TRIGGER schema_migrations_no_delete;DELETE FROM schema_migrations WHERE version>11;UPDATE cell SET schema_version=11;CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
    }
}
