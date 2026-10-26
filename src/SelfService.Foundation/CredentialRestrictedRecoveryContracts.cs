using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfService.Foundation;

// Metadata only. Its fixed access profile is not a grant to read or activate credentials.
public sealed class CredentialRestrictedRecoveryInventory
{
    public const int MaximumProfiles = 256, MaximumReportBytes = 1048576;
    internal CredentialRestrictedRecoveryInventory(CredentialVaultNamespaceIdentity identity, DateTimeOffset observedUtc,
        IEnumerable<CredentialRecoveryProfileDependency> profiles, IEnumerable<CredentialAbsenceScopeDependency> absences,
        long authorityRevision, string authorityStateSha256) : this(new CredentialScopedRecoveryInventory(identity, observedUtc,
            profiles, absences, authorityRevision, authorityStateSha256)) { }
    internal CredentialRestrictedRecoveryInventory(CredentialScopedRecoveryInventory source)
    {
        source.ValidateIntegrity(); ScopedInventory = source;
        Sha256 = CredentialRecoverySets.Hash(CanonicalPayload()); ValidateIntegrity();
    }
    internal CredentialScopedRecoveryInventory ScopedInventory { get; }
    public string Schema => "platform-studio.credential-restricted-recovery-inventory.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    [JsonIgnore] public CredentialVaultNamespaceIdentity Namespace => ScopedInventory.Namespace;
    public string CustomerId => ScopedInventory.CustomerId;
    public string CellId => ScopedInventory.CellId;
    public string VaultNamespaceId => ScopedInventory.VaultNamespaceId;
    public string NamespaceCommitmentSha256 => ScopedInventory.NamespaceCommitmentSha256;
    public string? AuthorityNamespaceId => ScopedInventory.AuthorityNamespaceId;
    public string? AuthorityBindingSha256 => ScopedInventory.AuthorityBindingSha256;
    public DateTimeOffset ObservedUtc => ScopedInventory.ObservedUtc;
    public long AuthorityRevision => ScopedInventory.AuthorityRevision;
    public string AuthorityStateSha256 => ScopedInventory.AuthorityStateSha256;
    public IReadOnlyList<CredentialRecoveryProfileDependency> ProfileDependencies => ScopedInventory.ProfileDependencies;
    public IReadOnlyList<CredentialAbsenceScopeDependency> AbsenceScopes => ScopedInventory.AbsenceScopes;
    public IReadOnlyList<string> CurrentRequiredKeyIds => ScopedInventory.CurrentRequiredKeyIds;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool KeyRetirementAuthorized => false;
    public string Sha256 { get; }
    internal byte[] CanonicalPayload() => JsonSerializer.SerializeToUtf8Bytes(new { Schema, AccessProfile, sourceSha256 = ScopedInventory.Sha256 }, CredentialRules.Json);
    internal void ValidateIntegrity()
    {
        ScopedInventory.ValidateIntegrity();
        if (CredentialRecoverySets.Hash(CanonicalPayload()) != Sha256 || JsonSerializer.SerializeToUtf8Bytes(this, CredentialRules.Json).Length > MaximumReportBytes)
            throw CredentialRestrictedRecoverySets.Failure("inventory-invalid");
    }
}

public sealed record CredentialRestrictedRecoverySetSignature(int Version, string Kind, string AccessProfile, string SetId, string CustomerId, string CellId,
    CredentialVaultNamespaceIdentity SourceNamespace, long Generation, DateTimeOffset CapturedUtc, DateTimeOffset IssuedUtc,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresUtc, string InventorySha256, int ProfileCount, int AbsenceProofCount,
    string ContentSha256, long ContentLength, string KeyId, string PublicKeySha256, string Algorithm, string Signature);
public sealed record CredentialRestrictedRecoverySetAuthority(string CustomerId, string CellId, string SetId, long Generation,
    string ArtifactSha256, string SignerKeyId, string PublicKeySha256, string AccessProfile);

[JsonConverter(typeof(CredentialRestrictedRecoverySetJsonConverter))]
public sealed class CredentialRestrictedRecoverySet : IDisposable
{
    readonly object gate = new(); byte[]? artifact;
    internal CredentialRestrictedRecoverySet(byte[] ownedArtifact, CredentialRestrictedRecoverySetSignature signature)
    { artifact = ownedArtifact; Signature = signature; Sha256 = CredentialRecoverySets.Hash(ownedArtifact); SerializedLength = ownedArtifact.Length; }
    public CredentialRestrictedRecoverySetSignature Signature { get; }
    public string Sha256 { get; }
    public int SerializedLength { get; }
    public void WritePrivateArtifact(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination); var bytes = CopyArtifact();
        try { destination.Write(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal byte[] CopyArtifact() { lock (gate) return artifact?.ToArray() ?? throw CredentialRestrictedRecoverySets.Failure("disposed"); }
    public void Dispose() { lock (gate) { if (artifact is not null) CryptographicOperations.ZeroMemory(artifact); artifact = null; } }
}
public sealed class CredentialRestrictedRecoverySetJsonConverter : JsonConverter<CredentialRestrictedRecoverySet>
{
    public override CredentialRestrictedRecoverySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Use the bounded private artifact parser.");
    public override void Write(Utf8JsonWriter writer, CredentialRestrictedRecoverySet value, JsonSerializerOptions options) => throw new JsonException("Private recovery artifacts cannot be ordinary JSON.");
}
public sealed class CredentialRestrictedRecoverySetVerification
{
    internal CredentialRestrictedRecoverySetVerification(CredentialRestrictedRecoverySetSignature signature, CredentialRestrictedRecoveryInventory inventory,
        string sha256, int fileCount, IReadOnlyDictionary<string, string> preserved)
    {
        Signature = signature; Inventory = inventory; Sha256 = sha256; FileCount = fileCount;
        PreservedProfileHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(preserved, StringComparer.Ordinal));
    }
    internal IReadOnlyDictionary<string, string> PreservedProfileHashes { get; }
    public string Schema => "platform-studio.credential-restricted-recovery-set-verification.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public CredentialRestrictedRecoverySetSignature Signature { get; }
    public CredentialRestrictedRecoveryInventory Inventory { get; }
    public string Sha256 { get; }
    public int FileCount { get; }
    public bool SignatureVerified => true;
    public bool RetainedContentVerified => true;
    public bool EncryptedProfileAuthenticationPerformed => false;
    public bool AbsenceProofAuthenticationPerformed => false;
    public bool RestoreAuthorized => false;
    public bool CustomerReviewAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool KeyBackupVerified => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
}
internal sealed class ValidatedCredentialRestrictedRecoverySet(CredentialRestrictedRecoverySetVerification verification,
    IEnumerable<RetainedCredentialRecoveryEnvelope> envelopes, IEnumerable<CredentialAbsenceScopeProof> proofs, IEnumerable<byte[]> ownedBuffers) : IDisposable
{
    readonly byte[][] owned = ownedBuffers.ToArray();
    internal CredentialRestrictedRecoverySetVerification Verification { get; } = verification;
    internal IReadOnlyList<RetainedCredentialRecoveryEnvelope> Envelopes { get; } = Array.AsReadOnly(envelopes.ToArray());
    internal IReadOnlyList<CredentialAbsenceScopeProof> AbsenceProofs { get; } = Array.AsReadOnly(proofs.ToArray());
    public void Dispose() { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); }
}

internal enum CredentialRecoveryContentProfile { GeneralScopedV2, RestrictedScopedV3 }
