using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationRecoveryExecutionHold(string Id, string CustomerId, string CellId, string DatabaseInstanceId,
    string? SourceDatabaseInstanceId, string Origin, string BackupSha256, long PolicyVersion, DateTimeOffset CreatedAt, string Reason, string Sha256);

public sealed record FoundationRecoveryExecutionHoldStatus(string CustomerId, string CellId, string DatabaseInstanceId,
    bool ExecutionHeld, FoundationRecoveryExecutionHold? Hold)
{
    public string Schema => "platform-studio.foundation-recovery-execution-hold-status.v1";
    public bool ReleaseAvailable => false;
    public bool SourceHostFenced => false;
    public bool CredentialActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
}

public sealed partial class FoundationStore
{
    public const int MaximumRecoveryExecutionHolds = 1024;
    const string RecoveryHoldReason = "RestoreRecoveryRequiresReconciliation";
    const string SchemaV11 = """
        CREATE TABLE recovery_execution_holds(customer_id TEXT NOT NULL,cell_id TEXT NOT NULL,database_instance_id TEXT NOT NULL,
          id TEXT NOT NULL,source_database_instance_id TEXT,origin TEXT NOT NULL CHECK(origin IN ('RestoredSnapshot','LegacyRestoreOriginUnknown')),
          backup_sha256 TEXT NOT NULL,policy_version INTEGER NOT NULL,created INTEGER NOT NULL,reason TEXT NOT NULL CHECK(reason='RestoreRecoveryRequiresReconciliation'),digest TEXT NOT NULL,
          CHECK((origin='RestoredSnapshot' AND source_database_instance_id IS NOT NULL) OR (origin='LegacyRestoreOriginUnknown' AND source_database_instance_id IS NULL)),
          PRIMARY KEY(customer_id,cell_id,database_instance_id),UNIQUE(customer_id,id));
        CREATE TRIGGER recovery_execution_holds_no_update BEFORE UPDATE ON recovery_execution_holds
          BEGIN SELECT RAISE(ABORT,'immutable recovery execution hold'); END;
        CREATE TRIGGER recovery_execution_holds_no_delete BEFORE DELETE ON recovery_execution_holds
          BEGIN SELECT RAISE(ABORT,'retained recovery execution hold'); END;
        """;

    public FoundationRecoveryExecutionHoldStatus GetRecoveryExecutionHold(CustomerContext reader)
    {
        if (reader is null) throw new FoundationStoreException("RecoveryHoldContextDenied", 403);
        var roles = reader.Capability switch
        {
            FoundationCapabilities.RecoveryRead => new[] { "Operator" },
            FoundationCapabilities.AuditRead => ["Auditor"],
            FoundationCapabilities.LifecycleRead => ["CustomerAdmin", "Approver", "Auditor"],
            _ => throw new FoundationStoreException("RecoveryHoldContextDenied", 403)
        };
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        MaintenanceContext(db, tx, reader, reader.Capability, roles);
        var hold = ReadCurrentRecoveryExecutionHold(db, tx);
        var status = new FoundationRecoveryExecutionHoldStatus(CustomerId, CellId,
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), hold is not null, hold);
        tx.Commit(); return status;
    }

    // This guard is deliberately absent from Open/Require/activation: safe reads and inactive
    // recovery verification/reconciliation remain available. Never accept a caller status as authority.
    void RequireNoRecoveryExecutionHold(SqliteConnection db, SqliteTransaction? tx)
    {
        if (ReadCurrentRecoveryExecutionHold(db, tx) is not null) throw new FoundationStoreException("RecoveryExecutionHeld", 403);
    }

    static string RecoveryExecutionHoldDigest(FoundationRecoveryExecutionHold hold) => Hash(JsonSerializer.Serialize(new
    {
        schema = "platform-studio.foundation-recovery-execution-hold.v1", hold.Id, hold.CustomerId, hold.CellId, hold.DatabaseInstanceId,
        hold.SourceDatabaseInstanceId, hold.Origin, hold.BackupSha256, hold.PolicyVersion,
        created = hold.CreatedAt.ToUnixTimeMilliseconds(), hold.Reason
    }, CredentialRecoverySets.Json));

    static FoundationRecoveryExecutionHold? ReadCurrentRecoveryExecutionHold(SqliteConnection db, SqliteTransaction? tx)
    {
        try
        {
            var cell = Rows(db, tx, "SELECT customer_id,cell_id,database_instance_id,policy_version FROM cell WHERE singleton=1",
                r => (Customer: r.GetString(0), Cell: r.GetString(1), Instance: r.GetString(2), Policy: r.GetInt64(3))).Single();
            if (!ValidDatabaseInstanceId(cell.Instance)) throw new FoundationStoreException("RecoveryExecutionHoldIntegrityFailed");
            var holds = Rows(db, tx, """
                SELECT id,customer_id,cell_id,database_instance_id,source_database_instance_id,origin,backup_sha256,policy_version,created,reason,digest
                FROM recovery_execution_holds LIMIT $limit
                """, r => new FoundationRecoveryExecutionHold(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetInt64(7), At(r.GetInt64(8)), r.GetString(9), r.GetString(10)), ("$limit", MaximumRecoveryExecutionHolds + 1));
            if (holds.Count > MaximumRecoveryExecutionHolds) throw new FoundationStoreException("RecoveryExecutionHoldCapacityExceeded");
            foreach (var hold in holds)
                if (!ValidDatabaseInstanceId(hold.Id) || hold.CustomerId != cell.Customer || hold.CellId != cell.Cell || !ValidDatabaseInstanceId(hold.DatabaseInstanceId) ||
                    hold.Origin is not ("RestoredSnapshot" or "LegacyRestoreOriginUnknown") ||
                    (hold.Origin == "RestoredSnapshot" ? !ValidDatabaseInstanceId(hold.SourceDatabaseInstanceId) || hold.SourceDatabaseInstanceId == hold.DatabaseInstanceId : hold.SourceDatabaseInstanceId is not null) ||
                    !MaintenanceHash(hold.BackupSha256) || hold.PolicyVersion <= 0 || hold.PolicyVersion > cell.Policy || hold.Reason != RecoveryHoldReason ||
                    !MaintenanceHash(hold.Sha256) || hold.Sha256 != RecoveryExecutionHoldDigest(hold))
                    throw new FoundationStoreException("RecoveryExecutionHoldIntegrityFailed");
            var current = holds.SingleOrDefault(h => h.DatabaseInstanceId == cell.Instance);
            if (current is null && (holds.Count > 0 || Number(db, tx,
                "SELECT EXISTS(SELECT 1 FROM audit WHERE customer_id=$c AND cell_id=$cell AND action='BackupRestored')", ("$c", cell.Customer), ("$cell", cell.Cell)) != 0))
                throw new FoundationStoreException("RecoveryExecutionHoldIntegrityFailed");
            return current;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException or JsonException)
        { throw new FoundationStoreException("RecoveryExecutionHoldIntegrityFailed"); }
    }

    void AppendRecoveryExecutionHold(SqliteConnection db, SqliteTransaction tx, string? sourceInstance, string backupSha256, string origin)
    {
        if (Number(db, tx, "SELECT count(*) FROM recovery_execution_holds") >= MaximumRecoveryExecutionHolds)
            throw new FoundationStoreException("RecoveryExecutionHoldCapacityExceeded");
        var hold = new FoundationRecoveryExecutionHold(NewId(), CustomerId, CellId,
            ScalarText(db, tx, "SELECT database_instance_id FROM cell WHERE singleton=1"), sourceInstance, origin, backupSha256,
            Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1"), At(Now), RecoveryHoldReason, "");
        if (!MaintenanceHash(backupSha256) || origin == "RestoredSnapshot" && (!ValidDatabaseInstanceId(sourceInstance) || sourceInstance == hold.DatabaseInstanceId))
            throw new FoundationStoreException("RecoveryExecutionHoldIntegrityFailed");
        hold = hold with { Sha256 = RecoveryExecutionHoldDigest(hold) };
        Exec(db, tx, "INSERT INTO recovery_execution_holds VALUES($c,$cell,$instance,$id,$source,$origin,$backup,$policy,$created,$reason,$digest)",
            ("$c", CustomerId), ("$cell", CellId), ("$instance", hold.DatabaseInstanceId), ("$id", hold.Id), ("$source", sourceInstance), ("$origin", origin),
            ("$backup", backupSha256), ("$policy", hold.PolicyVersion), ("$created", hold.CreatedAt.ToUnixTimeMilliseconds()), ("$reason", hold.Reason), ("$digest", hold.Sha256));
        var scope = BootstrapScope(db, tx);
        Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "RecoveryExecutionHoldCreated", hold.Id, "Held", origin, hold.Sha256);
        _ = ReadCurrentRecoveryExecutionHold(db, tx);
    }

    void BackfillLegacyRecoveryExecutionHold(SqliteConnection db, SqliteTransaction tx)
    {
        var restored = Rows(db, tx, "SELECT correlation FROM audit WHERE customer_id=$c AND cell_id=$cell AND action='BackupRestored' ORDER BY sequence DESC LIMIT 1",
            r => r.GetString(0), ("$c", CustomerId), ("$cell", CellId)).SingleOrDefault();
        if (restored is not null) AppendRecoveryExecutionHold(db, tx, null, restored, "LegacyRestoreOriginUnknown");
    }

    static void ValidateRecoveryExecutionHoldSchema(SqliteConnection db, SqliteTransaction? tx)
    {
        if (Number(db, tx, "SELECT count(*) FROM schema_migrations WHERE version=11 AND checksum=$sha", ("$sha", SchemaV11Checksum)) != 1)
            throw new FoundationStoreException("MigrationHistoryMismatch");
        using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False"); reference.Open(); Exec(reference, null, SchemaV11);
        var actual = SnapshotSchemaObjects(db, tx);
        foreach (var expected in SnapshotSchemaObjects(reference).Values)
            if (!actual.TryGetValue(expected.Name, out var observed) || observed.Type != expected.Type || observed.Table != expected.Table || NormalizeSnapshotSql(observed.Sql) != NormalizeSnapshotSql(expected.Sql))
                throw new FoundationStoreException("MigrationSchemaMismatch");
        _ = ReadCurrentRecoveryExecutionHold(db, tx);
    }
}
