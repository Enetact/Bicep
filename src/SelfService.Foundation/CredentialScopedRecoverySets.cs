using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public static partial class CredentialScopedRecoverySets
{
    public const int MaximumArtifactBytes = 64 * 1024 * 1024, MaximumContentBytes = 47 * 1024 * 1024, MaximumProfiles = 256, MaximumFiles = 264;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    sealed record Artifact(string Schema, CredentialScopedRecoverySetSignature Signature, string Content);
    internal sealed record FileDocument(string Name, long Length, string Sha256, string Bytes);
    internal sealed record ProfileDocument(string ProfileIdSha256, string ScopeSha256, string ProjectId, string EnvironmentId,
        long Generation, bool Enabled, long ExpiresUnixMilliseconds, string SourceKeyId, string EnvelopeSha256, long EnvelopeLength);
    internal sealed record InventoryDocument(string Schema, string CustomerId, string CellId, string VaultNamespaceId,
        string NamespaceCommitmentSha256, string AuthorityNamespaceId, string AuthorityBindingSha256, DateTimeOffset ObservedUtc,
        long AuthorityRevision, string AuthorityStateSha256, ProfileDocument[] ProfileDependencies, CredentialAbsenceScopeDependency[] AbsenceScopes,
        string[] CurrentRequiredKeyIds, string IndependentBackupCoverage, bool KeyRetirementAuthorized, string Sha256);
    sealed record ContentDocument(int Version, string Kind, InventoryDocument Inventory, FileDocument[] Files);

    public static CredentialScopedRecoverySet Parse(ReadOnlySpan<byte> privateArtifact)
    {
        if (privateArtifact.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        var copy = privateArtifact.ToArray(); var owned = new List<byte[]>();
        try
        {
            var artifact = ReadArtifact(copy); RequireSignature(artifact.Signature);
            var content = Decode(artifact.Content, MaximumContentBytes); owned.Add(content); RequireContentDigest(artifact.Signature, content);
            _ = ValidateContent(artifact.Signature, content, owned); return new(copy, artifact.Signature);
        }
        catch (CredentialException) { CryptographicOperations.ZeroMemory(copy); throw; }
        catch (Exception error) when (BadInput(error)) { CryptographicOperations.ZeroMemory(copy); throw Failure("invalid"); }
        finally { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); }
    }
    public static CredentialScopedRecoverySetAuthority Authority(CredentialScopedRecoverySet set)
    {
        if (set is null) throw Failure("authority-invalid"); var s = set.Signature;
        return new(s.CustomerId, s.CellId, s.SetId, s.Generation, set.Sha256, s.KeyId, s.PublicKeySha256);
    }
    public static CredentialScopedRecoverySetVerification Verify(CredentialScopedRecoverySet set, CredentialScopedRecoverySetAuthority pinnedAuthority,
        RSA pinnedPublicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    { using var retained = ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, timeProvider); return retained.Verification; }

    internal static ValidatedCredentialScopedRecoverySet ValidateRetainedSet(CredentialScopedRecoverySet set,
        CredentialScopedRecoverySetAuthority pinnedAuthority, RSA pinnedPublicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    {
        var owned = new List<byte[]>();
        try
        {
            CredentialRecoverySets.RequireIdentity(expectedNamespace);
            if (expectedNamespace.AuthorityNamespaceId is null || set is null || pinnedAuthority is null || !Hash(pinnedAuthority.ArtifactSha256)) throw Failure("authority-invalid");
            var bytes = set.CopyArtifact(); owned.Add(bytes); var artifact = ReadArtifact(bytes); var signature = artifact.Signature; RequireSignature(signature);
            if (signature.SourceNamespace != expectedNamespace || signature.CustomerId != expectedNamespace.CustomerId || signature.CellId != expectedNamespace.CellId) throw Failure("namespace-mismatch");
            if (pinnedAuthority.CustomerId != signature.CustomerId || pinnedAuthority.CellId != signature.CellId || pinnedAuthority.SetId != signature.SetId ||
                pinnedAuthority.Generation != signature.Generation || pinnedAuthority.SignerKeyId != signature.KeyId || pinnedAuthority.PublicKeySha256 != signature.PublicKeySha256 ||
                pinnedAuthority.ArtifactSha256 != CredentialRecoverySets.Hash(bytes)) throw Failure("authority-mismatch");
            VerifySignature(signature, pinnedPublicKey, timeProvider ?? TimeProvider.System);
            var content = Decode(artifact.Content, MaximumContentBytes); owned.Add(content); RequireContentDigest(signature, content);
            var (inventory, files, proofs) = ValidateContent(signature, content, owned);
            var envelopes = files.Where(f => IsProfileName(f.Key)).Select(f => new RetainedCredentialRecoveryEnvelope(f.Key[6..70], f.Value));
            var preserved = files.Where(f => IsProfileName(f.Key)).ToDictionary(f => f.Key[6..70], f => CredentialVault.RecoveryProfileSha256(f.Value), StringComparer.Ordinal);
            return new(new(signature, inventory, CredentialRecoverySets.Hash(bytes), files.Count, preserved), envelopes, proofs, owned);
        }
        catch (CredentialException) { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); throw; }
        catch (Exception error) when (BadInput(error)) { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); throw Failure("invalid"); }
    }
    internal static CredentialScopedRecoverySet Create(CredentialScopedRecoveryInventory inventory, IReadOnlyDictionary<string, byte[]> files,
        RSA signer, RSA frozenPublicKey, string signerKeyId, long generation, DateTimeOffset issuedUtc, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc)
    {
        inventory.ValidateIntegrity();
        var metadata = JsonSerializer.Deserialize<InventoryDocument>(JsonSerializer.SerializeToUtf8Bytes(inventory, Json), Json) ?? throw Failure("invalid");
        var content = JsonSerializer.SerializeToUtf8Bytes(new ContentDocument(2, "credential-scoped-recovery-set-content", metadata,
            files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FileDocument(f.Key, f.Value.Length, CredentialRecoverySets.Hash(f.Value), Convert.ToBase64String(f.Value))).ToArray()), Json);
        try
        {
            if (content.Length > MaximumContentBytes) throw Failure("capacity-exceeded");
            var signature = new CredentialScopedRecoverySetSignature(2, "credential-scoped-recovery-set", Guid.NewGuid().ToString("N"), inventory.CustomerId, inventory.CellId,
                inventory.Namespace, generation, inventory.ObservedUtc, issuedUtc, notBeforeUtc, expiresUtc, inventory.Sha256, inventory.ProfileDependencies.Count,
                inventory.AbsenceScopes.Count, CredentialRecoverySets.Hash(content), content.Length, signerKeyId, FoundationAuditAnchors.PublicKeyFingerprint(frozenPublicKey), "RSA-PSS-SHA256", "");
            RequireSignature(signature, allowUnsigned: true);
            signature = signature with { Signature = Convert.ToBase64String(signer.SignData(CanonicalPayload(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
            RequireSignature(signature);
            if (!frozenPublicKey.VerifyData(CanonicalPayload(signature), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure("signing-unavailable");
            var artifact = JsonSerializer.SerializeToUtf8Bytes(new Artifact("platform-studio.credential-scoped-recovery-set-artifact.v1", signature, Convert.ToBase64String(content)), Json);
            try { return Parse(artifact); } finally { CryptographicOperations.ZeroMemory(artifact); }
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException) { throw Failure("signing-unavailable"); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }
    static Artifact ReadArtifact(byte[] bytes)
    {
        if (bytes.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        using var document = CredentialRules.Document(bytes); var artifact = document.RootElement.Deserialize<Artifact>(Json) ?? throw Failure("invalid");
        if (artifact.Schema != "platform-studio.credential-scoped-recovery-set-artifact.v1") throw Failure("invalid"); return artifact;
    }
    static (CredentialScopedRecoveryInventory Inventory, SortedDictionary<string, ReadOnlyMemory<byte>> Files, IReadOnlyList<CredentialAbsenceScopeProof> Proofs)
        ValidateContent(CredentialScopedRecoverySetSignature signature, byte[] bytes, List<byte[]> owned)
    {
        using var document = CredentialRules.Document(bytes); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var fileArray) || fileArray.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("inventory", out var inventoryObject) || inventoryObject.ValueKind != JsonValueKind.Object ||
            !inventoryObject.TryGetProperty("profileDependencies", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            !inventoryObject.TryGetProperty("absenceScopes", out var absences) || absences.ValueKind != JsonValueKind.Array ||
            !inventoryObject.TryGetProperty("currentRequiredKeyIds", out var keys) || keys.ValueKind != JsonValueKind.Array) throw Failure("contents-invalid");
        if (fileArray.GetArrayLength() > MaximumFiles || profiles.GetArrayLength() > MaximumProfiles || absences.GetArrayLength() > MaximumProfiles ||
            profiles.GetArrayLength() + absences.GetArrayLength() > MaximumProfiles || keys.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var content = root.Deserialize<ContentDocument>(Json) ?? throw Failure("contents-invalid"); var raw = content.Inventory;
        if (content.Version != 2 || content.Kind != "credential-scoped-recovery-set-content" || raw is null || content.Files is null ||
            content.Files.Length is < 7 or > MaximumFiles || raw.Schema != "platform-studio.credential-scoped-recovery-inventory.v1" ||
            raw.ProfileDependencies is null || raw.AbsenceScopes is null || raw.CurrentRequiredKeyIds is null || raw.ProfileDependencies.Any(p => p is null) || raw.AbsenceScopes.Any(p => p is null)) throw Failure("contents-invalid");
        var identity = new CredentialVaultNamespaceIdentity(raw.CustomerId, raw.CellId, raw.VaultNamespaceId, raw.NamespaceCommitmentSha256, raw.AuthorityNamespaceId, raw.AuthorityBindingSha256);
        if (identity != signature.SourceNamespace || raw.ObservedUtc != signature.CapturedUtc || raw.IndependentBackupCoverage != "NotAssessed" || raw.KeyRetirementAuthorized) throw Failure("contents-invalid");
        var inventory = new CredentialScopedRecoveryInventory(identity, raw.ObservedUtc, raw.ProfileDependencies.Select(p => new CredentialRecoveryProfileDependency(
            p.ProfileIdSha256, p.ScopeSha256, p.ProjectId, p.EnvironmentId, p.Generation, p.Enabled, p.ExpiresUnixMilliseconds, p.SourceKeyId, p.EnvelopeSha256, p.EnvelopeLength)),
            raw.AbsenceScopes, raw.AuthorityRevision, raw.AuthorityStateSha256);
        if (inventory.Sha256 != raw.Sha256 || inventory.Sha256 != signature.InventorySha256 || inventory.ProfileDependencies.Count != signature.ProfileCount ||
            inventory.AbsenceScopes.Count != signature.AbsenceProofCount || !inventory.CurrentRequiredKeyIds.SequenceEqual(raw.CurrentRequiredKeyIds, StringComparer.Ordinal) ||
            !inventory.ProfileDependencies.Select(p => p.ProfileIdSha256).SequenceEqual(raw.ProfileDependencies.Select(p => p.ProfileIdSha256), StringComparer.Ordinal) ||
            !inventory.AbsenceScopes.Select(p => p.ProfileIdSha256).SequenceEqual(raw.AbsenceScopes.Select(p => p.ProfileIdSha256), StringComparer.Ordinal)) throw Failure("contents-invalid");
        var decoded = DecodeFiles(inventory, content.Files, owned, CredentialRecoveryContentProfile.GeneralScopedV2);
        return (inventory, decoded.Files, decoded.Proofs);
    }

    internal static (SortedDictionary<string, ReadOnlyMemory<byte>> Files, IReadOnlyList<CredentialAbsenceScopeProof> Proofs) DecodeFiles(
        CredentialScopedRecoveryInventory inventory, IReadOnlyList<FileDocument> documents, List<byte[]> owned, CredentialRecoveryContentProfile contentProfile)
    {
        inventory.ValidateIntegrity(); var identity = inventory.Namespace;
        if (documents.Count is < 7 or > MaximumFiles) throw Failure("contents-invalid");
        var kinds = contentProfile switch
        {
            CredentialRecoveryContentProfile.GeneralScopedV2 => (Vault: "credential-vault-scoped-fenced", Authority: "credential-rollback-scoped-authority"),
            CredentialRecoveryContentProfile.RestrictedScopedV3 => (Vault: "credential-vault-scoped-bound-dispatch", Authority: "credential-rollback-scoped-bound-authority"),
            _ => throw Failure("contents-invalid")
        };
        var files = new SortedDictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal); long total = 0; string? previous = null;
        foreach (var file in documents)
        {
            if (file is null || file.Name is null || previous is not null && StringComparer.Ordinal.Compare(previous, file.Name) >= 0) throw Failure("contents-invalid");
            var maximum = FileMaximum(file.Name);
            if (file.Length < 0 || file.Length > maximum || !Hash(file.Sha256)) throw Failure("contents-invalid");
            var value = Decode(file.Bytes, maximum); owned.Add(value); total += value.Length;
            if (value.Length != file.Length || CredentialRecoverySets.Hash(value) != file.Sha256 || total > CredentialRecoveryInventory.MaximumCustodyBytes || !files.TryAdd(file.Name, value)) throw Failure("contents-invalid");
            previous = file.Name;
        }
        var expected = new SortedSet<string>(StringComparer.Ordinal) { "vault/.credential-namespace.json", "vault/.vault.lock", "vault/rollback-binding.json",
            "authority/.credential-namespace.json", "authority/.rollback.lock", "authority/authority.json", "authority/state.json" };
        foreach (var profile in inventory.ProfileDependencies)
        {
            var name = "vault/" + profile.ProfileIdSha256 + ".credential.json"; expected.Add(name);
            if (!files.TryGetValue(name, out var retained) || retained.Length != profile.EnvelopeLength || CredentialRecoverySets.Hash(retained.Span) != profile.EnvelopeSha256 ||
                JsonSerializer.Serialize(CredentialVault.InspectScopedRecoveryEnvelopeMetadata(retained, profile.ProfileIdSha256, identity), Json) != JsonSerializer.Serialize(profile, Json)) throw Failure("contents-invalid");
        }
        if (!files.Keys.SequenceEqual(expected, StringComparer.Ordinal) || files["vault/.vault.lock"].Length != 0 || files["authority/.rollback.lock"].Length != 0) throw Failure("contents-invalid");
        var vaultMarker = CredentialNamespaceCustody.ParseRecoveryMarker(files["vault/.credential-namespace.json"], kinds.Vault,
            identity.CustomerId, identity.CellId, identity.VaultNamespaceId, identity.NamespaceCommitmentSha256);
        var authorityMarker = CredentialNamespaceCustody.ParseRecoveryMarker(files["authority/.credential-namespace.json"], kinds.Authority,
            identity.CustomerId, identity.CellId, identity.AuthorityNamespaceId!, null);
        return (files, contentProfile == CredentialRecoveryContentProfile.GeneralScopedV2
            ? CredentialRollbackAuthority.ValidateScopedRecoverySnapshot(vaultMarker, authorityMarker, files, inventory)
            : CredentialRollbackAuthority.ValidateRestrictedRecoverySnapshot(vaultMarker, authorityMarker, files, new CredentialRestrictedRecoveryInventory(inventory)));
    }
    static int FileMaximum(string name) => name switch
    {
        "vault/.vault.lock" or "authority/.rollback.lock" => 0,
        "vault/.credential-namespace.json" or "vault/rollback-binding.json" or "authority/.credential-namespace.json" or "authority/authority.json" => 16384,
        "authority/state.json" => 1048576,
        _ when IsProfileName(name) => 131072,
        _ => throw Failure("contents-invalid")
    };
    internal static bool IsProfileName(string name) => Regex.IsMatch(name, "\\Avault/[a-f0-9]{64}\\.credential\\.json\\z", RegexOptions.CultureInvariant);
    internal static byte[] Decode(string value, int maximum)
    {
        if (value is null || value.Length > ((long)maximum + 2) / 3 * 4) throw Failure("capacity-exceeded");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length > maximum || Convert.ToBase64String(bytes) != value) { CryptographicOperations.ZeroMemory(bytes); throw Failure("invalid"); }
        return bytes;
    }
    static void RequireContentDigest(CredentialScopedRecoverySetSignature signature, byte[] bytes)
    { if (signature.ContentLength != bytes.Length || signature.ContentSha256 != CredentialRecoverySets.Hash(bytes)) throw Failure("content-mismatch"); }
    static bool Hash(string value) => CredentialRecoverySets.DigestShape(value);
    static bool BadInput(Exception error) => error is JsonException or FormatException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException;
    internal static CredentialException Failure(string suffix) => new("credential-scoped-recovery-set-" + suffix);
}
