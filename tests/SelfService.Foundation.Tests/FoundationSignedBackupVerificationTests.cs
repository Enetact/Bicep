using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationSignedBackupVerificationTests
{
    [Fact]
    public void Full_verification_is_readonly_and_creates_no_restore_target_or_audit_event()
    {
        using var fixture = new SignedBackupFixture(); fixture.Cell.Queue(); var signed = fixture.Create();
        using var key = fixture.PublicKey(); var before = File.ReadAllBytes(signed.Backup.DatabasePath);
        var files = Directory.GetFiles(Path.GetDirectoryName(signed.Backup.DatabasePath)!).Order().ToArray();
        var auditCount = fixture.Cell.Store.ListAudit(fixture.Cell.Customer, "auditor").Count;
        var claims = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), key,
            fixture.Cell.Customer, fixture.Cell.Cell, fixture.Cell.Clock);
        Assert.Equal(signed.Backup.Sha256, claims.SnapshotSha256); Assert.Equal(FoundationStore.CurrentSchemaVersion, claims.CellSchemaVersion);
        Assert.False(claims.SourceHostFenced); Assert.False(claims.ExternalExecutionAuthorized);
        Assert.Equal(before, File.ReadAllBytes(signed.Backup.DatabasePath));
        Assert.Equal(files, Directory.GetFiles(Path.GetDirectoryName(signed.Backup.DatabasePath)!).Order().ToArray());
        Assert.Equal(auditCount, fixture.Cell.Store.ListAudit(fixture.Cell.Customer, "auditor").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signed_main_file_cannot_admit_WAL_mode_or_uncommitted_sidecar_views(bool walMode)
    {
        using var fixture = new SignedBackupFixture(); var signed = fixture.Create();
        if (walMode)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = signed.Backup.DatabasePath, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL"; Assert.Equal("wal", command.ExecuteScalar());
        }
        else File.WriteAllText(fixture.Path(Path.GetFileName(signed.Backup.DatabasePath) + "-wal"), "synthetic uncommitted sidecar");
        var bytes = File.ReadAllBytes(signed.Backup.DatabasePath); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        signed = new(signed.Backup with { Sha256 = sha, Length = bytes.Length }, fixture.Sign(signed.Manifest with { SnapshotSha256 = sha, SnapshotLength = bytes.Length }));
        var files = Directory.GetFiles(Path.GetDirectoryName(signed.Backup.DatabasePath)!).Order().ToArray();
        using var key = fixture.PublicKey(); var authority = FoundationBackupSignatures.Authority(signed.Manifest);
        _ = FoundationBackupSignatures.Verify(signed.Manifest, key, authority, fixture.Cell.Customer, fixture.Cell.Cell, fixture.Cell.Clock);
        var error = Assert.Throws<FoundationStoreException>(() => FoundationStore.VerifySignedBackup(signed, authority, key,
            fixture.Cell.Customer, fixture.Cell.Cell, fixture.Cell.Clock));
        Assert.Equal("BackupSnapshotNotStandalone", error.Code);
        Assert.Equal(files, Directory.GetFiles(Path.GetDirectoryName(signed.Backup.DatabasePath)!).Order().ToArray());
        var target = fixture.AbsentTarget();
        Assert.Equal("BackupSnapshotNotStandalone", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(signed.Backup, target, fixture.Cell.Clock)).Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Valid_signature_and_hash_do_not_substitute_for_sqlite_audit_and_schema_verification(bool wrongSchema)
    {
        using var fixture = new SignedBackupFixture(); var signed = fixture.Create();
        if (wrongSchema) signed = signed with { Manifest = fixture.Sign(signed.Manifest with { CellSchemaVersion = FoundationStore.CurrentSchemaVersion - 1 }) };
        else
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = signed.Backup.DatabasePath, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "DROP TRIGGER audit_no_update; UPDATE audit SET outcome='SyntheticRewrite' WHERE sequence=1;"; command.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(signed.Backup.DatabasePath); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            signed = new(signed.Backup with { Sha256 = sha, Length = bytes.Length },
                fixture.Sign(signed.Manifest with { SnapshotSha256 = sha, SnapshotLength = bytes.Length }));
        }
        using var key = fixture.PublicKey(); var authority = FoundationBackupSignatures.Authority(signed.Manifest);
        _ = FoundationBackupSignatures.Verify(signed.Manifest, key, authority, fixture.Cell.Customer, fixture.Cell.Cell, fixture.Cell.Clock);
        var error = Assert.Throws<FoundationStoreException>(() => FoundationStore.VerifySignedBackup(signed, authority, key,
            fixture.Cell.Customer, fixture.Cell.Cell, fixture.Cell.Clock));
        Assert.Equal(wrongSchema ? "SignedBackupSchemaMismatch" : "BackupIntegrityFailed", error.Code);
    }
}
