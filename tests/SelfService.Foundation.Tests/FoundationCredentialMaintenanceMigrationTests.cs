using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceMigrationTests
{
    [Fact]
    public void V5_upgrade_is_additive_preserves_prior_checksums_and_has_an_audited_atomic_history()
    {
        using var f = new StoreFixture(); var queued = f.Queue(); var instance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId; Downgrade(f);
        var prior = History(f);
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(5, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(prior, History(f));
        var upgraded = f.Reopen(); Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version); Assert.Equal(prior, History(f).Take(5));
        Assert.Equal(instance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal(queued.Id, upgraded.GetJob(f.Customer, "viewer", queued.Id).Id);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=6"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV5ToV6'"));
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("audit")]
    public void V6_migration_failure_preserves_v5_and_rolls_back_new_tables_and_history(string fault)
    {
        using var f = new StoreFixture(); Downgrade(f); var prior = History(f);
        if (fault == "table") f.Execute("CREATE TABLE credential_maintenance_intents(conflicting TEXT)");
        else f.Execute("CREATE TRIGGER synthetic_migration_fault BEFORE INSERT ON audit WHEN NEW.reason='SchemaV5ToV6' BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END");
        Assert.Throws<SqliteException>(() => f.Reopen()); Assert.Equal(5, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(prior, History(f));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_maintenance_approvals'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_maintenance_executions'"));
    }

    public static IEnumerable<object[]> RequiredObjects()
    {
        foreach (var table in new[] { "credential_maintenance_intents", "credential_maintenance_approvals", "credential_maintenance_executions" }) yield return ["TABLE", table];
        foreach (var index in new[] { "credential_maintenance_scope", "credential_maintenance_admission", "credential_maintenance_execution_state" }) yield return ["INDEX", index];
        foreach (var trigger in new[] { "credential_maintenance_intents_immutable", "credential_maintenance_intents_no_delete", "credential_maintenance_approvals_no_update",
            "credential_maintenance_approvals_no_delete", "credential_maintenance_execution_immutable", "credential_maintenance_success_immutable", "credential_maintenance_executions_no_delete" }) yield return ["TRIGGER", trigger];
    }
    [Theory]
    [MemberData(nameof(RequiredObjects))]
    public void Missing_maintenance_schema_objects_fail_both_current_schema_runtime_and_explicit_reopen_without_repair(string kind, string name)
    {
        using var f = new StoreFixture(); f.Execute("DROP " + kind + " " + name);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='" + name + "'"));
    }

    [Fact]
    public void Rewritten_v6_history_is_rejected_before_any_schema_repair()
    {
        using var f = new StoreFixture(); f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=6; CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(FoundationStore.CurrentSchemaVersion, f.Scalar("SELECT schema_version FROM cell"));
    }

    static string[] History(StoreFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values.ToArray();
    }
    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 5);
        f.Execute("""
        DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>=6; UPDATE cell SET schema_version=5;
        CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
        """);
    }
}
