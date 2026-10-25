using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

/// <summary>A local observation made with the configured checkpoint authority retained through verification or staged recovery.</summary>
public sealed class FoundationBackupCheckpointRecoveryResult
{
    internal FoundationBackupCheckpointRecoveryResult(FoundationBackupCheckpoint checkpoint, ValidatedFoundationBackup backup, FoundationStore? store)
    { Checkpoint = checkpoint; Backup = backup; Store = store; }
    public string Schema => "platform-studio.foundation-backup-checkpoint-recovery.v1";
    public FoundationBackupCheckpoint Checkpoint { get; }
    public ValidatedFoundationBackup Backup { get; }
    [JsonIgnore] public FoundationStore? Store { get; }
    public bool RecoveredWorkHeldForReconciliation => Store is not null;
    public bool LocalCustodyOnly => true;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool KeyRetirementAuthorized => false;
    public bool PrivateKeyBackupVerified => false;
    public bool ProductionRecoveryQualified => false;
    public string IndependentBackupCoverage => "NotAssessed";
}

/// <summary>Explicit trusted-owner composition. Incoming backup packets never select their own latest checkpoint.</summary>
[SupportedOSPlatform("windows")]
public sealed class FoundationBackupCheckpointRecovery
{
    readonly FoundationBackupCheckpointAuthority authority;
    readonly FoundationBackupCheckpointAuthorityIdentity expectedIdentity;
    readonly TimeProvider clock;

    public FoundationBackupCheckpointRecovery(FoundationBackupCheckpointAuthority authority,
        FoundationBackupCheckpointAuthorityIdentity expectedIdentity, TimeProvider? timeProvider = null)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("BackupCheckpointRecoveryWindowsRequired", 403);
        this.authority = authority ?? throw Failure("BackupCheckpointIdentityMismatch", 403);
        this.expectedIdentity = expectedIdentity ?? throw Failure("BackupCheckpointIdentityMismatch", 403);
        clock = timeProvider ?? TimeProvider.System;
        if (authority.DescribeIdentity() != expectedIdentity) throw Failure("BackupCheckpointIdentityMismatch", 403);
    }

    public FoundationBackupCheckpointRecoveryResult VerifySignedBackup(FoundationSignedBackup backup, RSA pinnedPublicKey)
    {
        try
        {
            using var lease = authority.AcquireReadLease(); RequireIdentity(lease);
            using var source = OpenSource(backup);
            var verified = Verify(backup, pinnedPublicKey, lease);
            lease.AssertCurrent();
            return new(lease.Checkpoint, verified, null);
        }
        catch (Exception error) when (IsLocalFailure(error)) { throw Failure("BackupCheckpointRecoveryUnavailable", 403); }
    }

    // Fresh directory only: invalid packets create no staging directory; later failures retain this owned stage as evidence.
    public FoundationBackupCheckpointRecoveryResult RestoreSignedBackup(FoundationSignedBackup backup, string destinationDirectory, RSA pinnedPublicKey)
    {
        try
        {
            using var lease = authority.AcquireReadLease(); RequireIdentity(lease);
            using var source = OpenSource(backup);
            var verified = Verify(backup, pinnedPublicKey, lease);
            var directory = ValidateStage(destinationDirectory);
            lease.AssertCurrent();
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw Failure("BackupCheckpointRecoveryParentCustodyRejected", 403);
            if (!CredentialNamespaceCustody.CreateDirectory(directory, sid)) throw Failure("BackupCheckpointRecoveryDestinationReservationRejected");
            // No repair, overwrite, parent creation, activation or cleanup of the newly reserved directory.
            RequirePrivateStage(directory, sid.Value);
            using (var marker = CredentialNamespaceCustody.CreateFile(Path.Combine(directory, ".foundation-owner-initialization.json")))
            {
                JsonSerializer.Serialize(marker, new
                {
                    schema = "platform-studio.foundation-backup-checkpoint-recovery-stage.v1", kind = "backup-checkpoint-recovery-stage",
                    expectedIdentity.CustomerId, expectedIdentity.CellId, expectedIdentity.AuthorityId, expectedIdentity.CommitmentSha256,
                    checkpointSha256 = lease.Checkpoint.Sha256, checkpointSequence = lease.Checkpoint.Sequence,
                    verified.ManifestId, verified.ManifestSha256, verified.SnapshotSha256, activationPerformed = false
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                marker.Flush(flushToDisk: true);
            }
            var restored = FoundationStore.RestoreSignedBackup(backup, Path.Combine(directory, "cell.sqlite"),
                lease.Checkpoint.RestoreAuthority, pinnedPublicKey, expectedIdentity.CustomerId, expectedIdentity.CellId, clock);
            RequirePrivateStage(directory, sid.Value); lease.AssertCurrent();
            return new(lease.Checkpoint, verified, restored);
        }
        catch (Exception error) when (IsLocalFailure(error)) { throw Failure("BackupCheckpointRecoveryUnavailable", 403); }
    }

    ValidatedFoundationBackup Verify(FoundationSignedBackup backup, RSA key, FoundationBackupCheckpointReadLease lease) =>
        FoundationStore.VerifySignedBackup(backup, lease.Checkpoint.RestoreAuthority, key,
            expectedIdentity.CustomerId, expectedIdentity.CellId, clock);

    void RequireIdentity(FoundationBackupCheckpointReadLease lease)
    { if (lease.Identity != expectedIdentity) throw Failure("BackupCheckpointIdentityMismatch", 403); lease.AssertCurrent(); }

    static FileStream OpenSource(FoundationSignedBackup backup)
    {
        if (backup?.Backup is null || backup.Manifest is null) throw Failure("InvalidSignedBackupManifest", 400);
        var path = LocalPath(backup.Backup.DatabasePath);
        if (!File.Exists(path) || Directory.Exists(path)) throw Failure("BackupCheckpointRecoverySourceRejected", 403);
        // Windows sharing excludes a normal writer, replacement and deletion until both full verification and restore finish.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    static string LocalPath(string value)
    {
        try { return CredentialNamespaceCustody.LocalPath(value); }
        catch (CredentialException) { throw Failure("BackupCheckpointRecoveryPathRejected", 403); }
    }

    static readonly string[] Sentinels = [".activation.guard", "authority.json", ".backup-checkpoint.guard", ".installation-owner.json", ".installation.guard",
        ".package-owner.json", ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", "cell-binding.json", ".foundation-owner-initialization.json",
        ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json"];
    static bool ProtectedName(string name) => Sentinels.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase);
    static string ValidateStage(string value)
    {
        var path = LocalPath(value);
        if (ProtectedName(Path.GetFileName(path)) || new[] { "-wal", "-shm", "-journal" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw Failure("BackupCheckpointRecoveryProtectedNamespace", 403);
        if (Path.Exists(path)) throw Failure("BackupCheckpointRecoveryDestinationExists");
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) throw Failure("BackupCheckpointRecoveryParentMissing", 403);
        var depth = 0;
        for (var ancestor = parent; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (++depth > 128) throw Failure("BackupCheckpointRecoveryAncestorLimit", 403);
            var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(ancestor))
            {
                if (++count > 65536) throw Failure("BackupCheckpointRecoveryAncestorLimit", 403);
                if (ProtectedName(Path.GetFileName(entry))) throw Failure("BackupCheckpointRecoveryProtectedNamespace", 403);
            }
        }
        AssertTrustedParent(parent); return path;
    }

    static void AssertTrustedParent(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value;
        var trusted = new HashSet<string?>(StringComparer.Ordinal) { sid, "S-1-5-18", "S-1-5-32-544" };
        if (sid is null || descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
            throw Failure("BackupCheckpointRecoveryParentCustodyRejected", 403);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw Failure("BackupCheckpointRecoveryParentCustodyRejected", 403);
    }

    static void RequirePrivateStage(string path, string sid)
    {
        _ = LocalPath(path); var security = new DirectoryInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value != sid || !security.AreAccessRulesProtected)
            throw Failure("BackupCheckpointRecoveryStageCustodyRejected", 403);
        var full = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Value != sid || rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) !=
                    (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) throw Failure("BackupCheckpointRecoveryStageCustodyRejected", 403);
            full |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }
        if (!full) throw Failure("BackupCheckpointRecoveryStageCustodyRejected", 403);
    }
    static bool IsLocalFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        NotSupportedException or System.Security.SecurityException or SqliteException;
    static FoundationStoreException Failure(string code, int status = 409) => new(code, status);
}
