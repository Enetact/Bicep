using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

// Distinct scoped protocol; filenames deliberately reuse the operational namespace sentinels.
// Checksums and guards protect cooperating local custody, not a malicious owner or root rollback.
#pragma warning disable CA1416
public sealed class CredentialScopedRecoverySetCheckpointAuthority
{
    public const int MaximumCheckpoints = 1024, MaximumMetadataBytes = 16384;
    public const long MaximumHistoryBytes = 16L * 1024 * 1024;
    public const string MarkerName = ".credential-recovery-checkpoint.json", GuardName = ".credential-recovery-checkpoint.guard";
    readonly string root, signerKeyId, publicKeySha256, ownerSid;
    readonly CredentialVaultNamespaceIdentity sourceNamespace;
    readonly TimeProvider clock;
    readonly CredentialScopedRecoverySetCheckpointAuthorityIdentity identity;
    readonly DateTimeOffset createdUtc;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 10, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    sealed record Header(int Schema, string Kind, string RootPath, string OwnerSid, string AuthorityId,
        CredentialVaultNamespaceIdentity SourceNamespace, string SignerKeyId, string PublicKeySha256, DateTimeOffset CreatedUtc, string Sha256);
    sealed record CheckpointFile(int Schema, string Kind, CredentialScopedRecoverySetCheckpoint Checkpoint);

    public CredentialScopedRecoverySetCheckpointAuthority(string authorityDirectory, CredentialVaultNamespaceIdentity expectedNamespace,
        string signerKeyId, string publicKeySha256, TimeProvider? timeProvider = null)
    {
        RequireWindows(); ValidateConfiguration(expectedNamespace, signerKeyId, publicKeySha256);
        root = LocalPath(authorityDirectory); sourceNamespace = expectedNamespace; this.signerKeyId = signerKeyId;
        this.publicKeySha256 = publicKeySha256; clock = timeProvider ?? TimeProvider.System; ownerSid = CurrentSid();
        using var guard = OpenGuard(exclusive: false); var header = ReadHeader();
        identity = HeaderIdentity(header); createdUtc = header.CreatedUtc; _ = ReadState();
    }

    public static CredentialScopedRecoverySetCheckpointAuthority Provision(string authorityDirectory, CredentialVaultNamespaceIdentity expectedNamespace,
        string signerKeyId, string publicKeySha256, TimeProvider? timeProvider = null)
    {
        RequireWindows(); ValidateConfiguration(expectedNamespace, signerKeyId, publicKeySha256);
        var path = LocalPath(authorityDirectory); CheckParent(path); RejectNamespace(path);
        if (Path.Exists(path)) throw Failure("AlreadyExists");
        using var current = WindowsIdentity.GetCurrent(); var sid = current.User ?? throw Failure("OwnerUnavailable", 403);
        if (!CredentialNamespaceCustody.CreateDirectory(path, sid)) throw Failure(Path.Exists(path) ? "AlreadyExists" : "ReservationRejected");
        // Fresh owner-private reservation consumes the path. Never adopt, repair or clean partial output.
        NoLinks(path); CheckPrivate(path, directory: true);
        using (var guard = CredentialNamespaceCustody.CreateFile(Path.Combine(path, GuardName)))
        {
            guard.Flush(true); var now = At((timeProvider ?? TimeProvider.System).GetUtcNow());
            var header = new Header(1, "credential-scoped-recovery-checkpoint-authority", path, sid.Value, Guid.NewGuid().ToString("N"),
                expectedNamespace, signerKeyId, publicKeySha256, now, "");
            WriteNew(path, MarkerName, header with { Sha256 = HeaderDigest(header) });
        }
        return new(path, expectedNamespace, signerKeyId, publicKeySha256, timeProvider);
    }

    public CredentialScopedRecoverySetCheckpointAuthorityIdentity Identity => DescribeIdentity();
    public CredentialScopedRecoverySetCheckpointAuthorityIdentity DescribeIdentity()
    { using var guard = OpenGuard(exclusive: false); CheckAuthority(); _ = ReadState(); return identity; }
    public CredentialScopedRecoverySetCheckpoint? ReadCurrent()
    { using var guard = OpenGuard(exclusive: false); CheckAuthority(); return ReadState(); }
    public CredentialScopedRecoverySetCheckpointReadLease AcquireReadLease(long expectedSequence, string expectedHeadSha256)
    {
        if (expectedSequence is < 1 or > MaximumCheckpoints || !HashShape(expectedHeadSha256)) throw Failure("ExpectedStateInvalid", 400);
        var guard = OpenGuard(exclusive: false);
        try
        {
            CheckAuthority(); var checkpoint = ReadState() ?? throw Failure("Missing", 403);
            MatchHead(checkpoint, expectedSequence, expectedHeadSha256);
            return new(this, guard, identity, checkpoint);
        }
        catch { guard.Dispose(); throw; }
    }

    public async Task<CredentialScopedRecoverySetCheckpoint> AcceptNextAsync(CredentialScopedRecoverySet set, string reviewedArtifactSha256,
        RSA pinnedPublicKey, ICredentialKeyProvider sourceKeys, long expectedSequence, string? expectedHeadSha256,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!HashShape(reviewedArtifactSha256) || expectedSequence is < 0 or > MaximumCheckpoints ||
                (expectedSequence == 0 ? expectedHeadSha256 is not null : !HashShape(expectedHeadSha256))) throw Failure("ExpectedStateInvalid", 400);
            if (set is null || sourceKeys is null) throw Failure("CandidateInvalid", 400);
            using var guard = OpenGuard(exclusive: true); CheckAuthority(); var ids = new HashSet<string>(StringComparer.Ordinal); var previous = ReadState(ids);
            MatchHead(previous, expectedSequence, expectedHeadSha256);
            if (expectedSequence >= MaximumCheckpoints) throw Failure("CapacityExceeded", 429);
            if (set.Sha256 != reviewedArtifactSha256) throw Failure("ReviewedArtifactMismatch", 403);
            // Detach all retained bytes and freeze the supplied public key before any external source-key await.
            byte[]? privateArtifact = null;
            try
            {
                privateArtifact = set.CopyArtifact();
                using var retained = CredentialScopedRecoverySets.Parse(privateArtifact);
                if (retained.Sha256 != reviewedArtifactSha256) throw Failure("ReviewedArtifactMismatch", 403);
                var selected = CredentialScopedRecoverySets.Authority(retained); var signature = retained.Signature;
                if (signature.SourceNamespace != sourceNamespace || selected.CustomerId != sourceNamespace.CustomerId || selected.CellId != sourceNamespace.CellId)
                    throw Failure("NamespaceMismatch", 403);
                if (selected.SignerKeyId != signerKeyId || selected.PublicKeySha256 != publicKeySha256) throw Failure("SignerBindingMismatch", 403);
                if (previous is not null && selected.Generation <= previous.SourceGeneration) throw Failure("SetGenerationNotIncreasing");
                if (ids.Contains(selected.SetId)) throw Failure("SetAlreadyAccepted");
                using var publicKey = FoundationBackupSignatures.PublicCopy(pinnedPublicKey);
                if (FoundationAuditAnchors.PublicKeyFingerprint(publicKey) != publicKeySha256) throw Failure("SignerBindingMismatch", 403);
                // The typed rehearsal authenticates both retained AES envelopes and original absence-scope HMACs.
                var rehearsal = await new CredentialScopedRecoveryRehearsal(sourceNamespace, sourceKeys, clock).RehearseAsync(retained, selected, publicKey, token);
                token.ThrowIfCancellationRequested(); CheckAuthority(); var current = ReadState();
                MatchHead(current, expectedSequence, expectedHeadSha256);
                if (rehearsal.SignedSetSha256 != reviewedArtifactSha256 || rehearsal.SignedSetVerification.Signature != signature ||
                    rehearsal.SignedSetVerification.Signature.SourceNamespace != sourceNamespace || !HashShape(rehearsal.Sha256)) throw Failure("CandidateInvalid", 403);
                var accepted = At(clock.GetUtcNow()); CredentialScopedRecoverySets.ValidateLifetime(signature, accepted);
                if (accepted < createdUtc || accepted < rehearsal.CompletedUtc || previous is not null && accepted < previous.AcceptedUtc)
                    throw Failure("ClockInvalid", 403);
                var checkpoint = new CredentialScopedRecoverySetCheckpoint(identity.AuthorityId, identity.CommitmentSha256, expectedSequence + 1,
                    previous?.Sha256 ?? "", accepted, sourceNamespace, selected, signature.InventorySha256, rehearsal.Sha256,
                    signature.CapturedUtc, signature.IssuedUtc, signature.NotBeforeUtc, signature.ExpiresUtc, "");
                checkpoint = checkpoint with { Sha256 = CheckpointDigest(checkpoint) };
                token.ThrowIfCancellationRequested();
                var appendAttempted = false;
                try
                {
                    appendAttempted = true; WriteNew(root, RecordName(checkpoint.Sequence), new CheckpointFile(1, "credential-scoped-recovery-checkpoint", checkpoint));
                    CheckAuthority(); if (ReadState() != checkpoint) throw Failure("Invalid"); token.ThrowIfCancellationRequested(); return checkpoint;
                }
                catch (Exception) when (appendAttempted) { throw Failure("AppendUncertain", 503); }
            }
            finally { if (privateArtifact is not null) CryptographicOperations.ZeroMemory(privateArtifact); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential recovery checkpoint acceptance canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Failure("Timeout", 503); }
        catch (OperationCanceledException) { throw Failure("Unavailable", 503); }
        catch (CredentialException) { throw Failure("CandidateInvalid", 403); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or InvalidOperationException or NotSupportedException)
        { throw Failure("Unavailable", 503); }
    }

    internal void AssertLease(CredentialScopedRecoverySetCheckpointAuthorityIdentity expected, CredentialScopedRecoverySetCheckpoint checkpoint)
    {
        CheckAuthority(); var current = ReadState();
        if (expected != identity || current is null || current.Sequence != checkpoint.Sequence || current.Sha256 != checkpoint.Sha256)
            throw Failure("LeaseStale", 403);
    }
    static void MatchHead(CredentialScopedRecoverySetCheckpoint? head, long sequence, string? sha256)
    { if ((head?.Sequence ?? 0) != sequence || head?.Sha256 != sha256) throw Failure("GenerationChanged"); }
    FileStream OpenGuard(bool exclusive)
    {
        FileStream? guard = null;
        try
        {
            RequireWindows(); CheckParent(root); RejectNamespace(root); NoLinks(root); CheckPrivate(root, directory: true); var path = Path.Combine(root, GuardName); CheckPrivate(path, directory: false);
            guard = new(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
            CheckPrivate(root, directory: true); CheckPrivate(path, directory: false);
            if (guard.Length != 0) throw Failure("Invalid"); var retained = guard; guard = null; return retained;
        }
        catch (FileNotFoundException) { throw Failure("AuthorityMissing", 403); }
        catch (DirectoryNotFoundException) { throw Failure("AuthorityMissing", 403); }
        catch (IOException) { throw Failure("Busy"); }
        catch (UnauthorizedAccessException) { throw Failure("AccessDenied", 403); }
        finally { guard?.Dispose(); }
    }
    void CheckAuthority()
    {
        CheckParent(root); RejectNamespace(root);
        if (HeaderIdentity(ReadHeader()) != identity) throw Failure("AuthorityChanged", 403);
    }
    Header ReadHeader()
    {
        var header = Read<Header>(Path.Combine(root, MarkerName));
        if (header.Schema != 1 || header.Kind != "credential-scoped-recovery-checkpoint-authority" || header.RootPath != root || header.OwnerSid != ownerSid ||
            !IdShape(header.AuthorityId) || header.SourceNamespace != sourceNamespace || header.SignerKeyId != signerKeyId || header.PublicKeySha256 != publicKeySha256 ||
            !Utc(header.CreatedUtc) || header.CreatedUtc > clock.GetUtcNow() || header.Sha256 != HeaderDigest(header)) throw Failure("Invalid");
        return header;
    }
    CredentialScopedRecoverySetCheckpoint? ReadState(ISet<string>? acceptedIds = null)
    {
        try { return ReadStateCore(acceptedIds); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw Failure("Invalid"); }
    }
    CredentialScopedRecoverySetCheckpoint? ReadStateCore(ISet<string>? acceptedIds)
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
        CredentialScopedRecoverySetCheckpoint? previous = null; var ids = acceptedIds ?? new HashSet<string>(StringComparer.Ordinal); var now = clock.GetUtcNow();
        foreach (var path in records.Order(StringComparer.Ordinal))
        {
            var file = Read<CheckpointFile>(path); var checkpoint = file.Checkpoint; var pin = checkpoint?.SetAuthority;
            if (checkpoint is null || pin is null || file.Schema != 1 || file.Kind != "credential-scoped-recovery-checkpoint" || checkpoint.SourceNamespace != sourceNamespace ||
                checkpoint.AuthorityId != identity.AuthorityId || checkpoint.AuthorityCommitmentSha256 != identity.CommitmentSha256 ||
                checkpoint.Sequence != (previous?.Sequence ?? 0) + 1 || checkpoint.Sequence > MaximumCheckpoints || Path.GetFileName(path) != RecordName(checkpoint.Sequence) ||
                checkpoint.PreviousSha256 != (previous?.Sha256 ?? "") || !Utc(checkpoint.AcceptedUtc) || checkpoint.AcceptedUtc < createdUtc || checkpoint.AcceptedUtc > now ||
                previous is not null && checkpoint.AcceptedUtc < previous.AcceptedUtc || pin.CustomerId != sourceNamespace.CustomerId || pin.CellId != sourceNamespace.CellId ||
                pin.SignerKeyId != signerKeyId || pin.PublicKeySha256 != publicKeySha256 || !IdShape(pin.SetId) || !ids.Add(pin.SetId) || !HashShape(pin.ArtifactSha256) ||
                pin.Generation is < 1 or > CredentialRecoveryInventory.MaximumCounter || previous is not null && pin.Generation <= previous.SourceGeneration ||
                !HashShape(checkpoint.InventorySha256) || !HashShape(checkpoint.RehearsalSha256) || !Utc(checkpoint.CapturedUtc) || !Utc(checkpoint.IssuedUtc) ||
                !Utc(checkpoint.NotBeforeUtc) || !Utc(checkpoint.ExpiresUtc) || checkpoint.CapturedUtc > checkpoint.IssuedUtc ||
                checkpoint.IssuedUtc >= checkpoint.ExpiresUtc || checkpoint.NotBeforeUtc >= checkpoint.ExpiresUtc || checkpoint.ExpiresUtc - checkpoint.IssuedUtc > TimeSpan.FromDays(366) ||
                checkpoint.AcceptedUtc < checkpoint.IssuedUtc || checkpoint.AcceptedUtc < checkpoint.NotBeforeUtc || checkpoint.AcceptedUtc >= checkpoint.ExpiresUtc ||
                checkpoint.Sha256 != CheckpointDigest(checkpoint)) throw Failure("Invalid");
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
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || input.Length != bytes.Length) throw Failure("Invalid");
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 10 }); Unique(doc.RootElement);
            var value = doc.RootElement.Deserialize<T>(Json) ?? throw Failure("Invalid");
            if (value is CheckpointFile file)
            {
                var metadata = doc.RootElement.GetProperty("checkpoint");
                if (!metadata.TryGetProperty("sourceGeneration", out var generation) || !generation.TryGetInt64(out var parsedGeneration) || parsedGeneration != file.Checkpoint.SourceGeneration ||
                    !metadata.TryGetProperty("independentBackupCoverage", out var coverage) || coverage.ValueKind != JsonValueKind.String || coverage.GetString() != "NotAssessed" ||
                    !FixedFalse(metadata, "keyBackupVerified") || !FixedFalse(metadata, "promotionAuthorized") ||
                    !FixedFalse(metadata, "activationPerformed") || !FixedFalse(metadata, "externalExecutionAuthorized")) throw Failure("Invalid");
            }
            return value;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw Failure("Invalid"); }
    }
    static bool FixedFalse(JsonElement value, string property) => value.TryGetProperty(property, out var flag) && flag.ValueKind == JsonValueKind.False;
    static void WriteNew<T>(string directory, string name, T value)
    {
        RequireWindows(); var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumMetadataBytes) throw Failure("CapacityExceeded", 429);
        CheckPrivate(directory, directory: true); var path = Path.Combine(directory, name); NoLinks(path);
        using (var output = CredentialNamespaceCustody.CreateFile(path)) { output.Write(bytes); output.Flush(true); }
        CheckPrivate(path, directory: false);
    }
    static string RecordName(long sequence) => $"checkpoint-{sequence:D4}.json";
    static CredentialScopedRecoverySetCheckpointAuthorityIdentity HeaderIdentity(Header header) => new(header.SourceNamespace.CustomerId, header.SourceNamespace.CellId,
        header.SourceNamespace, header.AuthorityId, header.SignerKeyId, header.PublicKeySha256, header.Sha256);
    static string HeaderDigest(Header header) => Digest(JsonSerializer.SerializeToUtf8Bytes(new
    { header.Schema, header.Kind, header.RootPath, header.OwnerSid, header.AuthorityId, header.SourceNamespace, header.SignerKeyId,
        header.PublicKeySha256, createdUnixMilliseconds = header.CreatedUtc.ToUnixTimeMilliseconds() }, Json));
    static string CheckpointDigest(CredentialScopedRecoverySetCheckpoint checkpoint) => Digest(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = 1, kind = "credential-scoped-recovery-checkpoint", checkpoint.AuthorityId, checkpoint.AuthorityCommitmentSha256, checkpoint.Sequence,
        checkpoint.PreviousSha256, acceptedUnixMilliseconds = checkpoint.AcceptedUtc.ToUnixTimeMilliseconds(), checkpoint.SourceNamespace, checkpoint.SetAuthority,
        checkpoint.InventorySha256, checkpoint.RehearsalSha256, capturedUnixMilliseconds = checkpoint.CapturedUtc.ToUnixTimeMilliseconds(),
        issuedUnixMilliseconds = checkpoint.IssuedUtc.ToUnixTimeMilliseconds(), notBeforeUnixMilliseconds = checkpoint.NotBeforeUtc.ToUnixTimeMilliseconds(),
        expiresUnixMilliseconds = checkpoint.ExpiresUtc.ToUnixTimeMilliseconds() }, Json));
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool HashShape(string? value) => CredentialRecoverySets.DigestShape(value);
    static bool IdShape(string? value) => CredentialRecoverySets.NamespaceShape(value);
    static bool Utc(DateTimeOffset value) => CredentialRecoverySets.Utc(value);
    static DateTimeOffset At(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    static void ValidateConfiguration(CredentialVaultNamespaceIdentity source, string key, string fingerprint)
    {
        try
        {
            CredentialRecoverySets.RequireIdentity(source);
            if (source.AuthorityNamespaceId is null || source.AuthorityBindingSha256 is null) throw Failure("ConfigurationInvalid", 400);
            if (key is null || !Regex.IsMatch(key, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) || !HashShape(fingerprint)) throw Failure("ConfigurationInvalid", 400);
            _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { source, signerKeyId = key, publicKeySha256 = fingerprint }, Json));
        }
        catch (Exception error) when (error is CredentialException or FoundationStoreException) { throw Failure("ConfigurationInvalid", 400); }
    }
    static void RequireWindows() { if (!OperatingSystem.IsWindows()) throw Failure("WindowsRequired", 403); }
    static string CurrentSid() { RequireWindows(); using var current = WindowsIdentity.GetCurrent(); return current.User?.Value ?? throw Failure("OwnerUnavailable", 403); }
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
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
    }
    static void CheckPrivate(string path, bool directory)
    {
        RequireWindows(); NoLinks(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path) || Directory.Exists(path)) throw Failure("AuthorityMissing", 403);
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
        var security = new DirectoryInfo(parent).GetAccessControl(); var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { CurrentSid(), "S-1-5-18", "S-1-5-32-544" };
        if (raw.DiscretionaryAcl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
            security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value)) throw Failure("ParentCustodyDenied", 403);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value)) throw Failure("ParentCustodyDenied", 403);
    }
    static readonly string[] Sentinels = [MarkerName, GuardName, "authority.json", ".backup-checkpoint.guard", ".activation.guard", ".installation-owner.json", ".installation.guard", ".package-owner.json",
        ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", "cell-binding.json", ".foundation-owner-initialization.json",
        ".credential-recovery-stage.guard", ".credential-recovery-stage.json"];
    static bool ProtectedName(string name) => Sentinels.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase);
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
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Failure("Invalid"); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    static FoundationStoreException Failure(string suffix, int status = 409) => new("CredentialScopedRecoveryCheckpoint" + suffix, status);
}
