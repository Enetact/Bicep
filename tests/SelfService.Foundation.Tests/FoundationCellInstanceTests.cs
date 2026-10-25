using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCellInstanceTests
{
    [Fact]
    public void Instance_identity_is_stable_across_reopen_and_read_only_preflight()
    {
        using var f = new StoreFixture(); var id = f.Store.GetCellInstanceIdentity();
        Assert.Matches("^[a-f0-9]{32}$", id.DatabaseInstanceId);
        Assert.Equal(id, f.Reopen().GetCellInstanceIdentity());
        Assert.Equal(id, FoundationStore.ReadCellInstanceIdentity(f.Path, f.Customer, f.Cell));
        Assert.Equal("CellBindingMismatch", Assert.Throws<FoundationStoreException>(() =>
            FoundationStore.ReadCellInstanceIdentity(f.Path, "other", f.Cell)).Code);
        var missing = f.Path + ".missing";
        Assert.Equal("ExistingCellRequired", Assert.Throws<FoundationStoreException>(() =>
            FoundationStore.ReadCellInstanceIdentity(missing, f.Customer, f.Cell)).Code); Assert.False(File.Exists(missing));
    }

    [Fact]
    public void V4_migration_preserves_history_and_state_while_assigning_a_new_identity_transactionally()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var history = f.Store.SchemaStatus().AppliedMigrations.Take(4).ToArray();
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 4);
        f.Execute("DROP TRIGGER cell_instance_insert_guard; DROP TRIGGER cell_instance_update_guard; ALTER TABLE cell DROP COLUMN database_instance_id; DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>=5; UPDATE cell SET schema_version=4; CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => FoundationStore.ReadCellInstanceIdentity(f.Path, f.Customer, f.Cell)).Code);
        f.Execute("CREATE TRIGGER cell_instance_insert_guard BEFORE UPDATE ON cell BEGIN SELECT 1; END;");
        Assert.Throws<SqliteException>(() => f.Reopen()); Assert.Equal(4, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM pragma_table_info('cell') WHERE name='database_instance_id'"));
        f.Execute("DROP TRIGGER cell_instance_insert_guard"); var upgraded = f.Reopen();
        Assert.Equal(history, upgraded.SchemaStatus().AppliedMigrations.Take(4).ToArray());
        Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version); Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Matches("^[a-f0-9]{32}$", upgraded.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void Restored_copy_gets_a_distinct_instance_and_retains_recovery_holds()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var source = f.Store.GetCellInstanceIdentity();
        var backup = f.Store.CreateBackup(f.Path + ".snapshot");
        var restored = FoundationStore.RestoreBackup(backup, f.Path + ".restored", f.Clock);
        try
        {
            Assert.NotEqual(source.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
            Assert.Equal(source, f.Store.GetCellInstanceIdentity());
            Assert.Equal("OutcomeUnknown", restored.GetJob(f.Customer, "viewer", job.Id).State);
            Assert.Null(restored.ClaimNext(f.Customer, "worker", TimeSpan.FromSeconds(10)));
        }
        finally { foreach (var path in new[] { backup.DatabasePath, f.Path + ".restored", f.Path + ".restored-wal", f.Path + ".restored-shm" }) if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Invalid_identity_is_rejected_and_runtime_guard_rechecks_inside_dispatch()
    {
        using var f = new StoreFixture();
        Assert.Throws<SqliteException>(() => f.Execute("UPDATE cell SET database_instance_id='invalid'"));
        var job = f.Queue(); int? denyAt = null; var calls = 0;
        var guarded = new FoundationStore(f.Path, f.Cell, f.Customer, f.Clock, initializeSchema: false,
            activationGuard: (path, instance) => { Assert.Equal(f.Path, path); Assert.Matches("^[a-f0-9]{32}$", instance); calls++; if (denyAt is not null && calls >= denyAt) throw new FoundationStoreException("ActivationLost", 403); });
        var claimed = guarded.ClaimNext(f.Customer, "worker", TimeSpan.FromSeconds(10))!; Assert.Equal(job.Id, claimed.Id);
        denyAt = calls + 2; // Opening succeeds; the independent check inside the dispatch transaction must still reject.
        Assert.Equal("ActivationLost", Assert.Throws<FoundationStoreException>(() => guarded.StartDispatch(f.Customer, job.Id, "worker", claimed.Fence)).Code);
        Assert.True(calls >= 4); Assert.Equal("Claimed", f.Store.GetJob(f.Customer, "viewer", job.Id).State);
        Assert.Throws<ArgumentException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, activationGuard: (_, _) => { }));
    }
}
