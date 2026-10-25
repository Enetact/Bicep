using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public static class CredentialRecoverySets
{
    public const int MaximumArtifactBytes = 64 * 1024 * 1024, MaximumContentBytes = 47 * 1024 * 1024;
    public const int MaximumProfiles = 256, MaximumFiles = 264;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    internal sealed record Artifact(string Schema, CredentialRecoverySetSignature Signature, string Content);
    internal sealed record FileDocument(string Name, long Length, string Sha256, string Bytes);
    internal sealed record ProfileDocument(string ProfileIdSha256, string ScopeSha256, string ProjectId, string EnvironmentId,
        long Generation, bool Enabled, long ExpiresUnixMilliseconds, string SourceKeyId, string EnvelopeSha256, long EnvelopeLength);
    internal sealed record InventoryDocument(string Schema, string CustomerId, string CellId, string VaultNamespaceId,
        string NamespaceCommitmentSha256, string? AuthorityNamespaceId, string? AuthorityBindingSha256, long AuthorityRevision,
        string? AuthorityStateSha256, DateTimeOffset ObservedUtc, ProfileDocument[] ProfileDependencies, string[] CurrentRequiredKeyIds,
        string IndependentBackupCoverage, bool KeyRetirementAuthorized, string Sha256);
    internal sealed record ContentDocument(int Version, string Kind, InventoryDocument Inventory, FileDocument[] Files);

    public static CredentialRecoverySet Parse(ReadOnlySpan<byte> encryptedArtifact)
    {
        if (encryptedArtifact.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        var copy = encryptedArtifact.ToArray(); var owned = new List<byte[]>();
        try
        {
            var artifact = ReadArtifact(copy); RequireSignature(artifact.Signature);
            var content = Decode(artifact.Content, MaximumContentBytes); owned.Add(content);
            RequireContentDigest(artifact.Signature, content);
            _ = ValidateContent(artifact.Signature, content, owned);
            return new(copy, artifact.Signature);
        }
        catch (CredentialException) { CryptographicOperations.ZeroMemory(copy); throw; }
        catch (Exception error) when (BadInput(error)) { CryptographicOperations.ZeroMemory(copy); throw Failure("invalid"); }
        finally { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); }
    }

    // Use only for a set captured/reviewed by trusted composition, never to select an incoming packet's authority.
    public static CredentialRecoverySetAuthority Authority(CredentialRecoverySet set)
    {
        if (set is null) throw Failure("authority-invalid");
        var signature = set.Signature;
        return new(signature.CustomerId, signature.CellId, signature.SetId, signature.Generation, set.Sha256, signature.KeyId, signature.PublicKeySha256);
    }

    public static CredentialRecoverySetVerification Verify(CredentialRecoverySet set, CredentialRecoverySetAuthority pinnedAuthority,
        RSA pinnedPublicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    { using var validated = ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, timeProvider); return validated.Verification; }

    internal static ValidatedCredentialRecoverySet ValidateRetainedSet(CredentialRecoverySet set, CredentialRecoverySetAuthority pinnedAuthority,
        RSA pinnedPublicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    {
        var owned = new List<byte[]>();
        try
        {
            RequireIdentity(expectedNamespace);
            if (set is null || pinnedAuthority is null || !DigestShape(pinnedAuthority.ArtifactSha256)) throw Failure("authority-invalid");
            // Exact externally selected artifact and namespace are checked before any public-key/provider dependency.
            var bytes = set.CopyArtifact(); owned.Add(bytes);
            var artifact = ReadArtifact(bytes); var signature = artifact.Signature; RequireSignature(signature);
            if (signature.SourceNamespace != expectedNamespace || signature.CustomerId != expectedNamespace.CustomerId || signature.CellId != expectedNamespace.CellId)
                throw Failure("namespace-mismatch");
            if (pinnedAuthority.CustomerId != signature.CustomerId || pinnedAuthority.CellId != signature.CellId ||
                pinnedAuthority.SetId != signature.SetId || pinnedAuthority.Generation != signature.Generation ||
                pinnedAuthority.SignerKeyId != signature.KeyId || pinnedAuthority.PublicKeySha256 != signature.PublicKeySha256 ||
                pinnedAuthority.ArtifactSha256 != Hash(bytes)) throw Failure("authority-mismatch");
            VerifySignature(signature, pinnedPublicKey, timeProvider ?? TimeProvider.System);
            var content = Decode(artifact.Content, MaximumContentBytes); owned.Add(content); RequireContentDigest(signature, content);
            var (inventory, files, absentProfileIds) = ValidateContent(signature, content, owned);
            var preserved = files.Where(f => IsProfileName(f.Key)).ToDictionary(f => f.Key[6..70],
                f => CredentialVault.RecoveryProfileSha256(f.Value), StringComparer.Ordinal);
            var envelopes = files.Where(f => IsProfileName(f.Key)).Select(f => new RetainedCredentialRecoveryEnvelope(f.Key[6..70], f.Value));
            return new(new(signature, inventory, Hash(bytes), files.Count, preserved, absentProfileIds), envelopes, owned, absentProfileIds);
        }
        catch (CredentialException) { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); throw; }
        catch (Exception error) when (BadInput(error))
        { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); throw Failure("invalid"); }
    }

    internal static byte[] CanonicalPayload(CredentialRecoverySetSignature signature)
    {
        RequireSignature(signature, allowUnsigned: true);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            signature.Version, signature.Kind, signature.SetId, signature.CustomerId, signature.CellId, signature.SourceNamespace,
            signature.Generation, capturedUnixMilliseconds = signature.CapturedUtc.ToUnixTimeMilliseconds(),
            issuedUnixMilliseconds = signature.IssuedUtc.ToUnixTimeMilliseconds(), notBeforeUnixMilliseconds = signature.NotBeforeUtc.ToUnixTimeMilliseconds(),
            expiresUnixMilliseconds = signature.ExpiresUtc.ToUnixTimeMilliseconds(), signature.InventorySha256, signature.ProfileCount,
            signature.ContentSha256, signature.ContentLength, signature.KeyId, signature.PublicKeySha256, signature.Algorithm
        }, Json);
    }

    internal static CredentialRecoverySet Create(CredentialRecoveryInventory inventory, IReadOnlyDictionary<string, byte[]> files,
        RSA signer, RSA frozenPublicKey, string signerKeyId, long generation, DateTimeOffset issuedUtc, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc)
    {
        var inventoryDocument = JsonSerializer.Deserialize<InventoryDocument>(JsonSerializer.SerializeToUtf8Bytes(inventory, Json), Json) ?? throw Failure("invalid");
        var content = JsonSerializer.SerializeToUtf8Bytes(new ContentDocument(1, "credential-recovery-set-content", inventoryDocument,
            files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FileDocument(f.Key, f.Value.Length, Hash(f.Value), Convert.ToBase64String(f.Value))).ToArray()), Json);
        try
        {
            if (content.Length > MaximumContentBytes) throw Failure("capacity-exceeded");
            var ns = new CredentialVaultNamespaceIdentity(inventory.CustomerId, inventory.CellId, inventory.VaultNamespaceId,
                inventory.NamespaceCommitmentSha256, inventory.AuthorityNamespaceId, inventory.AuthorityBindingSha256);
            var signature = new CredentialRecoverySetSignature(1, "credential-recovery-set", Guid.NewGuid().ToString("N"), inventory.CustomerId,
                inventory.CellId, ns, generation, inventory.ObservedUtc, issuedUtc, notBeforeUtc, expiresUtc, inventory.Sha256,
                inventory.ProfileDependencies.Count, Hash(content), content.Length, signerKeyId, FoundationAuditAnchors.PublicKeyFingerprint(frozenPublicKey), "RSA-PSS-SHA256", "");
            RequireSignature(signature, allowUnsigned: true);
            signature = signature with { Signature = Convert.ToBase64String(signer.SignData(CanonicalPayload(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
            RequireSignature(signature);
            if (!frozenPublicKey.VerifyData(CanonicalPayload(signature), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw Failure("signing-unavailable");
            var artifact = JsonSerializer.SerializeToUtf8Bytes(new Artifact("platform-studio.credential-recovery-set-artifact.v1", signature, Convert.ToBase64String(content)), Json);
            try { return Parse(artifact); } finally { CryptographicOperations.ZeroMemory(artifact); }
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException)
        { throw Failure("signing-unavailable"); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    static Artifact ReadArtifact(byte[] bytes)
    {
        if (bytes.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        using var document = CredentialRules.Document(bytes);
        var artifact = document.RootElement.Deserialize<Artifact>(Json) ?? throw Failure("invalid");
        if (artifact.Schema != "platform-studio.credential-recovery-set-artifact.v1") throw Failure("invalid"); return artifact;
    }
    static (CredentialRecoveryInventory Inventory, SortedDictionary<string, ReadOnlyMemory<byte>> Files, IReadOnlyList<string> AbsentProfileIds) ValidateContent(
        CredentialRecoverySetSignature signature, byte[] bytes, List<byte[]> owned)
    {
        using var document = CredentialRules.Document(bytes);
        // Bound collection hydration as well as decoded bytes: hostile arrays must not allocate unbounded DTO graphs.
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var fileArray) ||
            fileArray.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("inventory", out var inventoryObject) || inventoryObject.ValueKind != JsonValueKind.Object ||
            !inventoryObject.TryGetProperty("profileDependencies", out var profileArray) || profileArray.ValueKind != JsonValueKind.Array ||
            !inventoryObject.TryGetProperty("currentRequiredKeyIds", out var keyArray) || keyArray.ValueKind != JsonValueKind.Array) throw Failure("contents-invalid");
        if (fileArray.GetArrayLength() > MaximumFiles || profileArray.GetArrayLength() > MaximumProfiles || keyArray.GetArrayLength() > MaximumProfiles)
            throw Failure("capacity-exceeded");
        if (fileArray.GetArrayLength() < 2) throw Failure("contents-invalid");
        var content = document.RootElement.Deserialize<ContentDocument>(Json) ?? throw Failure("contents-invalid");
        if (content.Version != 1 || content.Kind != "credential-recovery-set-content" || content.Inventory is null || content.Files is null ||
            content.Files.Length is < 2 or > MaximumFiles) throw Failure("contents-invalid");
        var raw = content.Inventory;
        if (raw.Schema != "platform-studio.credential-recovery-inventory.v1" || raw.ProfileDependencies is null || raw.ProfileDependencies.Length > MaximumProfiles ||
            raw.ProfileDependencies.Any(p => p is null) || raw.CurrentRequiredKeyIds is null) throw Failure("contents-invalid");
        var identity = new CredentialVaultNamespaceIdentity(raw.CustomerId, raw.CellId, raw.VaultNamespaceId, raw.NamespaceCommitmentSha256, raw.AuthorityNamespaceId, raw.AuthorityBindingSha256);
        if (identity != signature.SourceNamespace || raw.ObservedUtc != signature.CapturedUtc || raw.IndependentBackupCoverage != "NotAssessed" || raw.KeyRetirementAuthorized)
            throw Failure("contents-invalid");
        var inventory = new CredentialRecoveryInventory(identity, raw.ObservedUtc, raw.ProfileDependencies.Select(p => new CredentialRecoveryProfileDependency(
            p.ProfileIdSha256, p.ScopeSha256, p.ProjectId, p.EnvironmentId, p.Generation, p.Enabled, p.ExpiresUnixMilliseconds, p.SourceKeyId, p.EnvelopeSha256, p.EnvelopeLength)),
            raw.AuthorityRevision, raw.AuthorityStateSha256);
        if (inventory.Sha256 != raw.Sha256 || inventory.Sha256 != signature.InventorySha256 || inventory.ProfileDependencies.Count != signature.ProfileCount ||
            !inventory.CurrentRequiredKeyIds.SequenceEqual(raw.CurrentRequiredKeyIds, StringComparer.Ordinal) ||
            !inventory.ProfileDependencies.Select(p => p.ProfileIdSha256).SequenceEqual(raw.ProfileDependencies.Select(p => p.ProfileIdSha256), StringComparer.Ordinal)) throw Failure("contents-invalid");
        var files = new SortedDictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal); long total = 0; string? previous = null;
        foreach (var file in content.Files)
        {
            if (file is null || file.Name is null || previous is not null && StringComparer.Ordinal.Compare(previous, file.Name) >= 0) throw Failure("contents-invalid");
            var maximum = FileMaximum(file.Name);
            if (file.Length < 0 || file.Length > maximum || !DigestShape(file.Sha256)) throw Failure("contents-invalid");
            var value = Decode(file.Bytes, maximum); owned.Add(value); total += value.Length;
            if (value.Length != file.Length || Hash(value) != file.Sha256 || total > CredentialRecoveryInventory.MaximumCustodyBytes || !files.TryAdd(file.Name, value)) throw Failure("contents-invalid");
            previous = file.Name;
        }
        var expected = new SortedSet<string>(StringComparer.Ordinal) { "vault/.credential-namespace.json", "vault/.vault.lock" };
        foreach (var profile in inventory.ProfileDependencies)
        {
            var name = "vault/" + profile.ProfileIdSha256 + ".credential.json"; expected.Add(name);
            if (!files.TryGetValue(name, out var retained) || retained.Length != profile.EnvelopeLength || Hash(retained.Span) != profile.EnvelopeSha256) throw Failure("contents-invalid");
        }
        var vaultMarker = CredentialNamespaceCustody.ParseRecoveryMarker(files.GetValueOrDefault("vault/.credential-namespace.json"),
            identity.AuthorityNamespaceId is null ? "credential-vault" : "credential-vault-fenced", identity.CustomerId, identity.CellId,
            identity.VaultNamespaceId, identity.NamespaceCommitmentSha256);
        IReadOnlyList<string> absentProfileIds = [];
        if (identity.AuthorityNamespaceId is not null)
        {
            foreach (var name in new[] { "vault/rollback-binding.json", "authority/.credential-namespace.json", "authority/.rollback.lock", "authority/authority.json", "authority/state.json" }) expected.Add(name);
            var authorityMarker = CredentialNamespaceCustody.ParseRecoveryMarker(files.GetValueOrDefault("authority/.credential-namespace.json"),
                "credential-rollback-authority", identity.CustomerId, identity.CellId, identity.AuthorityNamespaceId, null);
            absentProfileIds = CredentialRollbackAuthority.ValidateRecoverySnapshot(vaultMarker, authorityMarker, files, inventory);
        }
        if (!files.Keys.SequenceEqual(expected, StringComparer.Ordinal) || files["vault/.vault.lock"].Length != 0 ||
            identity.AuthorityNamespaceId is not null && files["authority/.rollback.lock"].Length != 0) throw Failure("contents-invalid");
        return (inventory, files, absentProfileIds);
    }
    static int FileMaximum(string name) => name switch
    {
        "vault/.vault.lock" or "authority/.rollback.lock" => 0,
        "vault/.credential-namespace.json" or "vault/rollback-binding.json" or "authority/.credential-namespace.json" or "authority/authority.json" => 16384,
        "authority/state.json" => 1048576,
        _ when IsProfileName(name) => 131072,
        _ => throw Failure("contents-invalid")
    };
    static bool IsProfileName(string name) => Regex.IsMatch(name, "\\Avault/[a-f0-9]{64}\\.credential\\.json\\z", RegexOptions.CultureInvariant);
    static byte[] Decode(string value, int maximum)
    {
        if (value is null || value.Length > ((long)maximum + 2) / 3 * 4) throw Failure("capacity-exceeded");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length > maximum || Convert.ToBase64String(bytes) != value) { CryptographicOperations.ZeroMemory(bytes); throw Failure("invalid"); }
        return bytes;
    }
    static void RequireContentDigest(CredentialRecoverySetSignature signature, byte[] bytes)
    { if (signature.ContentLength != bytes.Length || signature.ContentSha256 != Hash(bytes)) throw Failure("content-mismatch"); }
    static void VerifySignature(CredentialRecoverySetSignature signature, RSA suppliedKey, TimeProvider clock)
    {
        try
        {
            using var key = FoundationBackupSignatures.PublicCopy(suppliedKey);
            if (FoundationAuditAnchors.PublicKeyFingerprint(key) != signature.PublicKeySha256) throw Failure("signer-mismatch");
            var value = Convert.FromBase64String(signature.Signature);
            if (value.Length != key.KeySize / 8 || !key.VerifyData(CanonicalPayload(signature), value, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure("signature-invalid");
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException) { throw Failure("signature-invalid"); }
        ValidateLifetime(signature, clock);
    }
    internal static void ValidateLifetime(CredentialRecoverySetSignature signature, TimeProvider clock)
        => ValidateLifetime(signature, clock.GetUtcNow());
    internal static void ValidateLifetime(CredentialRecoverySetSignature signature, DateTimeOffset now)
    {
        if (signature.IssuedUtc > now || signature.CapturedUtc > now) throw Failure("issued-in-future");
        if (signature.NotBeforeUtc > now) throw Failure("not-yet-valid");
        if (signature.ExpiresUtc <= now) throw Failure("expired");
    }
    internal static void RequireSignature(CredentialRecoverySetSignature signature, bool allowUnsigned = false)
    {
        if (signature is null) throw Failure("signature-invalid"); RequireIdentity(signature.SourceNamespace);
        if (signature.Version != 1 || signature.Kind != "credential-recovery-set" || !NamespaceShape(signature.SetId) ||
            signature.CustomerId != signature.SourceNamespace.CustomerId || signature.CellId != signature.SourceNamespace.CellId ||
            signature.Generation is < 1 or > CredentialRecoveryInventory.MaximumCounter ||
            !Utc(signature.CapturedUtc) || !Utc(signature.IssuedUtc) || !Utc(signature.NotBeforeUtc) || !Utc(signature.ExpiresUtc) ||
            signature.CapturedUtc > signature.IssuedUtc || signature.IssuedUtc >= signature.ExpiresUtc || signature.NotBeforeUtc >= signature.ExpiresUtc ||
            signature.ExpiresUtc - signature.IssuedUtc > TimeSpan.FromDays(366) || !DigestShape(signature.InventorySha256) ||
            !DigestShape(signature.ContentSha256) || signature.ContentLength is < 2 or > MaximumContentBytes ||
            signature.ProfileCount is < 0 or > MaximumProfiles || signature.KeyId is null || !Regex.IsMatch(signature.KeyId, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) ||
            !DigestShape(signature.PublicKeySha256) || signature.Algorithm != "RSA-PSS-SHA256" ||
            !(allowUnsigned && signature.Signature == "" || signature.Signature is { Length: >= 344 and <= 1368 } && Regex.IsMatch(signature.Signature, "\\A[A-Za-z0-9+/]+={0,2}\\z", RegexOptions.CultureInvariant))) throw Failure("signature-invalid");
        if (!(allowUnsigned && signature.Signature == ""))
        {
            byte[]? decoded = null;
            try { decoded = Convert.FromBase64String(signature.Signature); if (Convert.ToBase64String(decoded) != signature.Signature) throw Failure("signature-invalid"); }
            catch (FormatException) { throw Failure("signature-invalid"); }
            finally { if (decoded is not null) CryptographicOperations.ZeroMemory(decoded); }
        }
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { signature.KeyId }, Json)); }
        catch (FoundationStoreException) { throw Failure("signature-invalid"); }
    }
    internal static void RequireIdentity(CredentialVaultNamespaceIdentity identity)
    {
        if (identity is null || !Guid.TryParseExact(identity.CustomerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(identity.CellId, "D", out var cell) || cell == Guid.Empty || !NamespaceShape(identity.VaultNamespaceId) ||
            !DigestShape(identity.NamespaceCommitmentSha256) || (identity.AuthorityNamespaceId is null ? identity.AuthorityBindingSha256 is not null :
                !NamespaceShape(identity.AuthorityNamespaceId) || !DigestShape(identity.AuthorityBindingSha256))) throw Failure("namespace-invalid");
    }
    internal static bool DigestShape(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool NamespaceShape(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.UtcTicks % TimeSpan.TicksPerMillisecond == 0;
    static bool BadInput(Exception error) => error is JsonException or FormatException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException;
    internal static CredentialException Failure(string suffix) => new("credential-recovery-set-" + suffix);
}
