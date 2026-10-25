using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationBackupCheckpointRecoveryTests
{
    [WindowsCredentialFact]
    public void Verified_checkpoint_is_taken_from_configured_custody_and_verification_changes_no_source_bytes_or_audit()
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); var head = f.Accept(packet); var before = f.Snapshot();
        using var key = f.PublicKey(); var result = f.Recovery.VerifySignedBackup(packet, key);
        Assert.Equal(head, result.Checkpoint); Assert.Equal(packet.Backup.Sha256, result.Backup.SnapshotSha256);
        Assert.Null(result.Store); Assert.False(result.RecoveredWorkHeldForReconciliation); AssertLimits(result, f);
        Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public void Restore_creates_only_fresh_private_stage_and_holds_copied_work_without_activating_or_mutating_source()
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet);
        var sourceInstance = f.Store.GetCellInstanceIdentity(); var before = f.Snapshot(); var stage = f.Stage();
        using var key = f.PublicKey(); var result = f.Recovery.RestoreSignedBackup(packet, stage, key); var restored = result.Store!;
        Assert.NotNull(restored); Assert.True(result.RecoveredWorkHeldForReconciliation); AssertPrivate(stage);
        Assert.Equal(new[] { ".foundation-owner-initialization.json", "cell.sqlite" }, Directory.GetFiles(stage).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal));
        var marker = File.ReadAllText(Path.Combine(stage, ".foundation-owner-initialization.json"));
        Assert.Contains("platform-studio.foundation-backup-checkpoint-recovery-stage.v1", marker); Assert.DoesNotContain(f.Cell.Root, marker);
        Assert.Contains(result.Checkpoint.Sha256, marker); Assert.DoesNotContain(CredentialFixture.Token, marker);
        Assert.NotEqual(sourceInstance.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
        var copied = restored.GetJob(f.Customer, "operator", f.Cell.Dispatch.Job.Id);
        Assert.Equal("OutcomeUnknown", copied.State); Assert.Equal("RecoveryRequiresReconciliation", copied.Reason);
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(f.Customer, "restored-worker", TimeSpan.FromMinutes(1))).Code);
        Assert.True(restored.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.Contains(restored.ListAudit(f.Customer, "auditor"), a => a.Action == "BackupRestored" && a.CorrelationId == packet.Backup.Sha256);
        Assert.Equal(before, f.Snapshot(exclude: stage)); AssertLimits(result, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Previously_valid_packet_and_its_own_matching_legacy_DTO_cannot_override_newer_durable_checkpoint(bool restore)
    {
        using var f = new RecoveryFixture(); var old = f.Create(); f.Accept(old); var latest = f.Create(2); f.Accept(latest);
        using var key = f.PublicKey(); var suppliedOldDto = FoundationBackupSignatures.Authority(old.Manifest);
        _ = FoundationStore.VerifySignedBackup(old, suppliedOldDto, key, f.Customer, f.CellId, f.Cell.Clock);
        var before = f.Snapshot(); var stage = f.Stage();
        var error = Assert.Throws<FoundationStoreException>(() => restore ? f.Recovery.RestoreSignedBackup(old, stage, key) : f.Recovery.VerifySignedBackup(old, key));
        Assert.Equal("SignedBackupLatestAuthorityMismatch", error.Code); Assert.False(Path.Exists(stage)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("authority")] [InlineData("key")] [InlineData("public-key")] [InlineData("commitment")]
    public void Exact_configured_authority_identity_is_required_before_any_source_or_public_key_access(string field)
    {
        using var f = new RecoveryFixture(); var identity = f.Identity;
        var wrong = field switch
        {
            "customer" => identity with { CustomerId = "foreign-customer" }, "cell" => identity with { CellId = "foreign-cell" },
            "authority" => identity with { AuthorityId = Guid.NewGuid().ToString("N") }, "key" => identity with { SignerKeyId = "foreign-key" },
            "public-key" => identity with { PublicKeySha256 = new string('a', 64) }, _ => identity with { CommitmentSha256 = new string('b', 64) }
        };
        var before = f.Snapshot(); var error = Assert.Throws<FoundationStoreException>(() => new FoundationBackupCheckpointRecovery(f.Authority, wrong, f.Cell.Clock));
        Assert.Equal("BackupCheckpointIdentityMismatch", error.Code); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Empty_authority_refuses_without_touching_source_or_creating_a_stage(bool restore)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); var stage = f.Stage(); var before = f.Snapshot(); var exports = 0;
        using var key = new ObservedPublicKey(f.Key, () => exports++);
        Assert.Equal("BackupCheckpointMissing", Assert.Throws<FoundationStoreException>(() => restore ?
            f.Recovery.RestoreSignedBackup(packet, stage, key) : f.Recovery.VerifySignedBackup(packet, key)).Code);
        Assert.Equal(0, exports); Assert.False(Path.Exists(stage)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("bytes")] [InlineData("missing-source")] [InlineData("foreign-carrier")] [InlineData("expired")]
    [InlineData("wal-file")] [InlineData("journal-directory")]
    public void Current_head_never_substitutes_for_fresh_full_packet_and_standalone_snapshot_verification(string attack)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var stage = f.Stage();
        if (attack == "bytes") { using var file = new FileStream(packet.Backup.DatabasePath, FileMode.Open, FileAccess.Write); file.Position = 200; file.WriteByte(0x7f); }
        else if (attack == "missing-source") File.Delete(packet.Backup.DatabasePath);
        else if (attack == "foreign-carrier") packet = packet with { Backup = packet.Backup with { CustomerId = "other-customer" } };
        else if (attack == "expired") f.Cell.Clock.Now = f.Cell.Clock.Now.AddDays(2);
        else if (attack == "wal-file") File.WriteAllText(packet.Backup.DatabasePath + "-wal", "synthetic uncommitted sidecar");
        else Directory.CreateDirectory(packet.Backup.DatabasePath + "-journal");
        var before = f.Snapshot(); using var key = f.PublicKey();
        var verify = Assert.Throws<FoundationStoreException>(() => f.Recovery.VerifySignedBackup(packet, key));
        var restore = Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key));
        Assert.Equal(verify.Code, restore.Code); Assert.False(Path.Exists(stage)); Assert.Equal(before, f.Snapshot());
        Assert.DoesNotContain(f.Cell.Root, verify.ToString()); Assert.DoesNotContain(CredentialFixture.Token, restore.ToString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_or_corrupt_authority_is_never_recreated_or_replaced_by_packet_latest_metadata(bool missing)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet);
        if (missing) File.Delete(Path.Combine(f.AuthorityPath, ".backup-checkpoint.guard"));
        else File.WriteAllText(Path.Combine(f.AuthorityPath, "authority.json"), "{\"synthetic\":\"corrupt immutable authority\"}");
        var before = f.Snapshot(); var stage = f.Stage(); using var key = f.PublicKey();
        Assert.Throws<FoundationStoreException>(() => f.Recovery.VerifySignedBackup(packet, key));
        Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key));
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(stage));
    }

    [Theory]
    [InlineData("empty-directory")] [InlineData("unrelated-file")] [InlineData("missing-parent")]
    [InlineData("relative")] [InlineData("unc")] [InlineData("dot")] [InlineData("source-wal")]
    public void Existing_and_unsafe_stage_destinations_are_refused_without_adoption_overwrite_parent_creation_or_source_changes(string kind)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var stage = f.Stage();
        if (kind == "empty-directory") Directory.CreateDirectory(stage);
        else if (kind == "unrelated-file") File.WriteAllText(stage, "synthetic preserved owner file");
        else if (kind == "missing-parent") stage = Path.Combine(stage, "missing", "stage");
        else if (kind == "relative") stage = "not-absolute-stage";
        else if (kind == "unc") stage = "\\\\localhost\\not-a-share\\stage";
        else if (kind == "dot") stage = Path.Combine(f.Cell.Root, ".", "stage");
        else stage = packet.Backup.DatabasePath + "-wal";
        var before = f.Snapshot(); using var key = f.PublicKey();
        Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(".backup-checkpoint.guard", false)] [InlineData(".backup-checkpoint.guard", true)]
    [InlineData(".credential-recovery-stage.guard", false)] [InlineData(".credential-recovery-stage.guard", true)]
    [InlineData(".credential-recovery-stage.json", false)] [InlineData(".credential-recovery-stage.json", true)]
    [InlineData(".credential-recovery-checkpoint.guard", false)] [InlineData(".credential-recovery-checkpoint.guard", true)]
    [InlineData(".credential-recovery-checkpoint.json", false)] [InlineData(".credential-recovery-checkpoint.json", true)]
    [InlineData(".vault.lock", false)] [InlineData(".vault.lock", true)]
    [InlineData(".foundation-owner-initialization.json", false)] [InlineData(".foundation-owner-initialization.json", true)]
    [InlineData("legacy.CREDENTIAL.JSON", true)] [InlineData("legacy.DPAPI.JSON", false)]
    public void Protected_ancestor_names_as_file_or_directory_are_preserved_and_never_receive_stage_entries(string marker, bool directory)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var parent = Path.Combine(f.Cell.Root, "unrelated-owner-namespace");
        Directory.CreateDirectory(parent); var sentinel = Path.Combine(parent, marker);
        if (directory) Directory.CreateDirectory(sentinel); else File.WriteAllText(sentinel, "synthetic marker bytes");
        var child = Path.Combine(parent, "ordinary-child"); Directory.CreateDirectory(child); var stage = Path.Combine(child, "restore-stage");
        var before = f.Snapshot(); using var key = f.PublicKey();
        Assert.Equal("BackupCheckpointRecoveryProtectedNamespace", Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key)).Code);
        Assert.False(Path.Exists(stage)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)] [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    public void Foreign_writable_parent_custody_is_refused_without_repair_or_stage_creation(FileSystemRights rights)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var parent = Path.Combine(f.Cell.Root, "widened-parent"); Directory.CreateDirectory(parent);
        var acl = new DirectoryInfo(parent).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl); var before = f.Snapshot(); using var key = f.PublicKey(); var stage = Path.Combine(parent, "stage");
        Assert.Equal("BackupCheckpointRecoveryParentCustodyRejected", Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key)).Code);
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(stage));
    }

    [WindowsCredentialFact]
    public void Null_parent_dacl_is_refused_without_creating_stage_or_repairing_the_synthetic_parent()
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var parent = Path.Combine(f.Cell.Root, "null-dacl-parent"); Directory.CreateDirectory(parent);
        var original = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(); var stage = Path.Combine(parent, "stage");
        void SetAccess(byte[] bytes) { var security = new DirectorySecurity(); security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); new DirectoryInfo(parent).SetAccessControl(security); }
        try
        {
            var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0); SetAccess(bytes);
            Assert.Null(new RawSecurityDescriptor(new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
            var before = f.Snapshot(); using var key = f.PublicKey();
            Assert.Equal("BackupCheckpointRecoveryParentCustodyRejected", Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key)).Code);
            Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(stage));
        }
        finally { SetAccess(original); }
        Assert.NotNull(new RawSecurityDescriptor(new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Safe_parent_read_or_nonapplying_inherit_only_write_does_not_prevent_private_stage_creation(bool inheritOnly)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var parent = Path.Combine(f.Cell.Root, "safe-shared-read-parent"); Directory.CreateDirectory(parent);
        var acl = new DirectoryInfo(parent).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), inheritOnly ? FileSystemRights.Write : FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, inheritOnly ? PropagationFlags.InheritOnly : PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl); var security = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var stage = Path.Combine(parent, "stage"); using var key = f.PublicKey(); var result = f.Recovery.RestoreSignedBackup(packet, stage, key);
        Assert.NotNull(result.Store); AssertPrivate(stage); Assert.Equal(security, new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm());
        var markerSecurity = new FileInfo(Path.Combine(stage, ".foundation-owner-initialization.json")).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent(); Assert.True(markerSecurity.AreAccessRulesProtected);
        Assert.All(markerSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => Assert.Equal(identity.User!.Value, rule.IdentityReference.Value));
    }

    [WindowsCredentialFact]
    public async Task Concurrent_restores_into_one_fresh_stage_produce_one_owned_recovery_and_never_adopt_the_winner()
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var before = f.Snapshot(); var stage = f.Stage();
        using var barrier = new Barrier(2);
        async Task<(FoundationBackupCheckpointRecoveryResult? Result, string? Error)> Attempt()
        {
            var observed = 0; using var key = new ObservedPublicKey(f.Key, () =>
            { if (Interlocked.Exchange(ref observed, 1) == 0 && !barrier.SignalAndWait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Synthetic restore rendezvous expired."); });
            return await Task.Run(() =>
            {
                try { return (f.Recovery.RestoreSignedBackup(packet, stage, key), (string?)null); }
                catch (FoundationStoreException error) { return ((FoundationBackupCheckpointRecoveryResult?)null, error.Code); }
            });
        }
        var attempts = await Task.WhenAll(Attempt(), Attempt()).WaitAsync(TimeSpan.FromSeconds(15));
        var winner = Assert.Single(attempts, attempt => attempt.Result is not null); var loser = Assert.Single(attempts, attempt => attempt.Result is null);
        Assert.Contains(loser.Error, new[] { "BackupCheckpointRecoveryDestinationExists", "BackupCheckpointRecoveryDestinationReservationRejected" });
        Assert.NotNull(winner.Result!.Store); AssertPrivate(stage); Assert.Equal(before, f.Snapshot(exclude: stage));
        Assert.Equal(1, winner.Result.Store!.ListAudit(f.Customer, "auditor").Count(a => a.Action == "BackupRestored"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Latest_checkpoint_advance_and_source_replacement_are_blocked_until_full_verification_or_staged_restore_finishes(bool restore)
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); var initial = f.Accept(packet); var next = f.Create(2); var stage = f.Stage();
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var key = new ObservedPublicKey(f.Key, () =>
        {
            if (!restore || Directory.Exists(stage)) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic public-key pause expired."); }
        });
        var operation = Task.Run(() => restore ? f.Recovery.RestoreSignedBackup(packet, stage, key) : f.Recovery.VerifySignedBackup(packet, key));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal("BackupCheckpointBusy", Assert.Throws<FoundationStoreException>(() => f.Accept(next)).Code);
            Assert.Equal(initial, f.Authority.ReadCurrent());
            Assert.Throws<IOException>(() => new FileStream(packet.Backup.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            if (restore) { AssertPrivate(stage); Assert.Equal(".foundation-owner-initialization.json", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(stage)))); }
        }
        finally { release.Set(); }
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(initial, result.Checkpoint);
        Assert.Equal(2, f.Accept(next).Sequence); AssertLimits(result, f);
    }

    [WindowsCredentialFact]
    public void Failure_after_atomic_stage_reservation_retains_only_that_fresh_private_stage_for_inspection()
    {
        using var f = new RecoveryFixture(); var packet = f.Create(); f.Accept(packet); var stage = f.Stage(); var before = f.Snapshot();
        using var key = new ObservedPublicKey(f.Key, () => { if (Directory.Exists(stage)) throw new IOException("synthetic local public-key failure"); });
        Assert.Equal("BackupCheckpointRecoveryUnavailable", Assert.Throws<FoundationStoreException>(() => f.Recovery.RestoreSignedBackup(packet, stage, key)).Code);
        AssertPrivate(stage); Assert.Equal(".foundation-owner-initialization.json", Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(stage))));
        Assert.Equal(before, f.Snapshot(exclude: stage));
    }

    static void AssertLimits(FoundationBackupCheckpointRecoveryResult result, RecoveryFixture f)
    {
        Assert.True(result.LocalCustodyOnly); Assert.False(result.SourceHostFenced); Assert.False(result.ExternalExecutionAuthorized);
        Assert.False(result.ActivationPerformed); Assert.False(result.KeyRetirementAuthorized); Assert.False(result.PrivateKeyBackupVerified);
        Assert.False(result.ProductionRecoveryQualified); Assert.Equal("NotAssessed", result.IndependentBackupCoverage);
        var json = JsonSerializer.Serialize(result); Assert.DoesNotContain(f.Cell.Root, json); Assert.DoesNotContain(CredentialFixture.Token, json);
        Assert.DoesNotContain("databaseInstanceId", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("\"store\"", json, StringComparison.OrdinalIgnoreCase);
    }
    static void AssertPrivate(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var security = new DirectoryInfo(path).GetAccessControl();
        Assert.Equal(identity.User!.Value, security.GetOwner(typeof(SecurityIdentifier))!.Value); Assert.True(security.AreAccessRulesProtected);
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        { Assert.Equal(identity.User.Value, rule.IdentityReference.Value); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); });
    }

    sealed class ObservedPublicKey : RSA
    {
        readonly RSA inner; readonly Action onExport;
        public ObservedPublicKey(RSA source, Action onExport) { inner = RSA.Create(); inner.ImportSubjectPublicKeyInfo(source.ExportSubjectPublicKeyInfo(), out _); this.onExport = onExport; }
        public override int KeySize { get => inner.KeySize; set => throw new NotSupportedException(); }
        public override RSAParameters ExportParameters(bool includePrivateParameters) { if (includePrivateParameters) throw new NotSupportedException(); onExport(); return inner.ExportParameters(false); }
        public override byte[] ExportSubjectPublicKeyInfo() { onExport(); return inner.ExportSubjectPublicKeyInfo(); }
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => throw new NotSupportedException();
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => inner.VerifyHash(hash, signature, hashAlgorithm, padding);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    sealed class RecoveryFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public RSA Key { get; } = RSA.Create(2048);
        public FoundationStore Store { get; }
        public FoundationBackupCheckpointAuthority Authority { get; }
        public FoundationBackupCheckpointAuthorityIdentity Identity { get; }
        public FoundationBackupCheckpointRecovery Recovery { get; }
        public string Customer => Cell.Scope.CustomerId;
        public string CellId => Cell.Scope.CellId;
        public string AuthorityPath => Path.Combine(Cell.Root, "backup-checkpoint-authority");
        public RecoveryFixture()
        {
            Store = new(Path.Combine(Cell.Root, "cell.db"), CellId, Customer, Cell.Clock); Store.SetMembership(Customer, "admin", "auditor", "default", "dev", ["Auditor"]);
            Authority = FoundationBackupCheckpointAuthority.Provision(AuthorityPath, Customer, CellId, "backup-key", FoundationAuditAnchors.PublicKeyFingerprint(Key), Cell.Clock);
            Identity = Authority.DescribeIdentity(); Recovery = new(Authority, Identity, Cell.Clock);
        }
        public string Stage() => Path.Combine(Cell.Root, "recovered-" + Guid.NewGuid().ToString("N"));
        public FoundationSignedBackup Create(long generation = 1) => Store.CreateSignedBackup(Path.Combine(Cell.Root, "snapshot-" + Guid.NewGuid().ToString("N") + ".sqlite"),
            Key, "backup-key", generation, Cell.Clock.Now, Cell.Clock.Now.AddDays(1));
        public RSA PublicKey() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _); return key; }
        public FoundationBackupCheckpoint Accept(FoundationSignedBackup packet)
        {
            var head = Authority.ReadCurrent(); using var key = PublicKey();
            return Authority.AcceptNext(packet, key, FoundationBackupSignatures.Authority(packet.Manifest).ManifestSha256, head?.Sequence ?? 0, head?.Sha256);
        }
        public string[] Snapshot(string? exclude = null)
        {
            var entries = new List<string>(); Walk(Cell.Root, entries, exclude); return entries.Order(StringComparer.Ordinal).ToArray();
        }
        void Walk(string path, List<string> entries, string? exclude)
        {
            if (path == exclude) return;
            var directory = Directory.Exists(path); var relative = Path.GetRelativePath(Cell.Root, path);
            FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var security = Convert.ToHexStringLower(SHA256.HashData(acl.GetSecurityDescriptorBinaryForm()));
            if (directory)
            {
                entries.Add(relative + ":directory:" + security);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child, entries, exclude);
            }
            else
            {
                var length = new FileInfo(path).Length; var bytes = (Path.GetFileName(path) is ".vault.lock" or ".backup-checkpoint.guard") && length == 0 ? [] : File.ReadAllBytes(path);
                entries.Add(relative + ":file:" + security + ":" + length + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        public void Dispose() { Key.Dispose(); Cell.Dispose(); }
    }
}
