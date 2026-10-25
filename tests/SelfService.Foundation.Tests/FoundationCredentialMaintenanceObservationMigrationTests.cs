using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceObservationMigrationTests
{
    [Fact]
    public void V6_upgrade_preserves_all_six_prior_checksums_instance_and_data_with_transactional_audit()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var instance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId; Downgrade(f); var prior = History(f);
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(6, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(prior, History(f));
        var upgraded = f.Reopen(); Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version); Assert.Equal(prior, History(f).Take(6));
        Assert.Equal(instance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=7"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV6ToV7'"));
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("audit")]
    public void New_observation_migration_failure_rolls_back_tables_history_version_and_audit(string fault)
    {
        using var f = new StoreFixture(); Downgrade(f); var prior = History(f); var audit = f.Scalar("SELECT count(*) FROM audit");
        if (fault == "table") f.Execute("CREATE TABLE credential_maintenance_observation_requests(conflicting TEXT)");
        else f.Execute("CREATE TRIGGER synthetic_observation_migration_fault BEFORE INSERT ON audit WHEN NEW.reason='SchemaV6ToV7' BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END");
        Assert.Throws<SqliteException>(() => f.Reopen()); Assert.Equal(6, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(prior, History(f)); Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_maintenance_observation_approvals'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_maintenance_observation_receipts'"));
    }

    public static IEnumerable<object[]> RequiredObjects()
    {
        foreach (var table in new[] { "credential_maintenance_observation_requests", "credential_maintenance_observation_approvals", "credential_maintenance_observation_receipts" }) yield return ["TABLE", table];
        foreach (var index in new[] { "credential_maintenance_observation_scope", "credential_maintenance_observation_original", "credential_maintenance_observation_receipt_id" }) yield return ["INDEX", index];
        foreach (var trigger in new[] { "credential_maintenance_observation_requests_immutable", "credential_maintenance_observation_requests_no_delete", "credential_maintenance_observation_recorded_immutable",
            "credential_maintenance_observation_approvals_no_update", "credential_maintenance_observation_approvals_no_delete", "credential_maintenance_observation_receipts_no_update", "credential_maintenance_observation_receipts_no_delete" }) yield return ["TRIGGER", trigger];
    }
    [Theory]
    [MemberData(nameof(RequiredObjects))]
    public void Missing_v7_objects_fail_current_runtime_and_explicit_reopen_without_repair(string kind, string name)
    {
        using var f = new StoreFixture(); f.Execute("DROP " + kind + " " + name);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='" + name + "'"));
    }

    [Fact]
    public void Rewritten_v7_checksum_is_rejected_before_any_schema_repair()
    {
        using var f = new StoreFixture(); f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=7; CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code); Assert.Equal(FoundationStore.CurrentSchemaVersion, f.Scalar("SELECT schema_version FROM cell"));
    }

    static string[] History(StoreFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values.ToArray();
    }
    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 6);
        f.Execute("""
            DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>=7; UPDATE cell SET schema_version=6;
            CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
            """);
    }
}
