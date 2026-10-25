using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

/// <summary>One protected local checkpoint chain for a fixed customer/cell and backup signer. No independent OS-owner rollback resistance.</summary>
public sealed class FoundationBackupCheckpointAuthority
{
    public const int MaximumCheckpoints = 1024, MaximumMetadataBytes = 16384;
    public const long MaximumHistoryBytes = 16L * 1024 * 1024;
    const string MarkerName = "authority.json", GuardName = ".backup-checkpoint.guard";
    readonly string root, customerId, cellId, signerKeyId, publicKeySha256, ownerSid;
    readonly TimeProvider clock;
    readonly FoundationBackupCheckpointAuthorityIdentity identity;
    readonly DateTimeOffset createdUtc;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 6, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    sealed record Header(int Schema, string Kind, string RootPath, string OwnerSid, string AuthorityId, string CustomerId,
        string CellId, string SignerKeyId, string PublicKeySha256, DateTimeOffset CreatedUtc, string Sha256);
    sealed record CheckpointFile(int Schema, string Kind, FoundationBackupCheckpoint Checkpoint);

    public FoundationBackupCheckpointAuthority(string authorityDirectory, string customerId, string cellId,
        string signerKeyId, string publicKeySha256, TimeProvider? timeProvider = null)
    {
        RequireWindows(); ValidateConfiguration(customerId, cellId, signerKeyId, publicKeySha256);
        root = LocalPath(authorityDirectory); this.customerId = customerId; this.cellId = cellId; this.signerKeyId = signerKeyId;
        this.publicKeySha256 = publicKeySha256; clock = timeProvider ?? TimeProvider.System; ownerSid = CurrentSid();
        using var guard = OpenGuard(exclusive: false); var header = ReadHeader();
        identity = Identity(header); createdUtc = header.CreatedUtc; _ = ReadState();
    }

    public static FoundationBackupCheckpointAuthority Provision(string authorityDirectory, string customerId, string cellId,
        string signerKeyId, string publicKeySha256, TimeProvider? timeProvider = null)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403);
        ValidateConfiguration(customerId, cellId, signerKeyId, publicKeySha256);
        var path = LocalPath(authorityDirectory); CheckParent(path); RejectNamespace(path);
        if (Path.Exists(path)) throw Failure("AlreadyExists");
        using var current = WindowsIdentity.GetCurrent(); var sid = current.User ?? throw Failure("OwnerUnavailable", 403);
        if (!CredentialNamespaceCustody.CreateDirectory(path, sid)) throw Failure(Path.Exists(path) ? "AlreadyExists" : "ReservationRejected");
        // Reservation is fresh and private at birth. Preserve all partial state on subsequent failure.
        NoLinks(path); CheckPrivate(path, directory: true);
        using (var guard = CredentialNamespaceCustody.CreateFile(Path.Combine(path, GuardName)))
        {
            guard.Flush(true); var now = At((timeProvider ?? TimeProvider.System).GetUtcNow());
            var header = new Header(1, "backup-checkpoint-authority", path, sid.Value, Guid.NewGuid().ToString("N"), customerId, cellId,
                signerKeyId, publicKeySha256, now, "");
            header = header with { Sha256 = HeaderDigest(header) }; WriteNew(path, MarkerName, header);
        }
        return new(path, customerId, cellId, signerKeyId, publicKeySha256, timeProvider);
    }

    public FoundationBackupCheckpointAuthorityIdentity DescribeIdentity()
    { using var guard = OpenGuard(exclusive: false); CheckAuthority(); _ = ReadState(); return identity; }
    public FoundationBackupCheckpoint? ReadCurrent()
    { using var guard = OpenGuard(exclusive: false); CheckAuthority(); return ReadState(); }
    public FoundationBackupCheckpointReadLease AcquireReadLease()
    {
        var guard = OpenGuard(exclusive: false);
        try
        {
            CheckAuthority(); var checkpoint = ReadState() ?? throw Failure("Missing", 403);
            return new(this, guard, identity, checkpoint);
        }
        catch { guard.Dispose(); throw; }
    }

    public FoundationBackupCheckpoint AcceptNext(FoundationSignedBackup candidate, RSA pinnedPublicKey,
        string expectedCandidateManifestSha256, long expectedSequence, string? expectedHeadSha256)
    {
        if (!DigestShape(expectedCandidateManifestSha256) || expectedSequence is < 0 or > MaximumCheckpoints ||
            (expectedSequence == 0 ? expectedHeadSha256 is not null : !DigestShape(expectedHeadSha256))) throw Failure("ExpectedStateInvalid", 400);
        using var guard = OpenGuard(exclusive: true); CheckAuthority(); var acceptedManifests = new HashSet<string>(StringComparer.Ordinal); var previous = ReadState(acceptedManifests);
        if ((previous?.Sequence ?? 0) != expectedSequence || previous?.Sha256 != expectedHeadSha256) throw Failure("GenerationChanged");
        if (expectedSequence >= MaximumCheckpoints) throw Failure("CapacityExceeded", 429);
        if (candidate?.Backup is null || candidate.Manifest is null) throw new FoundationStoreException("InvalidSignedBackupManifest", 400);
        var offered = FoundationBackupSignatures.Authority(candidate.Manifest);
        if (offered.ManifestSha256 != expectedCandidateManifestSha256) throw Failure("ReviewedManifestMismatch", 403);
        if (offered.CustomerId != customerId || offered.CellId != cellId || offered.KeyId != signerKeyId || offered.PublicKeySha256 != publicKeySha256 ||
            FoundationAuditAnchors.PublicKeyFingerprint(pinnedPublicKey) != publicKeySha256) throw Failure("SignerBindingMismatch", 403);
        if (previous is not null && offered.Generation <= previous.RestoreAuthority.Generation) throw Failure("BackupGenerationNotIncreasing");
        if (acceptedManifests.Contains(offered.ManifestId)) throw Failure("ManifestAlreadyAccepted");
        var signature = FoundationBackupSignatures.Verify(candidate.Manifest, pinnedPublicKey, offered, customerId, cellId, clock);
        if (candidate.Backup.CustomerId != customerId || candidate.Backup.CellId != cellId || candidate.Backup.Sha256 != signature.SnapshotSha256 ||
            candidate.Backup.Length != signature.SnapshotLength || candidate.Backup.CreatedAt != signature.SnapshotCreatedUtc)
            throw new FoundationStoreException("SignedBackupBindingMismatch", 403);
        var sourcePath = LocalPath(candidate.Backup.DatabasePath);
        if (sourcePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Failure("SourceNamespaceDenied", 403);
        using var source = OpenSource(sourcePath);
        // `offered` is trusted here only after configured signer + independent exact review + CAS. It is never incoming latest custody.
        _ = FoundationStore.VerifySignedBackup(candidate, offered, pinnedPublicKey, customerId, cellId, clock);
        CheckAuthority(); var latest = ReadState();
        if (latest?.Sequence != previous?.Sequence || latest?.Sha256 != previous?.Sha256) throw Failure("GenerationChanged");
        var accepted = At(clock.GetUtcNow());
        if (accepted < createdUtc || previous is not null && accepted < previous.AcceptedUtc || accepted < candidate.Manifest.IssuedUtc ||
            accepted < candidate.Manifest.NotBeforeUtc || accepted >= candidate.Manifest.ExpiresUtc) throw Failure("ClockInvalid", 403);
        var checkpoint = new FoundationBackupCheckpoint(identity.AuthorityId, identity.CommitmentSha256, expectedSequence + 1,
            previous?.Sha256 ?? "", accepted, offered, "");
        checkpoint = checkpoint with { Sha256 = CheckpointDigest(checkpoint) };
        var appendAttempted = false;
        try
        {
            appendAttempted = true; WriteNew(root, RecordName(checkpoint.Sequence), new CheckpointFile(1, "backup-checkpoint", checkpoint));
            if (ReadState() != checkpoint) throw Failure("Invalid"); return checkpoint;
        }
        catch (Exception) when (appendAttempted) { throw Failure("AppendUncertain", 503); }
    }

    internal void AssertLease(FoundationBackupCheckpointAuthorityIdentity expected, FoundationBackupCheckpoint checkpoint)
    {
        CheckAuthority(); var current = ReadState();
        if (expected != identity || current is null || current.Sequence != checkpoint.Sequence || current.Sha256 != checkpoint.Sha256) throw Failure("LeaseStale", 403);
    }

    FileStream OpenGuard(bool exclusive)
    {
        try
        {
            RequireWindows(); NoLinks(root); CheckPrivate(root, directory: true); var path = Path.Combine(root, GuardName); CheckPrivate(path, directory: false);
            var guard = new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
            if (guard.Length == 0) return guard; guard.Dispose(); throw Failure("Invalid");
        }
        catch (FileNotFoundException) { throw Failure("AuthorityMissing", 403); }
        catch (DirectoryNotFoundException) { throw Failure("AuthorityMissing", 403); }
        catch (IOException) { throw Failure("Busy"); }
        catch (UnauthorizedAccessException) { throw Failure("AccessDenied", 403); }
    }
    static FileStream OpenSource(string path)
    {
        try { NoLinks(path); return new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Failure("SourceUnavailable", 403); }
    }
    void CheckAuthority()
    { if (Identity(ReadHeader()) != identity) throw Failure("AuthorityChanged", 403); }
    Header ReadHeader()
    {
        var header = Read<Header>(Path.Combine(root, MarkerName));
        if (header.Schema != 1 || header.Kind != "backup-checkpoint-authority" || header.RootPath != root || header.OwnerSid != ownerSid ||
            !IdShape(header.AuthorityId) || header.CustomerId != customerId || header.CellId != cellId || header.SignerKeyId != signerKeyId ||
            header.PublicKeySha256 != publicKeySha256 || !UtcMilliseconds(header.CreatedUtc) || header.CreatedUtc > clock.GetUtcNow() || header.Sha256 != HeaderDigest(header)) throw Failure("Invalid");
        return header;
    }
    FoundationBackupCheckpoint? ReadState(ISet<string>? acceptedManifests = null)
    {
        CheckPrivate(root, directory: true); var records = new List<string>(); var entries = 0; long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (++entries > MaximumCheckpoints + 2) throw Failure("CapacityExceeded", 429);
            NoLinks(path); CheckPrivate(path, directory: false); var name = Path.GetFileName(path);
            if (name is MarkerName or GuardName) continue;
            if (!Regex.IsMatch(name, "\\Acheckpoint-[0-9]{4}\\.json\\z", RegexOptions.CultureInvariant) || records.Count >= MaximumCheckpoints) throw Failure("Invalid");
            bytes += new FileInfo(path).Length; if (bytes > MaximumHistoryBytes) throw Failure("CapacityExceeded", 429); records.Add(path);
        }
        FoundationBackupCheckpoint? previous = null; var manifests = acceptedManifests ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in records.Order(StringComparer.Ordinal))
        {
            var file = Read<CheckpointFile>(path); var checkpoint = file.Checkpoint; var pin = checkpoint?.RestoreAuthority;
            if (checkpoint is null || pin is null || file.Schema != 1 || file.Kind != "backup-checkpoint" || checkpoint.AuthorityId != identity.AuthorityId ||
                checkpoint.AuthorityCommitmentSha256 != identity.CommitmentSha256 || checkpoint.Sequence != (previous?.Sequence ?? 0) + 1 ||
                checkpoint.Sequence > MaximumCheckpoints || Path.GetFileName(path) != RecordName(checkpoint.Sequence) || checkpoint.PreviousSha256 != (previous?.Sha256 ?? "") ||
                !UtcMilliseconds(checkpoint.AcceptedUtc) || checkpoint.AcceptedUtc < createdUtc || checkpoint.AcceptedUtc > clock.GetUtcNow() || previous is not null && checkpoint.AcceptedUtc < previous.AcceptedUtc ||
                pin.CustomerId != customerId || pin.CellId != cellId || pin.KeyId != signerKeyId || pin.PublicKeySha256 != publicKeySha256 ||
                !IdShape(pin.ManifestId) || !manifests.Add(pin.ManifestId) || !DigestShape(pin.ManifestSha256) || pin.Generation is < 1 or > 9007199254740991 ||
                previous is not null && pin.Generation <= previous.RestoreAuthority.Generation || checkpoint.Sha256 != CheckpointDigest(checkpoint)) throw Failure("Invalid");
            previous = checkpoint;
        }
        return previous;
    }

    static T Read<T>(string path)
    {
        try
        {
            CheckPrivate(path, directory: false); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            CheckPrivate(path, directory: false); if (input.Length is < 2 or > MaximumMetadataBytes) throw Failure("Invalid");
            using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 6 }); Unique(doc.RootElement);
            return doc.RootElement.Deserialize<T>(Json) ?? throw Failure("Invalid");
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw Failure("Invalid"); }
    }
    static void WriteNew<T>(string directory, string name, T value)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); if (bytes.Length > MaximumMetadataBytes) throw Failure("CapacityExceeded", 429);
        CheckPrivate(directory, directory: true); var path = Path.Combine(directory, name); NoLinks(path);
        using (var output = CredentialNamespaceCustody.CreateFile(path)) { output.Write(bytes); output.Flush(true); }
        CheckPrivate(path, directory: false);
    }
    static string RecordName(long sequence) => $"checkpoint-{sequence:D4}.json";
    static FoundationBackupCheckpointAuthorityIdentity Identity(Header header) => new(header.CustomerId, header.CellId, header.AuthorityId, header.SignerKeyId, header.PublicKeySha256, header.Sha256);
    static string HeaderDigest(Header header) => Digest(JsonSerializer.SerializeToUtf8Bytes(new
    { header.Schema, header.Kind, header.RootPath, header.OwnerSid, header.AuthorityId, header.CustomerId, header.CellId, header.SignerKeyId, header.PublicKeySha256, createdUnixMilliseconds = header.CreatedUtc.ToUnixTimeMilliseconds() }, Json));
    static string CheckpointDigest(FoundationBackupCheckpoint checkpoint) => Digest(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = 1, kind = "backup-checkpoint", checkpoint.AuthorityId, checkpoint.AuthorityCommitmentSha256, checkpoint.Sequence, checkpoint.PreviousSha256,
        acceptedUnixMilliseconds = checkpoint.AcceptedUtc.ToUnixTimeMilliseconds(), checkpoint.RestoreAuthority }, Json));
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool DigestShape(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool IdShape(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');
    static bool UtcMilliseconds(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.UtcTicks % TimeSpan.TicksPerMillisecond == 0;
    static DateTimeOffset At(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    static void ValidateConfiguration(string customer, string cell, string key, string fingerprint)
    {
        if (!CustomerContextResolver.ValidId(customer) || !CustomerContextResolver.ValidId(cell) || key is null ||
            !Regex.IsMatch(key, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) || !DigestShape(fingerprint)) throw Failure("ConfigurationInvalid", 400);
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { customerId = customer, cellId = cell, signerKeyId = key, publicKeySha256 = fingerprint }, Json)); }
        catch (FoundationStoreException) { throw Failure("ConfigurationInvalid", 400); }
    }
    static void RequireWindows() { if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403); }
    static string LocalPath(string path)
    {
        RequireWindows();
        try { var local = CredentialNamespaceCustody.LocalPath(path); NoLinks(local); return local; }
        catch (CredentialException) { throw Failure("PathInvalid", 400); }
    }
    static void NoLinks(string path)
    {
        for (string? entry = Path.GetFullPath(path); entry is not null; entry = Path.GetDirectoryName(entry))
            try { if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw Failure("LinkedPathDenied", 400); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
    static string CurrentSid()
    { if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403); using var current = WindowsIdentity.GetCurrent(); return current.User?.Value ?? throw Failure("OwnerUnavailable", 403); }
    static void CheckPrivate(string path, bool directory)
    {
        RequireWindows(); NoLinks(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path) || Directory.Exists(path)) throw Failure("AuthorityMissing", 403);
        if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403);
        var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0); var sid = CurrentSid();
        if (raw.DiscretionaryAcl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || !security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value != sid) throw Failure("PermissionsDenied", 403);
        var allowed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference.Value != sid) throw Failure("PermissionsDenied", 403);
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) allowed = true;
        }
        if (!allowed) throw Failure("PermissionsDenied", 403);
    }
    static void CheckParent(string path)
    {
        RequireWindows(); var parent = Path.GetDirectoryName(path); if (parent is null || !Directory.Exists(parent)) throw Failure("ParentMissing", 403);
        if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403);
        var security = new DirectoryInfo(parent).GetAccessControl(); var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { CurrentSid(), "S-1-5-18", "S-1-5-32-544" };
        if (raw.DiscretionaryAcl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value)) throw Failure("ParentCustodyDenied", 403);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value)) throw Failure("ParentCustodyDenied", 403);
    }
    static readonly string[] Sentinels = ["authority.json", ".backup-checkpoint.guard", ".activation.guard", ".installation-owner.json", ".installation.guard", ".package-owner.json",
        ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", "cell-binding.json", ".foundation-owner-initialization.json",
        ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json"];
    static bool ProtectedName(string name) => Sentinels.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase);
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Failure("Invalid"); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    static void RejectNamespace(string path)
    {
        if (ProtectedName(Path.GetFileName(path))) throw Failure("ProtectedNamespaceDenied", 403); var depth = 0;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (++depth > 128) throw Failure("InspectionLimit", 403); var entries = 0;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                { if (++entries > 65536) throw Failure("InspectionLimit", 403); if (ProtectedName(Path.GetFileName(entry))) throw Failure("ProtectedNamespaceDenied", 403); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Failure("InspectionDenied", 403); }
        }
    }
    static FoundationStoreException Failure(string suffix, int status = 409) => new("BackupCheckpoint" + suffix, status);
}
