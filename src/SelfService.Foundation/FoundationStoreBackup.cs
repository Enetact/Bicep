using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationBackup(string DatabasePath, string CustomerId, string CellId, string Sha256,
    long Length, DateTimeOffset CreatedAt);

public sealed partial class FoundationStore
{
    // Cell-owner operational APIs. These are deliberately not exposed as customer HTTP commands.
    // Protect the resulting manifest independently from the database and its backup.
    public FoundationBackup CreateBackup(string destinationPath)
    {
        var destination = NewSnapshotPath(destinationPath);
        try
        {
            using (var source = Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                target.Open(); source.BackupDatabase(target);
                // Produce a standalone snapshot, with no dependency on a copied WAL file.
                Exec(target, null, "PRAGMA journal_mode=DELETE;");
                ValidateSnapshot(target, CellId, CustomerId);
            }
            using var file = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = new FoundationBackup(destination, CustomerId, CellId, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), file.Length, clock.GetUtcNow());
            MaintenanceAudit("BackupCreated", result.Sha256); return result;
        }
        catch
        {
            DeleteFailedSnapshot(destination); throw;
        }
    }

    public static FoundationStore RestoreBackup(FoundationBackup backup, string destinationPath, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ValidateId(backup.CellId); ValidateId(backup.CustomerId);
        if (backup.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(backup.Sha256, "\\A[a-f0-9]{64}\\z") || backup.Length <= 0)
            throw new FoundationStoreException("InvalidBackupManifest", 400);
        var sourcePath = Path.GetFullPath(backup.DatabasePath); RejectLinks(sourcePath);
        // Keeping this handle open excludes a concurrent writer during hash verification and copy on Windows.
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != backup.Length || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != backup.Sha256)
            throw new FoundationStoreException("BackupIntegrityFailed");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        { source.Open(); ValidateSnapshot(source, backup.CellId, backup.CustomerId); }
        var destination = NewSnapshotPath(destinationPath);
        try
        {
            input.Position = 0;
            using (var output = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(flushToDisk: true); }
            var restored = new FoundationStore(destination, backup.CellId, backup.CustomerId, timeProvider);
            restored.HoldRecoveredWork(backup.Sha256); return restored;
        }
        catch { DeleteFailedSnapshot(destination); throw; }
    }

    static string NewSnapshotPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FoundationStoreException("InvalidBackupPath", 400);
        var path = Path.GetFullPath(value); RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); RejectLinks(path);
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix)))
            throw new FoundationStoreException("BackupDestinationExists");
        try { using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); created.Flush(flushToDisk: true); }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { throw new FoundationStoreException("BackupDestinationExists"); }
        return path;
    }

    void MaintenanceAudit(string action, string digest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var scope = Rows(db, tx, "SELECT project_id,environment_id FROM audit WHERE action='CustomerBootstrapped' ORDER BY sequence LIMIT 1",
            r => (Project: r.GetString(0), Environment: r.GetString(1))).SingleOrDefault();
        Audit(db, tx, "cell-maintenance", scope.Project ?? "default", scope.Environment ?? "dev", action, CellId,
            "Succeeded", "SnapshotVerified", digest); tx.Commit();
    }

    void HoldRecoveredWork(string backupDigest)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        // Effects may have happened after the snapshot. A restored queue is never safe to replay automatically.
        Exec(db, tx, "UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
        var ids = Rows(db, tx, "SELECT id FROM jobs WHERE customer_id=$c AND state IN ('Queued','Claimed','Dispatched')",
            r => r.GetString(0), ("$c", CustomerId));
        foreach (var id in ids)
        {
            var job = ReadJob(db, tx, id);
            Exec(db, tx, """
                UPDATE jobs SET state='OutcomeUnknown',worker_id=NULL,lease_until=NULL,fence=fence+1,
                  reason='RecoveryRequiresReconciliation',updated=$now WHERE customer_id=$c AND id=$id
                """, ("$now", Now), ("$c", CustomerId), ("$id", id));
            Audit(db, tx, "cell-maintenance", job.ProjectId, job.EnvironmentId, "RecoveredJobHeld", id,
                "OutcomeUnknown", "RecoveryRequiresReconciliation", job.PlanId);
        }
        var scope = Rows(db, tx, "SELECT project_id,environment_id FROM audit WHERE action='CustomerBootstrapped' ORDER BY sequence LIMIT 1",
            r => (Project: r.GetString(0), Environment: r.GetString(1))).SingleOrDefault();
        Audit(db, tx, "cell-maintenance", scope.Project ?? "default", scope.Environment ?? "dev", "BackupRestored", CellId,
            "Succeeded", "RecoveryPolicyChanged", backupDigest); tx.Commit();
    }

    static void DeleteFailedSnapshot(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    static void ValidateSnapshot(SqliteConnection db, string cellId, string customerId)
    {
        var binding = Rows(db, null, "SELECT schema_version,cell_id,customer_id FROM cell WHERE singleton=1",
            r => (Version: r.GetInt64(0), Cell: r.GetString(1), Customer: r.GetString(2))).SingleOrDefault();
        using var check = Command(db, null, "PRAGMA quick_check;");
        if (binding.Version is < 1 or > CurrentSchemaVersion || binding.Cell != cellId || binding.Customer != customerId ||
            check.ExecuteScalar()?.ToString() != "ok" || Rows(db, null, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
            throw new FoundationStoreException("BackupIntegrityFailed");
        var rows = Rows(db, null, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit ORDER BY sequence LIMIT 100001
            """, AuditRow);
        if (rows.Count > 100000) throw new FoundationStoreException("AuditVerificationLimit");
        var previous = ""; long sequence = 0;
        foreach (var row in rows)
        {
            if (row.CustomerId != customerId || row.CellId != cellId || row.Sequence != ++sequence || row.PreviousHash != previous ||
                row.EventHash != AuditHash(row.Sequence, row.CustomerId, row.CellId, row.EventId, row.ActorKey, row.ProjectId,
                    row.EnvironmentId, row.Action, row.TargetId, row.Outcome, row.Reason, row.CorrelationId,
                    row.CreatedAt.ToUnixTimeMilliseconds(), row.PreviousHash)) throw new FoundationStoreException("BackupIntegrityFailed");
            previous = row.EventHash;
        }
    }
}
