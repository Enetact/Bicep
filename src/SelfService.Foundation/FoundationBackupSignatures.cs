using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationBackupSignature(int Version, string Kind, string ManifestId, string CustomerId, string CellId,
    int CellSchemaVersion, long Generation, string SnapshotSha256, long SnapshotLength, DateTimeOffset SnapshotCreatedUtc,
    DateTimeOffset IssuedUtc, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresUtc, string KeyId, string PublicKeySha256,
    string Algorithm, string Signature);
public sealed record FoundationSignedBackup(FoundationBackup Backup, FoundationBackupSignature Manifest);

/// <summary>Trusted latest checkpoint, kept independently from incoming backup packets. This is host authority, never packet-supplied authority.</summary>
public sealed record FoundationBackupRestoreAuthority(string CustomerId, string CellId, string ManifestId, long Generation,
    string ManifestSha256, string KeyId, string PublicKeySha256);

public sealed class ValidatedFoundationBackup
{
    internal ValidatedFoundationBackup(FoundationBackupSignature manifest, string digest)
    {
        CustomerId = manifest.CustomerId; CellId = manifest.CellId; ManifestId = manifest.ManifestId; Generation = manifest.Generation;
        CellSchemaVersion = manifest.CellSchemaVersion; SnapshotSha256 = manifest.SnapshotSha256; SnapshotLength = manifest.SnapshotLength;
        SnapshotCreatedUtc = manifest.SnapshotCreatedUtc; IssuedUtc = manifest.IssuedUtc; NotBeforeUtc = manifest.NotBeforeUtc;
        ExpiresUtc = manifest.ExpiresUtc; KeyId = manifest.KeyId; PublicKeySha256 = manifest.PublicKeySha256; ManifestSha256 = digest;
    }
    public string CustomerId { get; }
    public string CellId { get; }
    public string ManifestId { get; }
    public long Generation { get; }
    public int CellSchemaVersion { get; }
    public string SnapshotSha256 { get; }
    public long SnapshotLength { get; }
    public DateTimeOffset SnapshotCreatedUtc { get; }
    public DateTimeOffset IssuedUtc { get; }
    public DateTimeOffset NotBeforeUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string KeyId { get; }
    public string PublicKeySha256 { get; }
    public string ManifestSha256 { get; }
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
}

public static class FoundationBackupSignatures
{
    public const string Algorithm = "RSA-PSS-SHA256";
    public const int MaximumManifestBytes = 16384;
    public const long MaximumSnapshotBytes = 2147483648;
    static readonly Regex Digest = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    static readonly Regex KeyIdentifier = new("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    public static FoundationBackupSignature Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes) throw Invalid();
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in parsed.RootElement.EnumerateObject())
                if (!seen.Add(property.Name)) throw Invalid();
            var manifest = JsonSerializer.Deserialize<FoundationBackupSignature>(json, Json);
            RequireShape(manifest); return manifest!;
        }
        catch (JsonException) { throw Invalid(); }
    }

    /// <summary>Fixed field order and UTC Unix milliseconds. The physical snapshot path is deliberately excluded.</summary>
    public static byte[] CanonicalPayload(FoundationBackupSignature manifest)
    {
        RequireShape(manifest, allowUnsigned: true);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest.Version, manifest.Kind, manifest.ManifestId, manifest.CustomerId, manifest.CellId,
            manifest.CellSchemaVersion, manifest.Generation, manifest.SnapshotSha256, manifest.SnapshotLength,
            snapshotCreatedUtcMilliseconds = manifest.SnapshotCreatedUtc.ToUnixTimeMilliseconds(),
            issuedUtcMilliseconds = manifest.IssuedUtc.ToUnixTimeMilliseconds(),
            notBeforeUtcMilliseconds = manifest.NotBeforeUtc.ToUnixTimeMilliseconds(),
            expiresUtcMilliseconds = manifest.ExpiresUtc.ToUnixTimeMilliseconds(), manifest.KeyId, manifest.PublicKeySha256, manifest.Algorithm
        }, Json);
    }

    public static FoundationBackupRestoreAuthority Authority(FoundationBackupSignature manifest)
    {
        RequireShape(manifest);
        return new(manifest.CustomerId, manifest.CellId, manifest.ManifestId, manifest.Generation,
            ManifestDigest(manifest), manifest.KeyId, manifest.PublicKeySha256);
    }

    public static ValidatedFoundationBackup Verify(FoundationBackupSignature manifest, RSA pinnedPublicKey,
        FoundationBackupRestoreAuthority pinnedLatest, string expectedCustomerId, string expectedCellId, TimeProvider? timeProvider = null)
    {
        RequireShape(manifest);
        if (!CustomerContextResolver.ValidId(expectedCustomerId) || !CustomerContextResolver.ValidId(expectedCellId)) throw Invalid();
        if (manifest.CustomerId != expectedCustomerId || manifest.CellId != expectedCellId || pinnedLatest is null ||
            pinnedLatest.CustomerId != expectedCustomerId || pinnedLatest.CellId != expectedCellId)
            throw new FoundationStoreException("SignedBackupBindingMismatch", 403);
        var digest = ManifestDigest(manifest);
        if (pinnedLatest.ManifestId != manifest.ManifestId || pinnedLatest.Generation != manifest.Generation ||
            !SameDigest(digest, pinnedLatest.ManifestSha256) || pinnedLatest.KeyId != manifest.KeyId ||
            !SameDigest(manifest.PublicKeySha256, pinnedLatest.PublicKeySha256))
            throw new FoundationStoreException("SignedBackupLatestAuthorityMismatch", 403);
        try
        {
            using var publicKey = PublicCopy(pinnedPublicKey);
            if (!SameDigest(FoundationAuditAnchors.PublicKeyFingerprint(publicKey), manifest.PublicKeySha256))
                throw new FoundationStoreException("SignedBackupKeyMismatch", 403);
            var signature = Convert.FromBase64String(manifest.Signature);
            if (signature.Length != publicKey.KeySize / 8 || Convert.ToBase64String(signature) != manifest.Signature ||
                !publicKey.VerifyData(CanonicalPayload(manifest), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new FoundationStoreException("SignedBackupSignatureInvalid", 403);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or ObjectDisposedException or NotSupportedException)
        { throw new FoundationStoreException("SignedBackupSignatureInvalid", 403); }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (manifest.IssuedUtc > now || manifest.SnapshotCreatedUtc > now) throw new FoundationStoreException("SignedBackupIssuedInFuture", 403);
        if (manifest.NotBeforeUtc > now) throw new FoundationStoreException("SignedBackupNotYetValid", 403);
        if (manifest.ExpiresUtc <= now) throw new FoundationStoreException("SignedBackupExpired", 403);
        return new(manifest, digest);
    }

    internal static void RequireShape(FoundationBackupSignature? manifest, bool allowUnsigned = false)
    {
        if (manifest is null || manifest.Version != 1 || manifest.Kind != "cell-snapshot" ||
            !Guid.TryParseExact(manifest.ManifestId, "N", out var id) || id == Guid.Empty || id.ToString("N") != manifest.ManifestId ||
            !CustomerContextResolver.ValidId(manifest.CustomerId) || !CustomerContextResolver.ValidId(manifest.CellId) ||
            manifest.CellSchemaVersion is < 1 or > FoundationStore.CurrentSchemaVersion || manifest.Generation is < 1 or > 9007199254740991 ||
            manifest.SnapshotLength is < 1 or > MaximumSnapshotBytes || !IsDigest(manifest.SnapshotSha256) ||
            !UtcMilliseconds(manifest.SnapshotCreatedUtc) || !UtcMilliseconds(manifest.IssuedUtc) ||
            !UtcMilliseconds(manifest.NotBeforeUtc) || !UtcMilliseconds(manifest.ExpiresUtc) ||
            manifest.SnapshotCreatedUtc > manifest.IssuedUtc || manifest.IssuedUtc >= manifest.ExpiresUtc ||
            manifest.NotBeforeUtc >= manifest.ExpiresUtc || manifest.ExpiresUtc - manifest.IssuedUtc > TimeSpan.FromDays(366) ||
            manifest.KeyId is null || !KeyIdentifier.IsMatch(manifest.KeyId) || !IsDigest(manifest.PublicKeySha256) || manifest.Algorithm != Algorithm ||
            !(allowUnsigned && manifest.Signature == "" || manifest.Signature is not null && manifest.Signature.Length is >= 344 and <= 1368 &&
                Regex.IsMatch(manifest.Signature, "\\A[A-Za-z0-9+/]+={0,2}\\z", RegexOptions.CultureInvariant))) throw Invalid();
    }

    internal static RSA PublicCopy(RSA key)
    {
        if (key is null) throw new ArgumentException("A pinned public key is required.");
        // Reuse the audit anchor's public-key structural validation without exporting private material.
        _ = FoundationAuditAnchors.PublicKeyFingerprint(key);
        var result = RSA.Create();
        try { result.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _); return result; }
        catch { result.Dispose(); throw; }
    }
    internal static bool UtcMilliseconds(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.UtcTicks % TimeSpan.TicksPerMillisecond == 0;
    static bool IsDigest(string? value) => value is not null && Digest.IsMatch(value);
    static bool SameDigest(string first, string? second) => IsDigest(first) && IsDigest(second) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(first), Encoding.ASCII.GetBytes(second!));
    static string ManifestDigest(FoundationBackupSignature manifest) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, Json)));
    static FoundationStoreException Invalid() => new("InvalidSignedBackupManifest", 400);
}

public sealed partial class FoundationStore
{
    public FoundationSignedBackup CreateSignedBackup(string destinationPath, RSA signingKey, string keyId, long generation,
        DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc)
    {
        var issued = At(Now);
        FoundationBackupSignature shape;
        using var publicKey = ValidateBackupSigner(signingKey);
        shape = new(1, "cell-snapshot", NewId(), CustomerId, CellId, CurrentSchemaVersion, generation, new string('0', 64), 1,
            issued, issued, notBeforeUtc, expiresUtc, keyId, FoundationAuditAnchors.PublicKeyFingerprint(publicKey), FoundationBackupSignatures.Algorithm, "");
        FoundationBackupSignatures.RequireShape(shape, allowUnsigned: true);
        var backup = CreateBackup(destinationPath);
        // CreateBackup has already validated this standalone snapshot. Keep its read handle across signing.
        using var snapshot = new FileStream(backup.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (snapshot.Length != backup.Length || Convert.ToHexStringLower(SHA256.HashData(snapshot)) != backup.Sha256)
            throw new FoundationStoreException("BackupIntegrityFailed");
        int schemaVersion;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        { db.Open(); schemaVersion = checked((int)Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1")); }
        var unsigned = shape with { SnapshotSha256 = backup.Sha256, SnapshotLength = backup.Length, CellSchemaVersion = schemaVersion,
            SnapshotCreatedUtc = At(backup.CreatedAt.ToUnixTimeMilliseconds()), IssuedUtc = At(Now) };
        FoundationBackupSignatures.RequireShape(unsigned, allowUnsigned: true);
        FoundationBackupSignature signed;
        try
        {
            signed = unsigned with { Signature = Convert.ToBase64String(signingKey.SignData(FoundationBackupSignatures.CanonicalPayload(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
            if (!publicKey.VerifyData(FoundationBackupSignatures.CanonicalPayload(signed), Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new CryptographicException();
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException or NotSupportedException)
        { throw new FoundationStoreException("SignedBackupSigningKeyInvalid", 400); }
        MaintenanceAudit("BackupSignatureCreated", FoundationBackupSignatures.Authority(signed).ManifestSha256);
        return new(backup with { CreatedAt = signed.SnapshotCreatedUtc }, signed);
    }

    public static ValidatedFoundationBackup VerifySignedBackup(FoundationSignedBackup backup,
        FoundationBackupRestoreAuthority pinnedLatest, RSA pinnedPublicKey, string expectedCustomerId, string expectedCellId, TimeProvider? timeProvider = null)
    {
        if (backup?.Backup is null || backup.Manifest is null) throw new FoundationStoreException("InvalidSignedBackupManifest", 400);
        // Signature, latest authority and explicit customer/cell are verified before opening source or touching a target.
        var verified = FoundationBackupSignatures.Verify(backup.Manifest, pinnedPublicKey, pinnedLatest, expectedCustomerId, expectedCellId, timeProvider);
        var source = backup.Backup;
        if (source.CustomerId != verified.CustomerId || source.CellId != verified.CellId || source.Sha256 != verified.SnapshotSha256 ||
            source.Length != verified.SnapshotLength || source.CreatedAt != verified.SnapshotCreatedUtc)
            throw new FoundationStoreException("SignedBackupBindingMismatch", 403);
        var sourcePath = Path.GetFullPath(source.DatabasePath); RejectLinks(sourcePath);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != verified.SnapshotLength || Convert.ToHexStringLower(SHA256.HashData(input)) != verified.SnapshotSha256)
            throw new FoundationStoreException("BackupIntegrityFailed");
        RequireStandaloneSnapshot(sourcePath, input);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open(); ValidateSnapshot(db, verified.CellId, verified.CustomerId);
            if (Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1") != verified.CellSchemaVersion)
                throw new FoundationStoreException("SignedBackupSchemaMismatch");
        }
        return verified;
    }

    public static FoundationStore RestoreSignedBackup(FoundationSignedBackup backup, string destinationPath,
        FoundationBackupRestoreAuthority pinnedLatest, RSA pinnedPublicKey, string expectedCustomerId, string expectedCellId, TimeProvider? timeProvider = null)
    {
        _ = VerifySignedBackup(backup, pinnedLatest, pinnedPublicKey, expectedCustomerId, expectedCellId, timeProvider);
        // Restore reopens and independently rechecks the source bytes before creating a destination.
        return RestoreBackup(backup.Backup, destinationPath, timeProvider);
    }

    internal static RSA ValidateBackupSigner(RSA signingKey)
    {
        RSA? publicKey = null;
        try
        {
            publicKey = FoundationBackupSignatures.PublicCopy(signingKey);
            // Check that the handle can sign before CreateBackup can create any requested destination.
            var probe = "platform-studio.backup-signer-preflight.v1"u8.ToArray();
            var signature = signingKey.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            if (!publicKey.VerifyData(probe, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new CryptographicException();
            return publicKey;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or ObjectDisposedException or NotSupportedException)
        { publicKey?.Dispose(); throw new FoundationStoreException("SignedBackupSigningKeyInvalid", 400); }
    }
}
