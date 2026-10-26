using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialRestrictedRecoveryJointMigrationTests
{
    [Fact]
    public void V13_upgrade_preserves_prior_history_instance_jobs_and_audit_and_requires_explicit_upgrade()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var instance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        Downgrade(f); var history = History(f); var bytes = File.ReadAllBytes(f.Path);
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(13, f.Scalar("SELECT schema_version FROM cell"));
        var upgraded = f.Reopen(); Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version);
        Assert.Equal(history, History(f).Take(13)); Assert.Equal(instance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal(job.Id, upgraded.GetJob(f.Customer, "viewer", job.Id).Id);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=14"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV13ToV14'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_restricted_recovery_joint_executions"));
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Theory]
    [InlineData("table")] [InlineData("audit")]
    public void V14_migration_failure_rolls_back_schema_history_and_audit(string fault)
    {
        using var f = new StoreFixture(); Downgrade(f); var history = History(f); var audit = f.Scalar("SELECT count(*) FROM audit");
        if (fault == "table") f.Execute("CREATE TABLE credential_restricted_recovery_joint_executions(conflicting TEXT)");
        else f.Execute("CREATE TRIGGER synthetic_joint_migration_fault BEFORE INSERT ON audit WHEN NEW.reason='SchemaV13ToV14' BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END");
        Assert.Throws<SqliteException>(() => f.Reopen()); Assert.Equal(13, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(history, History(f)); Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_restricted_recovery_joint_state'"));
    }

    [Theory]
    [InlineData("DROP TABLE credential_restricted_recovery_joint_executions")]
    [InlineData("DROP INDEX credential_restricted_recovery_joint_state")]
    [InlineData("DROP TRIGGER credential_restricted_recovery_joint_success_immutable")]
    public void Current_schema_and_snapshot_missing_required_objects_are_refused_without_repair(string mutation)
    {
        using var f = new StoreFixture(); var snapshot = f.Path + ".joint-snapshot"; var original = f.Store.CreateBackup(snapshot);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = mutation; command.ExecuteNonQuery(); }
        var bytes = File.ReadAllBytes(snapshot); var changed = original with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Length = bytes.Length };
        var destination = f.Path + ".joint-restore";
        Assert.Equal("BackupIntegrityFailed", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(changed, destination)).Code);
        Assert.False(File.Exists(destination)); Assert.Equal(bytes, File.ReadAllBytes(snapshot)); File.Delete(snapshot);
        f.Execute(mutation); var existing = File.ReadAllBytes(f.Path);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, f.Cell, f.Customer, initializeSchema: false)).Code);
        Assert.Equal(existing, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void Rewritten_v14_checksum_is_refused_without_repair()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='modified' WHERE version=14; CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        Assert.Equal(FoundationStore.CurrentSchemaVersion, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=14 AND checksum='modified'"));
    }

    [Fact]
    public void All_thirteen_prior_DDL_bodies_and_checksums_match_the_frozen_reviewed_literals()
    {
        // Every declaration was compared with reviewed HEAD d7569f7 before these pins were recorded.
        string[] checksums = [
            "f0e23a6bf33cfeda5e691953b0203159bb8039f054ea4d58bc3a435e367ed701",
            "2f17a8550265661ca45cbf981b37d41c6fce85b708052075e99ad772be2e28f3",
            "b934ca9098bab0e6c2ef6cd44472d8b270789873c677afea036043e28b4b6483",
            "98a9c979bc3aaaa7fff7adf00748100f1bc0207028abcc45f62cf9e85d03d424",
            "89a028abc782ab3189c3a2c011f39959de8bc419e1626e6126053ad295edeb60",
            "99bc4e3943883bf1d8891e7378f2d14a5fda135bf453d0e13ffef128aacc8dcb",
            "7b63e39edaec3701a4194219edd4d8eb8dddffabc7a890404028e70a743e82f3",
            "962734218bea411d1573fcabe78fe289121c0065e2065e7a0bb28f32d6b9bdd6",
            "169608c2b136ef04724b5c183cbb29901d7559147d86389c3f7c80a0081e80ea",
            "f5d3abda737927370c660d68f409ffbc42760783182320816e5a6a5303f52c8d",
            "72259b9885bc2aff4e54e81f7d1e077014e5fb30285ccea1b1b357c0ea98d6a9",
            "e83772a4406fc545b8dd13f4ce565bd4c1685dceda21e2729fcdd44f7dd6425c",
            "6a143ad31387007f14448172337a8669ce9e5ca917ced32e2bd1ad8c3e38b5cd"];
        using var f = new StoreFixture(); Assert.Equal(checksums, History(f).Take(13));
        for (var version = 1; version <= 13; version++)
        {
            var name = version == 4 ? "WorkerOperationsSchemaV4" : "SchemaV" + version;
            var body = (string)typeof(FoundationStore).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n", StringComparison.Ordinal))));
            Assert.Equal(version == 1 ? "f4b6df6411f5987eb49fcac7030b7a11571c6b9d08d247c963bb4d3d37afd1ee" : checksums[version - 1], digest);
        }
    }

    static string[] History(StoreFixture f)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version";
        using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }
    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 13);
        f.Execute("""
            DROP TRIGGER schema_migrations_no_delete;
            DELETE FROM schema_migrations WHERE version>=14;
            UPDATE cell SET schema_version=13;
            CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
            """);
    }
}
