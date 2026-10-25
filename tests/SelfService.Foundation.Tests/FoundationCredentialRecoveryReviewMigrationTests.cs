using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialRecoveryReviewMigrationTests
{
    [Fact]
    public void V7_upgrade_preserves_prior_checksums_instance_jobs_and_audit_without_implicit_current_runtime_upgrade()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var instance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        Downgrade(f); var history = History(f); var bytes = File.ReadAllBytes(f.Path);
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(7, f.Scalar("SELECT schema_version FROM cell"));
        var upgraded = f.Reopen();
        Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version);
        Assert.Equal(history, History(f).Take(7)); Assert.Equal(instance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=8"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV7ToV8'"));
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("audit")]
    public void V8_failure_rolls_back_new_schema_history_version_and_audit(string fault)
    {
        using var f = new StoreFixture(); Downgrade(f); var history = History(f); var audit = f.Scalar("SELECT count(*) FROM audit");
        if (fault == "table") f.Execute("CREATE TABLE credential_recovery_stage_review_requests(conflicting TEXT)");
        else f.Execute("CREATE TRIGGER synthetic_review_migration_fault BEFORE INSERT ON audit WHEN NEW.reason='SchemaV7ToV8' BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END");
        Assert.Throws<SqliteException>(() => f.Reopen());
        Assert.Equal(7, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(history, History(f));
        Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_recovery_stage_review_approvals'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_recovery_stage_review_executions'"));
    }

    [Theory]
    [InlineData("credential_recovery_stage_review_requests")]
    [InlineData("credential_recovery_stage_review_approvals")]
    [InlineData("credential_recovery_stage_review_executions")]
    public void Current_schema_missing_review_table_is_refused_without_repair(string table)
    {
        using var f = new StoreFixture(); f.Execute("DROP TABLE " + table);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='" + table + "'"));
    }

    [Fact]
    public void Rewritten_v8_checksum_is_refused_without_repair_or_migration()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=8; CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(8, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=8 AND checksum='modified'"));
    }

    static string[] History(StoreFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }

    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 7);
        f.Execute("""
            DROP TRIGGER schema_migrations_no_delete;
            DELETE FROM schema_migrations WHERE version>=8;
            UPDATE cell SET schema_version=7;
            CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
            """);
    }
}
