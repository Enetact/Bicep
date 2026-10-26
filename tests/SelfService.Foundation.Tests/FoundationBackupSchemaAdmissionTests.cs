using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBackupSchemaAdmissionTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Valid_frozen_versions_verify_readonly_and_restore_through_staged_upgrade_with_recovery_holds(int version)
    {
        using var f = new SignedBackupFixture(); var job = f.Cell.Queue(); Downgrade(f.Cell, version); var signed = f.Create();
        Assert.Equal(version, f.Cell.Scalar("SELECT schema_version FROM cell"));
        using var key = f.PublicKey(); var before = File.ReadAllBytes(signed.Backup.DatabasePath); var files = Files(f);
        var claims = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), key, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Equal(version, claims.CellSchemaVersion); Assert.Equal(files, Files(f)); Assert.Equal(before, File.ReadAllBytes(signed.Backup.DatabasePath));
        var destination = f.Target();
        var restored = FoundationStore.RestoreSignedBackup(signed, destination, FoundationBackupSignatures.Authority(signed.Manifest), key, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Equal(FoundationStore.CurrentSchemaVersion, restored.SchemaStatus().Version);
        Assert.Equal("OutcomeUnknown", restored.GetJob(f.Cell.Customer, "viewer", job.Id).State); Assert.True(restored.VerifyAuditIntegrity(f.Cell.Customer, "auditor"));
        Assert.Equal("1", Scalar(destination, "SELECT count(*) FROM recovery_execution_holds WHERE database_instance_id=(SELECT database_instance_id FROM cell WHERE singleton=1)"));
        Assert.Equal(version < 5 ? "LegacyRestoreOriginUnknown" : "RestoredSnapshot", Scalar(destination,
            "SELECT origin FROM recovery_execution_holds WHERE database_instance_id=(SELECT database_instance_id FROM cell WHERE singleton=1)"));
        Assert.Equal(version < 5 ? "" : Scalar(f.Cell.Path, "SELECT database_instance_id FROM cell WHERE singleton=1"), Scalar(destination,
            "SELECT source_database_instance_id FROM recovery_execution_holds WHERE database_instance_id=(SELECT database_instance_id FROM cell WHERE singleton=1)"));
        Assert.Equal(before, File.ReadAllBytes(signed.Backup.DatabasePath));
    }

    public static IEnumerable<object[]> RequiredObjects()
    {
        foreach (var table in new[] { "cell", "actors", "memberships", "connections", "plans", "approvals", "jobs", "outbox", "inbox", "audit", "artifacts" }) yield return [1, "TABLE", table];
        foreach (var table in new[] { "schema_migrations", "scopes", "customer_lifecycle", "administrative_plans", "administrative_approvals", "scope_exports", "artifact_tombstones" }) yield return [2, "TABLE", table];
        yield return [3, "TABLE", "support_grants"]; yield return [4, "TABLE", "worker_heartbeats"];
        foreach (var name in new[] { "cell_instance_insert_guard", "cell_instance_update_guard" }) yield return [5, "TRIGGER", name];
        yield return [6, "TABLE", "credential_maintenance_approvals"]; yield return [7, "TABLE", "credential_maintenance_observation_receipts"];
        foreach (var table in new[] { "credential_recovery_stage_review_requests", "credential_recovery_stage_review_approvals", "credential_recovery_stage_review_executions" }) yield return [8, "TABLE", table];
    }
    [Theory]
    [MemberData(nameof(RequiredObjects))]
    public void A_correct_signature_and_byte_pin_cannot_authorize_repair_of_missing_required_schema(int version, string kind, string name)
    {
        using var f = new SignedBackupFixture(); Downgrade(f.Cell, version); var signed = f.Create();
        signed = Mutate(f, signed, "DROP " + kind + " " + name); AssertRejectedBeforeTarget(f, signed);
    }

    public static IEnumerable<object[]> RedefinedObjects()
    {
        foreach (var guard in new[] { "audit_no_update", "audit_no_delete" }) yield return [1, "TRIGGER", guard, "CREATE TRIGGER " + guard + " BEFORE UPDATE ON audit BEGIN SELECT 1; END"];
        yield return [2, "TRIGGER", "schema_migrations_no_update", "CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT 1; END"];
        yield return [2, "TRIGGER", "administrative_approvals_no_delete", "CREATE TRIGGER administrative_approvals_no_delete BEFORE DELETE ON administrative_approvals BEGIN SELECT 1; END"];
        yield return [5, "TRIGGER", "cell_instance_update_guard", "CREATE TRIGGER cell_instance_update_guard BEFORE UPDATE ON cell BEGIN SELECT 1; END"];
        yield return [6, "TRIGGER", "credential_maintenance_success_immutable", "CREATE TRIGGER credential_maintenance_success_immutable BEFORE UPDATE ON credential_maintenance_executions BEGIN SELECT 1; END"];
        yield return [7, "TRIGGER", "credential_maintenance_observation_receipts_no_update", "CREATE TRIGGER credential_maintenance_observation_receipts_no_update BEFORE UPDATE ON credential_maintenance_observation_receipts BEGIN SELECT 1; END"];
        yield return [8, "TRIGGER", "credential_recovery_review_success_immutable", "CREATE TRIGGER credential_recovery_review_success_immutable BEFORE UPDATE ON credential_recovery_stage_review_executions BEGIN SELECT 1; END"];
        yield return [1, "INDEX", "jobs_one_execution_per_plan", "CREATE INDEX jobs_one_execution_per_plan ON jobs(customer_id,id)"];
        yield return [2, "INDEX", "administrative_ready", "CREATE INDEX administrative_ready ON administrative_plans(customer_id,expires)"];
        yield return [3, "INDEX", "support_active", "CREATE INDEX support_active ON support_grants(customer_id,actor_key)"];
        yield return [4, "INDEX", "worker_heartbeat_health", "CREATE INDEX worker_heartbeat_health ON worker_heartbeats(customer_id,worker_id)"];
        yield return [6, "INDEX", "credential_maintenance_admission", "CREATE INDEX credential_maintenance_admission ON credential_maintenance_executions(customer_id,intent_id)"];
        yield return [7, "INDEX", "credential_maintenance_observation_receipt_id", "CREATE INDEX credential_maintenance_observation_receipt_id ON credential_maintenance_observation_receipts(customer_id,request_id)"];
        yield return [8, "INDEX", "credential_recovery_review_admission", "CREATE INDEX credential_recovery_review_admission ON credential_recovery_stage_review_executions(customer_id,admission_id)"];
    }
    [Theory]
    [MemberData(nameof(RedefinedObjects))]
    public void Required_names_do_not_substitute_for_guard_bodies_unique_indexes_or_exact_bindings(int version, string kind, string name, string replacement)
    {
        using var f = new SignedBackupFixture(); Downgrade(f.Cell, version); var signed = f.Create();
        signed = Mutate(f, signed, "DROP " + kind + " " + name + "; " + replacement); AssertRejectedBeforeTarget(f, signed);
    }

    [Theory]
    [InlineData("column")]
    [InlineData("nullability")]
    [InlineData("primary-key")]
    [InlineData("foreign-key")]
    [InlineData("check")]
    [InlineData("view")]
    public void Redefined_base_tables_and_views_cannot_be_recreated_or_adopted_during_restore(string changed)
    {
        using var f = new SignedBackupFixture(); var signed = f.Create();
        var sql = changed switch
        {
            "column" => "ALTER TABLE outbox RENAME COLUMN kind TO missing_kind",
            "nullability" => "DROP TABLE outbox; CREATE TABLE outbox(customer_id TEXT,event_id TEXT NOT NULL,job_id TEXT NOT NULL,kind TEXT NOT NULL,created INTEGER NOT NULL,delivered INTEGER,PRIMARY KEY(customer_id,event_id),FOREIGN KEY(customer_id,job_id) REFERENCES jobs(customer_id,id))",
            "primary-key" => "DROP TABLE outbox; CREATE TABLE outbox(customer_id TEXT NOT NULL,event_id TEXT NOT NULL,job_id TEXT NOT NULL,kind TEXT NOT NULL,created INTEGER NOT NULL,delivered INTEGER,FOREIGN KEY(customer_id,job_id) REFERENCES jobs(customer_id,id))",
            "foreign-key" => "DROP TABLE outbox; CREATE TABLE outbox(customer_id TEXT NOT NULL,event_id TEXT NOT NULL,job_id TEXT NOT NULL,kind TEXT NOT NULL,created INTEGER NOT NULL,delivered INTEGER,PRIMARY KEY(customer_id,event_id))",
            "check" => "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql=replace(sql,'CHECK(singleton=1)','') WHERE name='cell'; PRAGMA writable_schema=OFF",
            _ => "DROP TABLE outbox; CREATE VIEW outbox AS SELECT customer_id,event_id,id AS job_id,'synthetic' AS kind,created,NULL AS delivered FROM audit"
        };
        signed = Mutate(f, signed, sql); AssertRejectedBeforeTarget(f, signed);
    }

    [Theory]
    [InlineData(2, "checksum")]
    [InlineData(3, "missing")]
    [InlineData(4, "extra")]
    [InlineData(5, "checksum")]
    [InlineData(6, "earlier-checksum")]
    [InlineData(7, "earlier-checksum")]
    [InlineData(8, "earlier-checksum")]
    public void Every_declared_legacy_history_row_must_match_its_frozen_checksum_before_migration(int version, string change)
    {
        using var f = new SignedBackupFixture(); Downgrade(f.Cell, version); var signed = f.Create();
        var originalGuard = Scalar(signed.Backup.DatabasePath, "SELECT sql FROM sqlite_master WHERE name='schema_migrations_no_update'");
        var deleteGuard = Scalar(signed.Backup.DatabasePath, "SELECT sql FROM sqlite_master WHERE name='schema_migrations_no_delete'");
        var sql = change switch
        {
            "missing" => "DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version=2; " + deleteGuard,
            "extra" => "INSERT INTO schema_migrations VALUES(8,'unrecognized',0)",
            _ => "DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=1; " + originalGuard
        };
        signed = Mutate(f, signed, sql); AssertRejectedBeforeTarget(f, signed);
    }

    [Theory]
    [InlineData("trigger")]
    [InlineData("view")]
    [InlineData("virtual-table")]
    [InlineData("sqlite-like-trigger")]
    [InlineData("sqlite-like-view")]
    [InlineData("sqlite-case-alias-trigger")]
    [InlineData("sqlite-case-alias-view")]
    [InlineData("sqlite-like-oversized-schema")]
    [InlineData("reserved-prefix-trigger")]
    [InlineData("reserved-prefix-view")]
    [InlineData("later-version")]
    [InlineData("oversized-schema")]
    public void Unrecognized_executable_schema_and_later_version_collisions_are_refused_before_any_target(string extra)
    {
        using var f = new SignedBackupFixture(); if (extra == "later-version") Downgrade(f.Cell, 2); var signed = f.Create();
        var sql = extra switch
        {
            "trigger" => "CREATE TRIGGER synthetic_side_effect AFTER UPDATE ON cell BEGIN DELETE FROM artifacts; END",
            "view" => "CREATE VIEW synthetic_unreviewed_view AS SELECT * FROM cell",
            "virtual-table" => "CREATE VIRTUAL TABLE synthetic_unreviewed_virtual USING fts5(content)",
            "sqlite-like-trigger" => "CREATE TRIGGER sqliteXsynthetic AFTER UPDATE ON cell BEGIN DELETE FROM artifacts; END",
            "sqlite-like-view" => "CREATE VIEW sqliteXsynthetic AS SELECT * FROM cell",
            "sqlite-case-alias-trigger" => "CREATE TRIGGER SQLITExsynthetic AFTER UPDATE ON cell BEGIN DELETE FROM artifacts; END",
            "sqlite-case-alias-view" => "CREATE VIEW SQLITExsynthetic AS SELECT * FROM cell",
            "sqlite-like-oversized-schema" => "CREATE TABLE sqliteXsynthetic(value TEXT CHECK(value!='" + new string('a', 66000) + "'))",
            "reserved-prefix-trigger" => "CREATE TABLE synthetic_probe(fired INTEGER); PRAGMA writable_schema=ON; CREATE TRIGGER sqlite_synthetic AFTER UPDATE ON cell BEGIN INSERT INTO synthetic_probe VALUES(1); END; PRAGMA writable_schema=OFF",
            "reserved-prefix-view" => "PRAGMA writable_schema=ON; CREATE VIEW sqlite_synthetic AS SELECT * FROM cell; PRAGMA writable_schema=OFF",
            "later-version" => "CREATE TABLE support_grants(unrecognized TEXT)",
            _ => "CREATE TABLE synthetic_oversized_metadata(value TEXT CHECK(value!='" + new string('a', 66000) + "'))"
        };
        signed = Mutate(f, signed, sql);
        if (extra == "reserved-prefix-trigger")
        {
            // A fresh SQLite reload honors this reserved-name trigger despite ordinary CREATE prohibiting that name.
            // Demonstrate its side effect only in a separate in-memory witness, never in the incoming packet.
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = signed.Backup.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var witness = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ":memory:", Mode = SqliteOpenMode.Memory, Pooling = false }.ToString());
            source.Open(); witness.Open(); source.BackupDatabase(witness); using var command = witness.CreateCommand();
            command.CommandText = "UPDATE cell SET policy_version=policy_version+1; SELECT count(*) FROM synthetic_probe";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        AssertRejectedBeforeTarget(f, signed);
    }

    [Fact]
    public void Bounded_ordinary_extension_table_and_index_do_not_replace_required_schema_or_claim_business_attestation()
    {
        using var f = new SignedBackupFixture(); var signed = Mutate(f, f.Create(), "CREATE TABLE synthetic_extension(id TEXT PRIMARY KEY,value TEXT); CREATE INDEX synthetic_extension_value ON synthetic_extension(value)");
        using var key = f.PublicKey(); var bytes = File.ReadAllBytes(signed.Backup.DatabasePath);
        var claims = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), key, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Equal(FoundationStore.CurrentSchemaVersion, claims.CellSchemaVersion); Assert.Equal(bytes, File.ReadAllBytes(signed.Backup.DatabasePath));
    }

    static void AssertRejectedBeforeTarget(SignedBackupFixture f, FoundationSignedBackup signed)
    {
        var bytes = File.ReadAllBytes(signed.Backup.DatabasePath); var before = Files(f); var target = f.AbsentTarget(); using var key = f.PublicKey();
        var authority = FoundationBackupSignatures.Authority(signed.Manifest);
        _ = FoundationBackupSignatures.Verify(signed.Manifest, key, authority, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Throws<FoundationStoreException>(() => FoundationStore.VerifySignedBackup(signed, authority, key, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock));
        Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreSignedBackup(signed, target, authority, key, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock));
        Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(signed.Backup, target, f.Cell.Clock));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target))); Assert.Equal(before, Files(f)); Assert.Equal(bytes, File.ReadAllBytes(signed.Backup.DatabasePath));
    }
    static string[] Files(SignedBackupFixture f) => Directory.GetFileSystemEntries(Path.GetDirectoryName(f.Cell.Path)!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    static FoundationSignedBackup Mutate(SignedBackupFixture f, FoundationSignedBackup signed, string sql)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = signed.Backup.DatabasePath, Pooling = false, ForeignKeys = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        var bytes = File.ReadAllBytes(signed.Backup.DatabasePath); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(signed.Backup with { Sha256 = hash, Length = bytes.Length }, f.Sign(signed.Manifest with { SnapshotSha256 = hash, SnapshotLength = bytes.Length }));
    }
    static string Scalar(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!;
    }
    static void Downgrade(StoreFixture f, int version)
    {
        if (version == FoundationStore.CurrentSchemaVersion) return;
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, version);
        if (version < 5) f.Execute("DROP TRIGGER cell_instance_insert_guard; DROP TRIGGER cell_instance_update_guard; ALTER TABLE cell DROP COLUMN database_instance_id");
        if (version < 4) f.Execute("DROP TABLE worker_heartbeats");
        if (version < 3) f.Execute("DROP TABLE support_grants");
        if (version < 2) f.Execute("DROP TABLE artifact_tombstones; DROP TABLE scope_exports; DROP TABLE administrative_approvals; DROP TABLE administrative_plans; DROP TABLE customer_lifecycle; DROP TABLE scopes; DROP TABLE schema_migrations");
        else f.Execute("DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>" + version + "; CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END");
        f.Execute("UPDATE cell SET schema_version=" + version);
    }
}
