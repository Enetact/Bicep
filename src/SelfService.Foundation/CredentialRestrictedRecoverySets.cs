using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public static partial class CredentialRestrictedRecoverySets
{
    public const int MaximumArtifactBytes = CredentialScopedRecoverySets.MaximumArtifactBytes,
        MaximumContentBytes = CredentialScopedRecoverySets.MaximumContentBytes, MaximumProfiles = 256, MaximumFiles = 264;
    internal static readonly JsonSerializerOptions Json = CredentialScopedRecoverySets.Json;
    sealed record Artifact(string Schema, CredentialRestrictedRecoverySetSignature Signature, string Content);
    sealed record InventoryDocument(string Schema, string AccessProfile, CredentialScopedRecoverySets.InventoryDocument Source, string Sha256);
    sealed record ContentDocument(int Version, string Kind, string AccessProfile, InventoryDocument Inventory, CredentialScopedRecoverySets.FileDocument[] Files);
    const string Profile = "FoundationBoundDispatchOnly";
    const string ArtifactSchema = "platform-studio.credential-restricted-recovery-set-artifact.v1";

    public static CredentialRestrictedRecoverySet Parse(ReadOnlySpan<byte> privateArtifact)
    {
        if (privateArtifact.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        var bytes = privateArtifact.ToArray(); var owned = new List<byte[]>();
        try
        {
            var artifact = ReadArtifact(bytes); RequireSignature(artifact.Signature);
            var content = CredentialScopedRecoverySets.Decode(artifact.Content, MaximumContentBytes); owned.Add(content);
            RequireContent(artifact.Signature, content); _ = ValidateContent(artifact.Signature, content, owned);
            return new(bytes, artifact.Signature);
        }
        catch (Exception error) when (BadInput(error)) { CryptographicOperations.ZeroMemory(bytes); throw Failure("invalid"); }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        finally { foreach (var value in owned) CryptographicOperations.ZeroMemory(value); }
    }
    public static CredentialRestrictedRecoverySetAuthority Authority(CredentialRestrictedRecoverySet set)
    {
        ArgumentNullException.ThrowIfNull(set); var s = set.Signature;
        return new(s.CustomerId, s.CellId, s.SetId, s.Generation, set.Sha256, s.KeyId, s.PublicKeySha256, s.AccessProfile);
    }
    public static CredentialRestrictedRecoverySetVerification Verify(CredentialRestrictedRecoverySet set, CredentialRestrictedRecoverySetAuthority pinnedAuthority,
        RSA pinnedPublicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    { using var retained = ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, timeProvider); return retained.Verification; }

    internal static ValidatedCredentialRestrictedRecoverySet ValidateRetainedSet(CredentialRestrictedRecoverySet set,
        CredentialRestrictedRecoverySetAuthority authority, RSA publicKey, CredentialVaultNamespaceIdentity expectedNamespace, TimeProvider? timeProvider = null)
    {
        var owned = new List<byte[]>();
        try
        {
            CredentialRecoverySets.RequireIdentity(expectedNamespace);
            if (set is null || authority is null || expectedNamespace.AuthorityNamespaceId is null || authority.AccessProfile != Profile) throw Failure("authority-invalid");
            var bytes = set.CopyArtifact(); owned.Add(bytes); var artifact = ReadArtifact(bytes); var signature = artifact.Signature; RequireSignature(signature);
            if (signature.SourceNamespace != expectedNamespace || signature.CustomerId != expectedNamespace.CustomerId || signature.CellId != expectedNamespace.CellId) throw Failure("namespace-mismatch");
            if (authority.CustomerId != signature.CustomerId || authority.CellId != signature.CellId || authority.SetId != signature.SetId || authority.Generation != signature.Generation ||
                authority.SignerKeyId != signature.KeyId || authority.PublicKeySha256 != signature.PublicKeySha256 || authority.ArtifactSha256 != CredentialRecoverySets.Hash(bytes)) throw Failure("authority-mismatch");
            VerifySignature(signature, publicKey, timeProvider ?? TimeProvider.System);
            var content = CredentialScopedRecoverySets.Decode(artifact.Content, MaximumContentBytes); owned.Add(content); RequireContent(signature, content);
            var decoded = ValidateContent(signature, content, owned);
            var envelopes = decoded.Files.Where(f => CredentialScopedRecoverySets.IsProfileName(f.Key)).Select(f => new RetainedCredentialRecoveryEnvelope(f.Key[6..70], f.Value));
            var preserved = decoded.Files.Where(f => CredentialScopedRecoverySets.IsProfileName(f.Key)).ToDictionary(f => f.Key[6..70], f => CredentialVault.RecoveryProfileSha256(f.Value), StringComparer.Ordinal);
            return new(new(signature, decoded.Inventory, CredentialRecoverySets.Hash(bytes), decoded.Files.Count, preserved), envelopes, decoded.Proofs, owned);
        }
        catch (Exception error) when (BadInput(error)) { foreach (var value in owned) CryptographicOperations.ZeroMemory(value); throw Failure("invalid"); }
        catch { foreach (var value in owned) CryptographicOperations.ZeroMemory(value); throw; }
    }
    internal static CredentialRestrictedRecoverySet Create(CredentialRestrictedRecoveryInventory inventory, IReadOnlyDictionary<string, byte[]> files,
        RSA signer, RSA frozenPublicKey, string signerKeyId, long generation, DateTimeOffset issuedUtc, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc)
    {
        inventory.ValidateIntegrity();
        var source = JsonSerializer.Deserialize<CredentialScopedRecoverySets.InventoryDocument>(JsonSerializer.SerializeToUtf8Bytes(inventory.ScopedInventory, Json), Json) ?? throw Failure("invalid");
        var metadata = new InventoryDocument(inventory.Schema, Profile, source, inventory.Sha256);
        var content = JsonSerializer.SerializeToUtf8Bytes(new ContentDocument(3, "credential-restricted-recovery-set-content", Profile, metadata,
            files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new CredentialScopedRecoverySets.FileDocument(f.Key, f.Value.Length,
                CredentialRecoverySets.Hash(f.Value), Convert.ToBase64String(f.Value))).ToArray()), Json);
        try
        {
            if (content.Length > MaximumContentBytes) throw Failure("capacity-exceeded");
            var signature = new CredentialRestrictedRecoverySetSignature(3, "credential-restricted-recovery-set", Profile, Guid.NewGuid().ToString("N"), inventory.CustomerId,
                inventory.CellId, inventory.Namespace, generation, inventory.ObservedUtc, issuedUtc, notBeforeUtc, expiresUtc, inventory.Sha256,
                inventory.ProfileDependencies.Count, inventory.AbsenceScopes.Count, CredentialRecoverySets.Hash(content), content.Length, signerKeyId,
                FoundationAuditAnchors.PublicKeyFingerprint(frozenPublicKey), "RSA-PSS-SHA256", "");
            RequireSignature(signature, true); signature = signature with { Signature = Convert.ToBase64String(signer.SignData(CanonicalPayload(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
            RequireSignature(signature);
            if (!frozenPublicKey.VerifyData(CanonicalPayload(signature), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure("signing-unavailable");
            var artifact = JsonSerializer.SerializeToUtf8Bytes(new Artifact(ArtifactSchema, signature, Convert.ToBase64String(content)), Json);
            try { return Parse(artifact); } finally { CryptographicOperations.ZeroMemory(artifact); }
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException) { throw Failure("signing-unavailable"); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }
    static Artifact ReadArtifact(byte[] bytes)
    {
        if (bytes.Length is < 2 or > MaximumArtifactBytes) throw Failure("capacity-exceeded");
        using var document = CredentialRules.Document(bytes); var artifact = document.RootElement.Deserialize<Artifact>(Json) ?? throw Failure("invalid");
        if (artifact.Schema != ArtifactSchema) throw Failure("invalid"); return artifact;
    }
    static (CredentialRestrictedRecoveryInventory Inventory, SortedDictionary<string, ReadOnlyMemory<byte>> Files, IReadOnlyList<CredentialAbsenceScopeProof> Proofs)
        ValidateContent(CredentialRestrictedRecoverySetSignature signature, byte[] bytes, List<byte[]> owned)
    {
        using var document = CredentialRules.Document(bytes); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("inventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object ||
            !inventory.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("profileDependencies", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            !source.TryGetProperty("absenceScopes", out var absences) || absences.ValueKind != JsonValueKind.Array ||
            !source.TryGetProperty("currentRequiredKeyIds", out var keys) || keys.ValueKind != JsonValueKind.Array) throw Failure("contents-invalid");
        if (files.GetArrayLength() > MaximumFiles || profiles.GetArrayLength() + absences.GetArrayLength() > MaximumProfiles || keys.GetArrayLength() > MaximumProfiles) throw Failure("capacity-exceeded");
        var content = root.Deserialize<ContentDocument>(Json) ?? throw Failure("contents-invalid"); var raw = content.Inventory.Source;
        if (content.Version != 3 || content.Kind != "credential-restricted-recovery-set-content" || content.AccessProfile != Profile || content.Inventory.AccessProfile != Profile ||
            content.Inventory.Schema != "platform-studio.credential-restricted-recovery-inventory.v1" || raw.Schema != "platform-studio.credential-scoped-recovery-inventory.v1" ||
            raw.IndependentBackupCoverage != "NotAssessed" || raw.KeyRetirementAuthorized || raw.ProfileDependencies.Any(p => p is null) || raw.AbsenceScopes.Any(p => p is null)) throw Failure("contents-invalid");
        var identity = new CredentialVaultNamespaceIdentity(raw.CustomerId, raw.CellId, raw.VaultNamespaceId, raw.NamespaceCommitmentSha256, raw.AuthorityNamespaceId, raw.AuthorityBindingSha256);
        var scoped = new CredentialScopedRecoveryInventory(identity, raw.ObservedUtc, raw.ProfileDependencies.Select(p => new CredentialRecoveryProfileDependency(p.ProfileIdSha256,
            p.ScopeSha256, p.ProjectId, p.EnvironmentId, p.Generation, p.Enabled, p.ExpiresUnixMilliseconds, p.SourceKeyId, p.EnvelopeSha256, p.EnvelopeLength)), raw.AbsenceScopes, raw.AuthorityRevision, raw.AuthorityStateSha256);
        var result = new CredentialRestrictedRecoveryInventory(scoped);
        if (identity != signature.SourceNamespace || raw.ObservedUtc != signature.CapturedUtc || scoped.Sha256 != raw.Sha256 || result.Sha256 != signature.InventorySha256 || result.Sha256 != content.Inventory.Sha256 ||
            result.ProfileDependencies.Count != signature.ProfileCount || result.AbsenceScopes.Count != signature.AbsenceProofCount ||
            !scoped.CurrentRequiredKeyIds.SequenceEqual(raw.CurrentRequiredKeyIds, StringComparer.Ordinal) ||
            !scoped.ProfileDependencies.Select(p => p.ProfileIdSha256).SequenceEqual(raw.ProfileDependencies.Select(p => p.ProfileIdSha256), StringComparer.Ordinal) ||
            !scoped.AbsenceScopes.Select(p => p.ProfileIdSha256).SequenceEqual(raw.AbsenceScopes.Select(p => p.ProfileIdSha256), StringComparer.Ordinal)) throw Failure("contents-invalid");
        var decoded = CredentialScopedRecoverySets.DecodeFiles(scoped, content.Files, owned, CredentialRecoveryContentProfile.RestrictedScopedV3);
        return (result, decoded.Files, decoded.Proofs);
    }
    static void RequireContent(CredentialRestrictedRecoverySetSignature signature, byte[] bytes)
    { if (signature.ContentLength != bytes.Length || signature.ContentSha256 != CredentialRecoverySets.Hash(bytes)) throw Failure("content-mismatch"); }
    static bool BadInput(Exception e) => e is JsonException or FormatException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException;
    internal static CredentialException Failure(string suffix) => new("credential-restricted-recovery-set-" + suffix);
}
