using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        RequireStandaloneSnapshot(sourcePath, input);
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
        Exec(db, tx, "UPDATE cell SET policy_version=policy_version+1,database_instance_id=$instance WHERE singleton=1", ("$instance", NewId()));
        var oldWorkers = Number(db, tx, "SELECT count(*) FROM worker_heartbeats WHERE customer_id=$c AND state!='Stopped'", ("$c", CustomerId));
        Exec(db, tx, "UPDATE worker_heartbeats SET state='Stopped',stopped=$now,generation=generation+1 WHERE customer_id=$c", ("$now", Now), ("$c", CustomerId));
        // A copied approval or unknown local effect cannot become authority to repeat credential maintenance.
        // Successful execution receipts and all existing admission/approval bytes remain historical evidence.
        var maintenance = Rows(db, tx, """
            SELECT i.id,i.project_id,i.environment_id,i.digest
            FROM credential_maintenance_intents i
            LEFT JOIN credential_maintenance_executions e ON e.customer_id=i.customer_id AND e.intent_id=i.id
            WHERE i.customer_id=$c AND i.state IN ('Prepared','Approved')
              AND (e.intent_id IS NULL OR e.state!='Succeeded')
            ORDER BY i.id LIMIT $limit
            """, r => (Id: r.GetString(0), Project: r.GetString(1), Environment: r.GetString(2), Digest: r.GetString(3)),
            ("$c", CustomerId), ("$limit", MaximumCredentialMaintenanceIntents + 1));
        if (maintenance.Count > MaximumCredentialMaintenanceIntents)
            throw new FoundationStoreException("CredentialMaintenanceRecoveryLimit");
        foreach (var intent in maintenance)
        {
            Exec(db, tx, "UPDATE credential_maintenance_intents SET state='Quarantined' WHERE customer_id=$c AND id=$id",
                ("$c", CustomerId), ("$id", intent.Id));
            Audit(db, tx, "cell-maintenance", intent.Project, intent.Environment, "RecoveredCredentialMaintenanceHeld", intent.Id,
                "OutcomeUnknown", "RecoveryRequiresReconciliation", intent.Digest);
        }
        var observations = Rows(db, tx, """
            SELECT q.id,q.project_id,q.environment_id,q.digest
            FROM credential_maintenance_observation_requests q
            LEFT JOIN credential_maintenance_observation_receipts r ON r.customer_id=q.customer_id AND r.request_id=q.id
            WHERE q.customer_id=$c AND q.state IN ('Prepared','Approved') AND r.request_id IS NULL
            ORDER BY q.id LIMIT $limit
            """, r => (Id: r.GetString(0), Project: r.GetString(1), Environment: r.GetString(2), Digest: r.GetString(3)),
            ("$c", CustomerId), ("$limit", MaximumCredentialMaintenanceObservationRequests + 1));
        if (observations.Count > MaximumCredentialMaintenanceObservationRequests)
            throw new FoundationStoreException("CredentialMaintenanceObservationRecoveryLimit");
        foreach (var observation in observations)
        {
            Exec(db, tx, "UPDATE credential_maintenance_observation_requests SET state='Quarantined' WHERE customer_id=$c AND id=$id",
                ("$c", CustomerId), ("$id", observation.Id));
            Audit(db, tx, "cell-maintenance", observation.Project, observation.Environment, "RecoveredCredentialMaintenanceObservationHeld", observation.Id,
                "OutcomeUnknown", "RecoveryRequiresReconciliation", observation.Digest);
        }
        HoldRecoveredCredentialRecoveryReviews(db, tx);
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
        if (oldWorkers > 0)
            Audit(db, tx, "cell-maintenance", scope.Project ?? "default", scope.Environment ?? "dev", "RecoveredWorkerRegistrationsHeld", CellId,
                "Succeeded", "RegistryFencedForNewDatabaseInstance", backupDigest);
        Audit(db, tx, "cell-maintenance", scope.Project ?? "default", scope.Environment ?? "dev", "BackupRestored", CellId,
            "Succeeded", "RecoveryPolicyChanged", backupDigest); tx.Commit();
    }

    static void DeleteFailedSnapshot(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    static void RequireStandaloneSnapshot(string path, FileStream input)
    {
        // Generated snapshots use DELETE mode. Uncommitted sidecars must never alter the SQL view of signed main-file bytes.
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix) || Directory.Exists(path + suffix)))
            throw new FoundationStoreException("BackupSnapshotNotStandalone");
        if (input.Length < 100) throw new FoundationStoreException("BackupIntegrityFailed");
        input.Position = 0; Span<byte> header = stackalloc byte[20]; input.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8) || header[18] != 1 || header[19] != 1)
            throw new FoundationStoreException("BackupSnapshotNotStandalone");
        input.Position = 0;
    }

    static void ValidateSnapshot(SqliteConnection db, string cellId, string customerId)
    {
        try { ValidateSnapshotCore(db, cellId, customerId); }
        catch (Exception error) when (error is SqliteException or InvalidCastException or InvalidOperationException or OverflowException or ArgumentException or FormatException)
        { throw new FoundationStoreException("BackupIntegrityFailed"); }
    }

    static void ValidateSnapshotCore(SqliteConnection db, string cellId, string customerId)
    {
        var binding = Rows(db, null, "SELECT schema_version,cell_id,customer_id FROM cell WHERE singleton=1",
            r => (Version: r.GetInt64(0), Cell: r.GetString(1), Customer: r.GetString(2))).SingleOrDefault();
        using var check = Command(db, null, "PRAGMA quick_check;");
        if (binding.Version is < 1 or > CurrentSchemaVersion || binding.Cell != cellId || binding.Customer != customerId ||
            check.ExecuteScalar()?.ToString() != "ok" || Rows(db, null, "PRAGMA foreign_key_check", r => r.GetString(0)).Count != 0)
            throw new FoundationStoreException("BackupIntegrityFailed");
        if (binding.Version >= 5 && !ValidDatabaseInstanceId(ScalarText(db, null, "SELECT database_instance_id FROM cell WHERE singleton=1")))
            throw new FoundationStoreException("BackupIntegrityFailed");
        if (binding.Version >= 6) ValidateCredentialMaintenanceSchema(db, null);
        if (binding.Version >= 7) ValidateCredentialMaintenanceObservationSchema(db, null);
        ValidateSnapshotSchema(db, checked((int)binding.Version));
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

    // Instantiate only our frozen DDL in a separate in-memory reference. Incoming snapshot connections remain read-only;
    // this does not invoke FoundationStore construction, migration, CREATE IF NOT EXISTS or repair against the packet.
    static SqliteConnection SnapshotSchemaReference(int version)
    {
        var reference = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = ":memory:", Mode = SqliteOpenMode.Memory, Pooling = false }.ToString());
        try
        {
            reference.Open(); Exec(reference, null, SchemaV1);
            if (version >= 2) Exec(reference, null, SchemaV2);
            if (version >= 3) Exec(reference, null, SchemaV3);
            if (version >= 4) Exec(reference, null, WorkerOperationsSchemaV4);
            if (version >= 5) Exec(reference, null, SchemaV5);
            if (version >= 6) Exec(reference, null, SchemaV6);
            if (version >= 7) Exec(reference, null, SchemaV7);
            if (version >= 8) Exec(reference, null, SchemaV8);
            return reference;
        }
        catch { reference.Dispose(); throw; }
    }

    sealed record SnapshotSchemaObject(string Type, string Name, string Table, string Sql);
    static Dictionary<string, SnapshotSchemaObject> SnapshotSchemaObjects(SqliteConnection db, SqliteTransaction? tx = null)
    {
        var size = Rows(db, tx, """
            SELECT count(*),coalesce(sum(length(cast(sql AS BLOB))),0),coalesce(max(length(cast(sql AS BLOB))),0) FROM sqlite_master
            """, r => (Count: r.GetInt64(0), Bytes: r.GetInt64(1), Largest: r.GetInt64(2))).Single();
        if (size.Count > 4096 || size.Bytes > 1048576 || size.Largest > 65536)
            throw new FoundationStoreException("BackupIntegrityFailed");
        var objects = Rows(db, tx, """
            SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY name LIMIT 4097
            """, r => new SnapshotSchemaObject(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
        if (objects.Count > 4096 || objects.Sum(o => (long)Encoding.UTF8.GetByteCount(o.Sql)) > 1048576 || objects.Any(o => o.Sql.Length > 65536))
            throw new FoundationStoreException("BackupIntegrityFailed");
        var names = new Dictionary<string, SnapshotSchemaObject>(StringComparer.Ordinal);
        foreach (var item in objects) if (!names.TryAdd(item.Name, item)) throw new FoundationStoreException("BackupIntegrityFailed");
        return names;
    }

    static void ValidateSnapshotSchema(SqliteConnection db, int version)
    {
        using var reference = SnapshotSchemaReference(version);
        var required = SnapshotSchemaObjects(reference); var actual = SnapshotSchemaObjects(db);
        // Unrecognized executable schema must not run when a staged copy migrates or recovery updates core rows.
        // Bounded ordinary extension tables/indexes are not a claim that every additional object or business row is attested.
        foreach (var extra in actual.Values.Where(o => !required.ContainsKey(o.Name)))
            if (extra.Type is not ("table" or "index") || extra.Type == "table" && !NormalizeSnapshotSql(extra.Sql).StartsWith("createtable", StringComparison.Ordinal))
                throw new FoundationStoreException("BackupIntegrityFailed");
        foreach (var expected in required.Values)
        {
            if (!actual.TryGetValue(expected.Name, out var observed) || observed.Type != expected.Type || observed.Table != expected.Table ||
                NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(expected.Sql)) throw new FoundationStoreException("BackupIntegrityFailed");
            if (expected.Type == "table")
            {
                // Column order, affinity/nullability/defaults/PK and foreign-key bindings remain structural requirements,
                // even if an incoming sqlite_master text was edited independently of the original object definition.
                foreach (var pragma in new[] { "table_xinfo", "foreign_key_list" })
                    if (SnapshotSchemaMetadata(db, pragma, expected.Name) != SnapshotSchemaMetadata(reference, pragma, expected.Name))
                        throw new FoundationStoreException("BackupIntegrityFailed");
                var expectedIndexes = Rows(reference, null, "PRAGMA index_list(" + QuoteSnapshotName(expected.Name) + ")",
                    r => (Name: r.GetString(1), Unique: r.GetInt64(2), Origin: r.GetString(3), Partial: r.GetInt64(4)));
                var actualIndexes = Rows(db, null, "PRAGMA index_list(" + QuoteSnapshotName(expected.Name) + ")",
                    r => (Name: r.GetString(1), Unique: r.GetInt64(2), Origin: r.GetString(3), Partial: r.GetInt64(4)));
                foreach (var index in expectedIndexes)
                    if (!actualIndexes.Contains(index) || SnapshotSchemaMetadata(db, "index_xinfo", index.Name) != SnapshotSchemaMetadata(reference, "index_xinfo", index.Name))
                        throw new FoundationStoreException("BackupIntegrityFailed");
            }
        }
        // Known later-version objects cannot masquerade as a valid legacy packet and collide with staged migration.
        if (version < CurrentSchemaVersion)
        {
            using var current = SnapshotSchemaReference(CurrentSchemaVersion);
            if (SnapshotSchemaObjects(current).Keys.Any(name => !required.ContainsKey(name) && actual.ContainsKey(name)))
                throw new FoundationStoreException("BackupIntegrityFailed");
        }
        if (version == 1)
        {
            if (actual.ContainsKey("schema_migrations")) throw new FoundationStoreException("BackupIntegrityFailed");
            return;
        }
        var checksums = new[] { Hash("platform-studio.foundation.schema.v1"), SchemaV2Checksum, SchemaV3Checksum,
            SchemaV4Checksum, SchemaV5Checksum, SchemaV6Checksum, SchemaV7Checksum, SchemaV8Checksum };
        var history = Rows(db, null, "SELECT version,checksum FROM schema_migrations ORDER BY version LIMIT 9", r => (Version: r.GetInt64(0), Checksum: r.GetString(1)));
        if (history.Count != version || history.Where((entry, i) => entry.Version != i + 1 || entry.Checksum != checksums[i]).Any())
            throw new FoundationStoreException("BackupIntegrityFailed");
    }

    static string QuoteSnapshotName(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    static string SnapshotSchemaMetadata(SqliteConnection db, string pragma, string name)
    {
        var rows = Rows(db, null, "PRAGMA " + pragma + "(" + QuoteSnapshotName(name) + ")", r =>
        {
            var cells = new object?[r.FieldCount]; for (var i = 0; i < cells.Length; i++) cells[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            return JsonSerializer.Serialize(cells, Json);
        });
        return string.Join("\n", rows.Order(StringComparer.Ordinal));
    }

    static string NormalizeSnapshotSql(string sql)
    {
        var text = new StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i]; if (char.IsWhiteSpace(c)) continue;
            if (c == '\'')
            {
                text.Append(c);
                while (++i < sql.Length)
                {
                    text.Append(sql[i]); if (sql[i] != '\'') continue;
                    if (i + 1 < sql.Length && sql[i + 1] == '\'') { text.Append(sql[++i]); continue; }
                    break;
                }
            }
            else if (c is '\"' or '`' or '[')
            {
                var end = c == '[' ? ']' : c; var identifier = new StringBuilder();
                while (++i < sql.Length && sql[i] != end) identifier.Append(sql[i]);
                if (identifier.Length == 0 || identifier.ToString().Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
                    throw new FoundationStoreException("BackupIntegrityFailed");
                text.Append(identifier.ToString().ToLowerInvariant());
            }
            else text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString();
    }
}
