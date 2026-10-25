using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationBackupCheckpointAuthorityTests
{
    [WindowsCredentialFact]
    public void Exact_reviewed_backup_advances_private_checkpoint_without_copying_source_or_changing_cell_audit()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); var identity = authority.DescribeIdentity(); var candidate = f.Backup(7);
        Assert.Null(authority.ReadCurrent()); Assert.Equal("BackupCheckpointMissing", Assert.Throws<FoundationStoreException>(() => authority.AcquireReadLease()).Code);
        var bytes = File.ReadAllBytes(candidate.Backup.DatabasePath); var audit = f.Cell.Scalar("SELECT count(*) FROM audit");
        using var key = f.PublicKey(); var accepted = authority.AcceptNext(candidate, key, Review(candidate), 0, null);
        Assert.Equal(1, accepted.Sequence); Assert.Equal("", accepted.PreviousSha256); Assert.Equal(identity.AuthorityId, accepted.AuthorityId);
        Assert.Equal(identity.CommitmentSha256, accepted.AuthorityCommitmentSha256); Assert.Equal(FoundationBackupSignatures.Authority(candidate.Manifest), accepted.RestoreAuthority);
        Assert.Equal(accepted, authority.ReadCurrent()); Assert.Equal(accepted, f.Reopen().ReadCurrent()); Assert.Equal(CheckpointDigest(accepted), accepted.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(candidate.Backup.DatabasePath)); Assert.Equal(audit, f.Cell.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(new[] { ".backup-checkpoint.guard", "authority.json", "checkpoint-0001.json" }, Directory.GetFiles(f.AuthorityPath).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal));
        AssertPrivate(f.AuthorityPath, true); foreach (var file in Directory.GetFiles(f.AuthorityPath)) AssertPrivate(file, false);
        using var windowsIdentity = WindowsIdentity.GetCurrent();
        var text = JsonSerializer.Serialize(new { identity, accepted }); Assert.DoesNotContain(f.Root, text); Assert.DoesNotContain(windowsIdentity.User!.Value, text);
        Assert.DoesNotContain("BEGIN", text); Assert.DoesNotContain("signature", text, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsCredentialFact]
    public void Backup_generation_is_strictly_increasing_while_local_sequence_advances_one_and_history_is_retained()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); using var key = f.PublicKey(); var firstPacket = f.Backup(3);
        var first = authority.AcceptNext(firstPacket, key, Review(firstPacket), 0, null); var firstBytes = File.ReadAllBytes(Path.Combine(f.AuthorityPath, "checkpoint-0001.json"));
        var nextPacket = f.Backup(11); var next = authority.AcceptNext(nextPacket, key, Review(nextPacket), first.Sequence, first.Sha256);
        Assert.Equal(2, next.Sequence); Assert.Equal(11, next.RestoreAuthority.Generation); Assert.Equal(first.Sha256, next.PreviousSha256);
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(f.AuthorityPath, "checkpoint-0001.json"))); Assert.Equal(next, f.Reopen().ReadCurrent());
        var before = f.Tree();
        Assert.Equal("BackupCheckpointGenerationChanged", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(nextPacket, key, Review(nextPacket), first.Sequence, first.Sha256)).Code);
        Assert.Equal("BackupCheckpointBackupGenerationNotIncreasing", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(firstPacket, key, Review(firstPacket), next.Sequence, next.Sha256)).Code);
        Assert.Equal(before, f.Tree());
    }

    [WindowsCredentialFact]
    public void Multiple_shared_read_leases_exclude_every_cooperating_head_advance_until_all_readers_release()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); using var key = f.PublicKey(); var firstPacket = f.Backup(1);
        var first = authority.AcceptNext(firstPacket, key, Review(firstPacket), 0, null); var nextPacket = f.Backup(2);
        var one = authority.AcquireReadLease(); var two = f.Reopen().AcquireReadLease();
        try
        {
            one.AssertCurrent(); two.AssertCurrent(); Assert.Equal(first, one.Checkpoint); Assert.Equal(authority.DescribeIdentity(), one.Identity);
            Assert.Equal("BackupCheckpointBusy", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(nextPacket, key, Review(nextPacket), first.Sequence, first.Sha256)).Code);
            one.Dispose(); Assert.Equal("BackupCheckpointLeaseDisposed", Assert.Throws<FoundationStoreException>(() => one.AssertCurrent()).Code);
            Assert.Equal("BackupCheckpointBusy", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(nextPacket, key, Review(nextPacket), first.Sequence, first.Sha256)).Code);
        }
        finally { one.Dispose(); two.Dispose(); }
        Assert.Equal(2, authority.AcceptNext(nextPacket, key, Review(nextPacket), first.Sequence, first.Sha256).Sequence);
    }

    [WindowsCredentialFact]
    public void Independent_review_digest_and_cas_refusals_do_not_open_an_unreviewed_source_or_change_checkpoint_state()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); var packet = f.Backup(1); using var key = f.PublicKey(); var before = f.Tree();
        var unreviewed = packet with { Backup = packet.Backup with { DatabasePath = "\\\\192.0.2.1\\unreachable\\snapshot.sqlite" } };
        Assert.Equal("BackupCheckpointReviewedManifestMismatch", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(unreviewed, key, new string('0', 64), 0, null)).Code);
        Assert.Equal("BackupCheckpointExpectedStateInvalid", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(unreviewed, key, Review(packet), 0, new string('a', 64))).Code);
        Assert.Equal(before, f.Tree()); Assert.Null(authority.ReadCurrent());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Neither_a_new_signer_fingerprint_nor_a_new_key_identifier_is_implicit_key_rotation(bool newKey)
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); var first = f.Backup(1); using var originalPublic = f.PublicKey();
        var head = authority.AcceptNext(first, originalPublic, Review(first), 0, null); using var replacement = RSA.Create(2048);
        var candidate = f.Backup(2, newKey ? replacement : f.Key, newKey ? f.KeyId : "different-signer-id");
        using var publicKey = RSA.Create(); publicKey.ImportSubjectPublicKeyInfo((newKey ? replacement : f.Key).ExportSubjectPublicKeyInfo(), out _); var before = f.Tree();
        Assert.Equal("BackupCheckpointSignerBindingMismatch", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(candidate, publicKey, Review(candidate), head.Sequence, head.Sha256)).Code);
        Assert.Equal(before, f.Tree()); Assert.Equal(head, authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public void Reusing_an_accepted_manifest_identity_is_refused_before_publishing_an_invalid_history()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); using var key = f.PublicKey(); var firstPacket = f.Backup(1);
        var head = authority.AcceptNext(firstPacket, key, Review(firstPacket), 0, null); var second = f.Backup(2);
        var manifest = second.Manifest with { ManifestId = firstPacket.Manifest.ManifestId, Signature = "" };
        manifest = manifest with { Signature = Convert.ToBase64String(f.Key.SignData(FoundationBackupSignatures.CanonicalPayload(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
        second = second with { Manifest = manifest }; var before = f.Tree();
        Assert.Equal("BackupCheckpointManifestAlreadyAccepted", Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(second, key, Review(second), head.Sequence, head.Sha256)).Code);
        Assert.Equal(before, f.Tree()); Assert.Equal(head, f.Reopen().ReadCurrent());
    }

    [WindowsCredentialFact]
    public void Copied_header_cannot_rebind_the_configured_exact_path_or_reuse_expected_identity_commitment()
    {
        using var f = new CheckpointFixture(); var authority = f.Provision(); var expected = authority.DescribeIdentity(); var copy = Path.Combine(f.Root, "copied-checkpoints-" + Guid.NewGuid().ToString("N"));
        ActivationProvisionFixtureRoots.Create(copy);
        try
        {
            foreach (var path in Directory.GetFiles(f.AuthorityPath))
            {
                var destination = Path.Combine(copy, Path.GetFileName(path));
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = NewPrivateFile(destination)) input.CopyTo(output);
            }
            Assert.Throws<FoundationStoreException>(() => new FoundationBackupCheckpointAuthority(copy, f.Cell.Customer, f.Cell.Cell, f.KeyId, f.Pin, f.Cell.Clock));
            Assert.Equal(expected, authority.DescribeIdentity());
        }
        finally
        {
            var full = Path.GetFullPath(copy); var expectedParent = Path.GetFullPath(f.Root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("copied-checkpoints-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(full)[^32..], "N", out _))
                throw new InvalidOperationException("Synthetic copied checkpoint cleanup boundary rejected.");
            Directory.Delete(full, recursive: true);
        }
    }

    static string Review(FoundationSignedBackup packet) => FoundationBackupSignatures.Authority(packet.Manifest).ManifestSha256;
    static string CheckpointDigest(FoundationBackupCheckpoint checkpoint) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = 1, kind = "backup-checkpoint", checkpoint.AuthorityId, checkpoint.AuthorityCommitmentSha256, checkpoint.Sequence, checkpoint.PreviousSha256,
        acceptedUnixMilliseconds = checkpoint.AcceptedUtc.ToUnixTimeMilliseconds(), checkpoint.RestoreAuthority }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    static FileStream NewPrivateFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = new FileSecurity(); acl.SetOwner(identity.User!); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, acl);
    }
    static void AssertPrivate(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(identity.User!.Value, r.IdentityReference.Value));
    }

    sealed class CheckpointFixture : IDisposable
    {
        readonly List<string> packets = [];
        public StoreFixture Cell { get; } = new();
        public RSA Key { get; } = RSA.Create(2048);
        public string KeyId => "checkpoint-test-key";
        public string Pin => FoundationAuditAnchors.PublicKeyFingerprint(Key);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "studio-checkpoint-basic-" + Guid.NewGuid().ToString("N"));
        public string AuthorityPath => Path.Combine(Root, "checkpoints");
        public CheckpointFixture() => ActivationProvisionFixtureRoots.Create(Root);
        public FoundationBackupCheckpointAuthority Provision() => FoundationBackupCheckpointAuthority.Provision(AuthorityPath, Cell.Customer, Cell.Cell, KeyId, Pin, Cell.Clock);
        public FoundationBackupCheckpointAuthority Reopen() => new(AuthorityPath, Cell.Customer, Cell.Cell, KeyId, Pin, Cell.Clock);
        public RSA PublicKey() { var copy = RSA.Create(); copy.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _); return copy; }
        public FoundationSignedBackup Backup(long generation, RSA? key = null, string? keyId = null)
        {
            var path = Path.Combine(Root, "checkpoint-snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite"); packets.Add(path);
            return Cell.Store.CreateSignedBackup(path, key ?? Key, keyId ?? KeyId, generation, Cell.Clock.GetUtcNow(), Cell.Clock.GetUtcNow().AddHours(1));
        }
        public string[] Tree() => Directory.GetFileSystemEntries(AuthorityPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(AuthorityPath, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        public void Dispose()
        {
            Key.Dispose();
            var full = Path.GetFullPath(AuthorityPath); var expected = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != "checkpoints") throw new InvalidOperationException("Synthetic checkpoint cleanup boundary rejected.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            foreach (var packet in packets) foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(packet + suffix)) File.Delete(packet + suffix);
            Directory.Delete(Root, recursive: false);
            Cell.Dispose();
        }
    }
}
