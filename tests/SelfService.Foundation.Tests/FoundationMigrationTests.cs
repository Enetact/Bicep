using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationMigrationTests
{
    [Fact]
    public void V1_upgrade_is_transactional_preserves_data_and_records_immutable_history()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "// source"u8.ToArray());
        DowngradeToV1(f);
        var upgraded = f.Reopen(); var status = upgraded.SchemaStatus();
        Assert.Equal(FoundationStore.CurrentSchemaVersion, status.Version); Assert.Equal(FoundationStore.CurrentSchemaVersion, status.AppliedMigrations.Length);
        Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Equal(artifact.Sha256, upgraded.GetArtifact(f.Customer, "viewer", artifact.Id).Sha256);
        Assert.Contains(upgraded.ListAudit(f.Customer, "auditor"), e => e.Action == "SchemaMigrated");
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.Throws<SqliteException>(() => f.Execute("UPDATE schema_migrations SET checksum='rewritten'"));
        Assert.Throws<SqliteException>(() => f.Execute("DELETE FROM schema_migrations"));
        Assert.Equal(status, f.Reopen().SchemaStatus(), new SchemaComparer());
    }

    [Fact]
    public void Worker_store_open_never_creates_upgrades_or_repairs_a_cell()
    {
        using var f = new StoreFixture(); var missing = f.Path + ".missing";
        Assert.Equal("ExistingCellRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(missing, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.False(File.Exists(missing));
        var original = f.Store.SchemaStatus();
        _ = new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false);
        Assert.Equal(original, f.Store.SchemaStatus(), new SchemaComparer());
        f.Execute("DROP TABLE worker_heartbeats; DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version=4; UPDATE cell SET schema_version=3;");
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(3, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='worker_heartbeats'"));
        using var broken = new StoreFixture(); broken.Execute("DROP TABLE outbox");
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(broken.Path, broken.Cell, broken.Customer, initializeSchema: false)).Code);
        Assert.Equal(0, broken.Scalar("SELECT count(*) FROM sqlite_master WHERE name='outbox'"));
    }

    [Fact]
    public void V3_upgrade_preserves_data_and_rolls_back_on_worker_schema_conflict()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        f.Execute("DROP TABLE worker_heartbeats; DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version=4; UPDATE cell SET schema_version=3; CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        f.Execute("CREATE TABLE worker_heartbeats(conflicting TEXT)");
        Assert.Throws<SqliteException>(() => f.Reopen());
        Assert.Equal(3, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=4"));
        f.Execute("DROP TABLE worker_heartbeats");
        var upgraded = f.Reopen(); Assert.Equal(4, upgraded.SchemaStatus().Version);
        Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void Missing_worker_health_index_is_rejected_without_repair()
    {
        using var f = new StoreFixture(); f.Execute("DROP INDEX worker_heartbeat_health");
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
    }

    [Fact]
    public void Migration_failure_rolls_back_schema_version_and_partial_tables()
    {
        using var f = new StoreFixture(); DowngradeToV1(f);
        f.Execute("CREATE TABLE scopes(conflicting TEXT)");
        var tableCount = f.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table'");
        Assert.Throws<SqliteException>(() => f.Reopen());
        Assert.Equal(1, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(tableCount, f.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'"));
    }

    [Fact]
    public void Foreign_key_or_audit_corruption_prevents_migration()
    {
        using var fk = new StoreFixture(); _ = fk.Plan(); DowngradeToV1(fk); fk.Execute("PRAGMA foreign_keys=OFF; DELETE FROM connections");
        Assert.Equal("MigrationIntegrityFailed", Assert.Throws<FoundationStoreException>(() => fk.Reopen()).Code);
        Assert.Equal(1, fk.Scalar("SELECT schema_version FROM cell"));
        using var audit = new StoreFixture(); DowngradeToV1(audit);
        audit.Execute("DROP TRIGGER audit_no_update; UPDATE audit SET reason='tampered' WHERE sequence=1");
        Assert.Equal("MigrationIntegrityFailed", Assert.Throws<FoundationStoreException>(() => audit.Reopen()).Code);
        Assert.Equal(1, audit.Scalar("SELECT schema_version FROM cell"));
    }

    [Fact]
    public void Unsupported_version_and_wrong_customer_fail_before_schema_mutation()
    {
        using var f = new StoreFixture(); f.Execute("UPDATE cell SET schema_version=77");
        var count = f.Scalar("SELECT count(*) FROM sqlite_master");
        Assert.Equal("CellBindingMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(count, f.Scalar("SELECT count(*) FROM sqlite_master"));
        Assert.Equal(77, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal("CellBindingMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, "other", "other")).Code);
    }

    [Fact]
    public void Edited_migration_checksum_is_rejected_on_reopen()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='edited' WHERE version=2");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
    }

    [Fact]
    public void Missing_v2_tables_or_guards_fail_closed_without_silent_schema_repair()
    {
        using var table = new StoreFixture(); table.Execute("DROP TABLE scope_exports");
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => table.Reopen()).Code);
        using var trigger = new StoreFixture(); trigger.Execute("DROP TRIGGER administrative_approvals_no_delete");
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => trigger.Reopen()).Code);
    }

    [Fact]
    public void V1_snapshot_restores_into_current_schema_and_recovered_job_is_held()
    {
        using var f = new StoreFixture(); var job = f.Queue(); DowngradeToV1(f);
        var backupPath = f.Path + ".snapshot"; var restorePath = f.Path + ".recovered";
        try
        {
            var snapshot = f.Store.CreateBackup(backupPath);
            var restored = FoundationStore.RestoreBackup(snapshot, restorePath, f.Clock);
            Assert.Equal(FoundationStore.CurrentSchemaVersion, restored.SchemaStatus().Version);
            Assert.Equal("OutcomeUnknown", restored.GetJob(f.Customer, "viewer", job.Id).State);
            Assert.Null(restored.ClaimNext(f.Customer, "worker", TimeSpan.FromSeconds(10)));
        }
        finally
        {
            foreach (var path in new[] { backupPath, restorePath, restorePath + "-wal", restorePath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    static void DowngradeToV1(StoreFixture f) => f.Execute("""
        DROP TABLE worker_heartbeats; DROP TABLE support_grants; DROP TABLE schema_migrations; DROP TABLE scopes; DROP TABLE customer_lifecycle; DROP TABLE administrative_approvals; DROP TABLE administrative_plans;
        DROP TABLE scope_exports; DROP TABLE artifact_tombstones; UPDATE cell SET schema_version=1;
        """);

    sealed class SchemaComparer : IEqualityComparer<FoundationSchemaStatus>
    {
        public bool Equals(FoundationSchemaStatus? x, FoundationSchemaStatus? y) => x?.Version == y?.Version && x?.SupportedVersion == y?.SupportedVersion &&
            x is not null && y is not null && x.AppliedMigrations.SequenceEqual(y.AppliedMigrations);
        public int GetHashCode(FoundationSchemaStatus value) => value.Version;
    }
}
