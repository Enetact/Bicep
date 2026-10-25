using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationStoreBackupTests
{
    [Fact]
    public void Online_snapshot_restores_approved_work_artifacts_and_audit_to_a_fresh_database()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "parameterized mock"u8.ToArray());
        var backupPath = f.Path + ".backup"; var restorePath = f.Path + ".restored";
        try
        {
            var snapshot = f.Store.CreateBackup(backupPath);
            f.Store.CancelJob(f.Customer, "operator", job.Id);
            var restored = FoundationStore.RestoreBackup(snapshot, restorePath, f.Clock);
            Assert.Equal("OutcomeUnknown", restored.GetJob(f.Customer, "operator", job.Id).State);
            Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(f.Customer, "recovered-worker", TimeSpan.FromMinutes(1))).Code);
            Assert.Equal(artifact.Sha256, restored.GetArtifact(f.Customer, "viewer", artifact.Id).Sha256);
            Assert.True(restored.VerifyAuditIntegrity(f.Customer, "auditor"));
            Assert.Contains(restored.ListAudit(f.Customer, "auditor"), e => e.Action == "BackupRestored" && e.CorrelationId == snapshot.Sha256);
            Assert.Single(restored.PendingOutbox(f.Customer));
            Assert.Equal("BackupDestinationExists", Assert.Throws<FoundationStoreException>(() => f.Store.CreateBackup(backupPath)).Code);
            Assert.Equal("BackupDestinationExists", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, restorePath, f.Clock)).Code);
        }
        finally { Delete(backupPath); Delete(restorePath); }
    }

    [Fact]
    public void Restore_rejects_changed_manifest_customer_or_corrupted_snapshot()
    {
        using var f = new StoreFixture(); var path = f.Path + ".backup"; var target = f.Path + ".restored";
        try
        {
            var backup = f.Store.CreateBackup(path);
            Assert.Equal("BackupIntegrityFailed", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup with { CustomerId = "other" }, target, f.Clock)).Code);
            Assert.False(File.Exists(target));
            using (var changed = new FileStream(path, FileMode.Open, FileAccess.Write)) { changed.Position = 200; changed.WriteByte(0x7f); changed.Flush(true); }
            Assert.Equal("BackupIntegrityFailed", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup, target, f.Clock)).Code);
            Assert.False(File.Exists(target));
        }
        finally { Delete(path); Delete(target); }
    }

    static void Delete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
}
