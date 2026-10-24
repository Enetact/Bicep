using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBackupSignatureTests
{
    [Fact]
    public void Real_signed_sqlite_snapshot_restores_with_policy_fence_and_quarantined_work()
    {
        using var f = new SignedBackupFixture(); var job = f.Cell.Queue();
        var before = f.Cell.Scalar("SELECT policy_version FROM cell WHERE singleton=1");
        var signed = f.Create(); var authority = FoundationBackupSignatures.Authority(signed.Manifest);
        using var publicKey = f.PublicKey();
        var verified = FoundationBackupSignatures.Verify(signed.Manifest, publicKey, authority, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Equal(FoundationStore.CurrentSchemaVersion, verified.CellSchemaVersion);
        Assert.False(verified.SourceHostFenced); Assert.False(verified.ExternalExecutionAuthorized);
        var restored = FoundationStore.RestoreSignedBackup(signed, f.Target(), authority, publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        var held = restored.GetJob(f.Cell.Customer, "operator", job.Id);
        Assert.Equal("OutcomeUnknown", held.State); Assert.Equal("RecoveryRequiresReconciliation", held.Reason);
        Assert.Null(restored.ClaimNext(f.Cell.Customer, "restored-worker", TimeSpan.FromMinutes(1)));
        Assert.Contains(restored.ListAudit(f.Cell.Customer, "auditor"), row => row.Action == "BackupRestored" && row.CorrelationId == signed.Backup.Sha256);
        Assert.Equal(before, f.Cell.Scalar("SELECT policy_version FROM cell WHERE singleton=1"));
        Assert.Equal("Queued", f.Cell.Store.GetJob(f.Cell.Customer, "operator", job.Id).State);
        Assert.Contains(f.Cell.Store.ListAudit(f.Cell.Customer, "auditor"), row => row.Action == "BackupSignatureCreated");
    }

    [Fact]
    public void Snapshot_can_move_without_changing_its_signed_content_binding()
    {
        using var f = new SignedBackupFixture(); var signed = f.Create(); var moved = f.Path("moved.sqlite");
        File.Copy(signed.Backup.DatabasePath, moved);
        var packet = signed with { Backup = signed.Backup with { DatabasePath = moved } };
        using var publicKey = f.PublicKey();
        var restored = FoundationStore.RestoreSignedBackup(packet, f.Target(), FoundationBackupSignatures.Authority(signed.Manifest), publicKey,
            f.Cell.Customer, f.Cell.Cell, f.Cell.Clock);
        Assert.Equal(f.Cell.Customer, restored.CustomerId);
        Assert.DoesNotContain(moved, Encoding.UTF8.GetString(FoundationBackupSignatures.CanonicalPayload(signed.Manifest)), StringComparison.Ordinal);
    }

    [Fact]
    public void Old_valid_signature_is_rejected_against_independently_pinned_latest_generation()
    {
        using var f = new SignedBackupFixture(); var old = f.Create(1); var latest = f.Create(2);
        using var publicKey = f.PublicKey();
        var target = f.AbsentTarget();
        Assert.Equal("SignedBackupLatestAuthorityMismatch", Error(() => FoundationStore.RestoreSignedBackup(old, target,
            FoundationBackupSignatures.Authority(latest.Manifest), publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
        Assert.NotNull(FoundationBackupSignatures.Verify(old.Manifest, publicKey, FoundationBackupSignatures.Authority(old.Manifest),
            f.Cell.Customer, f.Cell.Cell, f.Cell.Clock));
    }

    [Theory]
    [InlineData("key", "SignedBackupKeyMismatch")]
    [InlineData("customer", "SignedBackupBindingMismatch")]
    [InlineData("cell", "SignedBackupBindingMismatch")]
    [InlineData("authority", "SignedBackupLatestAuthorityMismatch")]
    [InlineData("payload", "SignedBackupSignatureInvalid")]
    public void Signature_and_explicit_authority_fail_before_even_a_missing_source_is_opened(string attack, string code)
    {
        using var f = new SignedBackupFixture(); var signed = f.Create(); using var other = RSA.Create(2048); using var publicKey = f.PublicKey();
        var manifest = attack == "payload" ? signed.Manifest with { SnapshotLength = signed.Manifest.SnapshotLength + 1 } : signed.Manifest;
        var authority = FoundationBackupSignatures.Authority(manifest);
        if (attack == "authority") authority = authority with { ManifestSha256 = new string('0', 64) };
        var packet = signed with { Manifest = manifest, Backup = signed.Backup with { DatabasePath = f.Path("missing-source.sqlite") } };
        var target = f.AbsentTarget();
        Assert.Equal(code, Error(() => FoundationStore.RestoreSignedBackup(packet, target, authority, attack == "key" ? other : publicKey,
            attack == "customer" ? "customer-b" : f.Cell.Customer, attack == "cell" ? "cell-b" : f.Cell.Cell, f.Cell.Clock)));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
    }

    [Theory]
    [InlineData("expired", "SignedBackupExpired")]
    [InlineData("future", "SignedBackupIssuedInFuture")]
    [InlineData("inactive", "SignedBackupNotYetValid")]
    public void Lifetime_rejections_are_fail_closed_before_target_creation(string scenario, string code)
    {
        using var f = new SignedBackupFixture(); var signed = f.Create();
        var manifest = scenario switch
        {
            "expired" => signed.Manifest with { ExpiresUtc = f.Cell.Clock.GetUtcNow().AddMinutes(1) },
            "future" => signed.Manifest with { IssuedUtc = f.Cell.Clock.GetUtcNow().AddMinutes(1) },
            _ => signed.Manifest with { NotBeforeUtc = f.Cell.Clock.GetUtcNow().AddMinutes(1) }
        };
        manifest = f.Sign(manifest);
        if (scenario == "expired") f.Cell.Clock.Advance(TimeSpan.FromMinutes(1));
        using var publicKey = f.PublicKey(); var target = f.AbsentTarget();
        var packet = signed with { Manifest = manifest };
        Assert.Equal(code, Error(() => FoundationStore.RestoreSignedBackup(packet, target, FoundationBackupSignatures.Authority(manifest), publicKey,
            f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("schema")]
    [InlineData("generation")]
    [InlineData("algorithm")]
    [InlineData("length")]
    [InlineData("precision")]
    public void Malformed_schema_generation_algorithm_length_and_time_are_rejected(string field)
    {
        using var f = new SignedBackupFixture(); var signed = f.Create();
        var invalid = field switch
        {
            "version" => signed.Manifest with { Version = 2 },
            "schema" => signed.Manifest with { CellSchemaVersion = FoundationStore.CurrentSchemaVersion + 1 },
            "generation" => signed.Manifest with { Generation = 0 },
            "algorithm" => signed.Manifest with { Algorithm = "RSA-PKCS1-SHA256" },
            "length" => signed.Manifest with { SnapshotLength = FoundationBackupSignatures.MaximumSnapshotBytes + 1 },
            _ => signed.Manifest with { NotBeforeUtc = signed.Manifest.NotBeforeUtc.AddTicks(1) }
        };
        using var publicKey = f.PublicKey();
        var target = f.AbsentTarget();
        Assert.Equal("InvalidSignedBackupManifest", Error(() => FoundationStore.RestoreSignedBackup(signed with { Manifest = invalid }, target,
            FoundationBackupSignatures.Authority(signed.Manifest), publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
    }

    [Fact]
    public void Unsupported_or_public_only_signers_and_bad_parameters_create_no_snapshot_or_parent_directory()
    {
        using var f = new SignedBackupFixture(); using var publicOnly = f.PublicKey(); using var weak = RSA.Create(1024);
        foreach (var signer in new[] { publicOnly, weak })
        {
            var target = f.AbsentTarget();
            Assert.Equal("SignedBackupSigningKeyInvalid", Error(() => f.Cell.Store.CreateSignedBackup(target, signer, "backup-key", 1,
                f.Cell.Clock.GetUtcNow(), f.Cell.Clock.GetUtcNow().AddDays(1))));
            Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
        }
        foreach (var generation in new[] { 0L, 9007199254740992L })
        {
            var target = f.AbsentTarget();
            Assert.Equal("InvalidSignedBackupManifest", Error(() => f.Cell.Store.CreateSignedBackup(target, f.Key, "backup-key", generation,
                f.Cell.Clock.GetUtcNow(), f.Cell.Clock.GetUtcNow().AddDays(1))));
            Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
        }
    }

    [Fact]
    public void Snapshot_bytes_unsigned_metadata_and_signed_schema_are_rechecked_before_touching_target()
    {
        using var f = new SignedBackupFixture(); var signed = f.Create(); using var publicKey = f.PublicKey();
        var target = f.AbsentTarget();
        var rebound = signed with { Backup = signed.Backup with { CustomerId = "customer-b" } };
        Assert.Equal("SignedBackupBindingMismatch", Error(() => FoundationStore.RestoreSignedBackup(rebound, target,
            FoundationBackupSignatures.Authority(signed.Manifest), publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        var schema = f.Sign(signed.Manifest with { CellSchemaVersion = FoundationStore.CurrentSchemaVersion - 1 });
        Assert.Equal("SignedBackupSchemaMismatch", Error(() => FoundationStore.RestoreSignedBackup(signed with { Manifest = schema }, target,
            FoundationBackupSignatures.Authority(schema), publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        using (var output = new FileStream(signed.Backup.DatabasePath, FileMode.Append)) output.WriteByte(42);
        Assert.Equal("BackupIntegrityFailed", Error(() => FoundationStore.RestoreSignedBackup(signed, target,
            FoundationBackupSignatures.Authority(signed.Manifest), publicKey, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(target)));
    }

    [Fact]
    public void Strict_bounded_manifest_json_rejects_duplicates_case_aliases_unknowns_and_missing_fields()
    {
        using var f = new SignedBackupFixture(); var signed = f.Create();
        var json = JsonSerializer.Serialize(signed.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(signed.Manifest, FoundationBackupSignatures.Parse(json));
        foreach (var invalid in new[]
        {
            json.Insert(1, "\"version\":1,"), json.Insert(1, "\"Version\":1,"), json.Insert(1, "\"surprise\":true,"),
            json.Replace("\"generation\":1,", ""), new string(' ', FoundationBackupSignatures.MaximumManifestBytes + 1)
        }) Assert.Equal("InvalidSignedBackupManifest", Error(() => FoundationBackupSignatures.Parse(invalid)));
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain(signed.Backup.DatabasePath, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_padding_whitespace_signature_and_key_id_tampering_cannot_pass_latest_pin_and_signature()
    {
        using var f = new SignedBackupFixture(); var signed = f.Create(); using var publicKey = f.PublicKey();
        foreach (var invalid in new[]
        {
            signed.Manifest with { Signature = Convert.ToBase64String(f.Key.SignData(FoundationBackupSignatures.CanonicalPayload(signed.Manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
            signed.Manifest with { KeyId = "other-key" }
        }) Assert.Equal("SignedBackupSignatureInvalid", Error(() => FoundationBackupSignatures.Verify(invalid, publicKey,
            FoundationBackupSignatures.Authority(invalid), f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
        var whitespace = signed.Manifest with { Signature = signed.Manifest.Signature + "\n" };
        Assert.Equal("InvalidSignedBackupManifest", Error(() => FoundationBackupSignatures.CanonicalPayload(whitespace)));
    }

    static string Error(Action action) => Assert.Throws<FoundationStoreException>(action).Code;
}

internal sealed class SignedBackupFixture : IDisposable
{
    readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    public StoreFixture Cell { get; } = new();
    public RSA Key { get; } = RSA.Create(2048);
    public string Path(string name)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Cell.Path)!, name); paths.Add(path); return path;
    }
    public string Target() => Path("restored-" + Guid.NewGuid().ToString("N") + ".sqlite");
    public string AbsentTarget() => Path(System.IO.Path.Combine("uncreated-" + Guid.NewGuid().ToString("N"), "restore.sqlite"));
    public RSA PublicKey() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _); return key; }
    public FoundationSignedBackup Create(long generation = 1) => Cell.Store.CreateSignedBackup(Path("snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite"),
        Key, "backup-key", generation, Cell.Clock.GetUtcNow(), Cell.Clock.GetUtcNow().AddDays(1));
    public FoundationBackupSignature Sign(FoundationBackupSignature manifest) => manifest with
    { Signature = Convert.ToBase64String(Key.SignData(FoundationBackupSignatures.CanonicalPayload(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
    public void Dispose()
    {
        Key.Dispose();
        foreach (var path in paths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            var parent = System.IO.Path.GetDirectoryName(path)!;
            if (parent != System.IO.Path.GetDirectoryName(Cell.Path) && Directory.Exists(parent)) Directory.Delete(parent, false);
        }
        Cell.Dispose();
    }
}
