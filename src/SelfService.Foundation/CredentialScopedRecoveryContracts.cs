using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

// Metadata commitments only. Constructing or parsing this type does not assert AES or scope-MAC authentication.
public sealed class CredentialScopedRecoveryInventory
{
    public const int MaximumProfiles = 256, MaximumReportBytes = 1048576;
    static readonly Regex PublicSecretShape = new("(?i)(bearer\\s+|gh[pousr]_[A-Za-z0-9]|github_pat_|AccountKey=|SharedAccessSignature=|-----BEGIN .*PRIVATE KEY|eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]+\\.)", RegexOptions.CultureInvariant);
    internal CredentialScopedRecoveryInventory(CredentialVaultNamespaceIdentity identity, DateTimeOffset observedUtc,
        IEnumerable<CredentialRecoveryProfileDependency> profileDependencies, IEnumerable<CredentialAbsenceScopeDependency> absenceScopes,
        long authorityRevision, string authorityStateSha256)
    {
        if (identity is null || profileDependencies is null || absenceScopes is null) throw Invalid();
        Namespace = identity; ObservedUtc = observedUtc; AuthorityRevision = authorityRevision; AuthorityStateSha256 = authorityStateSha256;
        var profiles = profileDependencies.Take(MaximumProfiles + 1).ToArray(); var absences = absenceScopes.Take(MaximumProfiles + 1).ToArray();
        if (profiles.Any(p => p is null) || absences.Any(p => p is null) || profiles.Length + absences.Length > MaximumProfiles) throw Invalid();
        ProfileDependencies = Array.AsReadOnly(profiles.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray());
        AbsenceScopes = Array.AsReadOnly(absences.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray());
        CurrentRequiredKeyIds = Array.AsReadOnly(profiles.Select(p => p.SourceKeyId).Concat(absences.Select(p => p.SourceKeyId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        ValidateMetadata(); Sha256 = CredentialRecoverySets.Hash(CanonicalPayload()); ValidateIntegrity();
    }
    public string Schema => "platform-studio.credential-scoped-recovery-inventory.v1";
    [JsonIgnore] public CredentialVaultNamespaceIdentity Namespace { get; }
    public string CustomerId => Namespace.CustomerId;
    public string CellId => Namespace.CellId;
    public string VaultNamespaceId => Namespace.VaultNamespaceId;
    public string NamespaceCommitmentSha256 => Namespace.NamespaceCommitmentSha256;
    public string? AuthorityNamespaceId => Namespace.AuthorityNamespaceId;
    public string? AuthorityBindingSha256 => Namespace.AuthorityBindingSha256;
    public DateTimeOffset ObservedUtc { get; }
    public long AuthorityRevision { get; }
    public string AuthorityStateSha256 { get; }
    public IReadOnlyList<CredentialRecoveryProfileDependency> ProfileDependencies { get; }
    public IReadOnlyList<CredentialAbsenceScopeDependency> AbsenceScopes { get; }
    public IReadOnlyList<string> CurrentRequiredKeyIds { get; }
    public string IndependentBackupCoverage => "NotAssessed";
    public bool KeyRetirementAuthorized => false;
    public string Sha256 { get; }
    internal byte[] CanonicalPayload()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, CustomerId, CellId, VaultNamespaceId, NamespaceCommitmentSha256, AuthorityNamespaceId, AuthorityBindingSha256,
            observedUnixMilliseconds = ObservedUtc.ToUnixTimeMilliseconds(), AuthorityRevision, AuthorityStateSha256,
            ProfileDependencies, AbsenceScopes, CurrentRequiredKeyIds, IndependentBackupCoverage, KeyRetirementAuthorized
        }, CredentialRules.Json);
        if (bytes.Length > MaximumReportBytes) throw Invalid(); return bytes;
    }
    internal void ValidateIntegrity()
    {
        ValidateMetadata();
        if (!CredentialRecoverySets.DigestShape(Sha256) || CredentialRecoverySets.Hash(CanonicalPayload()) != Sha256 ||
            JsonSerializer.SerializeToUtf8Bytes(this, CredentialRules.Json).Length > MaximumReportBytes) throw Invalid();
    }
    void ValidateMetadata()
    {
        CredentialRecoverySets.RequireIdentity(Namespace);
        if (AuthorityNamespaceId is null || !CredentialRecoverySets.DigestShape(AuthorityStateSha256) ||
            AuthorityRevision is < 0 or > CredentialRecoveryInventory.MaximumCounter || !CredentialRecoverySets.Utc(ObservedUtc) ||
            ProfileDependencies is null || AbsenceScopes is null || CurrentRequiredKeyIds is null || ProfileDependencies.Count + AbsenceScopes.Count > MaximumProfiles) throw Invalid();
        foreach (var profile in ProfileDependencies) CredentialRecoveryInventory.ValidateProfile(profile);
        foreach (var scope in AbsenceScopes)
        {
            if (scope is null || !CredentialRecoverySets.DigestShape(scope.ProfileIdSha256) || !CredentialRecoverySets.DigestShape(scope.ScopeSha256) ||
                !CredentialRecoverySets.DigestShape(scope.ProofSha256) || !CustomerContextResolver.ValidId(scope.ProjectId) ||
                !CustomerContextResolver.ValidId(scope.EnvironmentId) || !CredentialRules.ProfileId(scope.SourceKeyId)) throw Invalid();
            if (PublicSecretShape.IsMatch(scope.SourceKeyId) || PublicSecretShape.IsMatch(scope.ProjectId) || PublicSecretShape.IsMatch(scope.EnvironmentId)) throw Invalid();
            try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(scope, CredentialRules.Json)); }
            catch (FoundationStoreException) { throw Invalid(); }
        }
        var positiveIds = ProfileDependencies.Select(p => p.ProfileIdSha256).ToArray(); var absentIds = AbsenceScopes.Select(p => p.ProfileIdSha256).ToArray();
        if (!positiveIds.SequenceEqual(positiveIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !absentIds.SequenceEqual(absentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            positiveIds.Intersect(absentIds, StringComparer.Ordinal).Any() ||
            !CurrentRequiredKeyIds.SequenceEqual(ProfileDependencies.Select(p => p.SourceKeyId).Concat(AbsenceScopes.Select(p => p.SourceKeyId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw Invalid();
    }
    static CredentialException Invalid() => new("credential-scoped-recovery-inventory-invalid");
}

public sealed record CredentialScopedRecoverySetSignature(int Version, string Kind, string SetId, string CustomerId, string CellId,
    CredentialVaultNamespaceIdentity SourceNamespace, long Generation, DateTimeOffset CapturedUtc, DateTimeOffset IssuedUtc,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresUtc, string InventorySha256, int ProfileCount, int AbsenceProofCount,
    string ContentSha256, long ContentLength, string KeyId, string PublicKeySha256, string Algorithm, string Signature);
public sealed record CredentialScopedRecoverySetAuthority(string CustomerId, string CellId, string SetId, long Generation,
    string ArtifactSha256, string SignerKeyId, string PublicKeySha256);

[JsonConverter(typeof(CredentialScopedRecoverySetJsonConverter))]
public sealed class CredentialScopedRecoverySet : IDisposable
{
    readonly object gate = new(); byte[]? artifact;
    internal CredentialScopedRecoverySet(byte[] ownedArtifact, CredentialScopedRecoverySetSignature signature)
    { artifact = ownedArtifact; Signature = signature; Sha256 = CredentialRecoverySets.Hash(ownedArtifact); SerializedLength = ownedArtifact.Length; }
    public CredentialScopedRecoverySetSignature Signature { get; }
    public string Sha256 { get; }
    public int SerializedLength { get; }
    public void WritePrivateArtifact(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination); var copy = CopyArtifact();
        try { destination.Write(copy); } finally { CryptographicOperations.ZeroMemory(copy); }
    }
    internal byte[] CopyArtifact() { lock (gate) { return artifact?.ToArray() ?? throw CredentialScopedRecoverySets.Failure("disposed"); } }
    public void Dispose() { lock (gate) { if (artifact is not null) CryptographicOperations.ZeroMemory(artifact); artifact = null; } }
}
public sealed class CredentialScopedRecoverySetJsonConverter : JsonConverter<CredentialScopedRecoverySet>
{
    public override CredentialScopedRecoverySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Use the explicit bounded private-artifact parser.");
    public override void Write(Utf8JsonWriter writer, CredentialScopedRecoverySet value, JsonSerializerOptions options) => throw new JsonException("Private recovery artifacts cannot be ordinary JSON.");
}
public sealed class CredentialScopedRecoverySetVerification
{
    internal CredentialScopedRecoverySetVerification(CredentialScopedRecoverySetSignature signature, CredentialScopedRecoveryInventory inventory, string sha256, int fileCount,
        IReadOnlyDictionary<string, string>? preservedProfileHashes = null)
    {
        Signature = signature; Inventory = inventory; Sha256 = sha256; FileCount = fileCount;
        PreservedProfileHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(preservedProfileHashes ?? new Dictionary<string, string>(), StringComparer.Ordinal));
    }
    internal IReadOnlyDictionary<string, string> PreservedProfileHashes { get; }
    public string Schema => "platform-studio.credential-scoped-recovery-set-verification.v1";
    public CredentialScopedRecoverySetSignature Signature { get; }
    public CredentialScopedRecoveryInventory Inventory { get; }
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
internal sealed class ValidatedCredentialScopedRecoverySet(CredentialScopedRecoverySetVerification verification,
    IEnumerable<RetainedCredentialRecoveryEnvelope> envelopes, IEnumerable<CredentialAbsenceScopeProof> proofs, IEnumerable<byte[]> ownedBuffers) : IDisposable
{
    readonly byte[][] owned = ownedBuffers.ToArray();
    internal CredentialScopedRecoverySetVerification Verification { get; } = verification;
    internal IReadOnlyList<RetainedCredentialRecoveryEnvelope> Envelopes { get; } = Array.AsReadOnly(envelopes.ToArray());
    internal IReadOnlyList<CredentialAbsenceScopeProof> AbsenceProofs { get; } = Array.AsReadOnly(proofs.ToArray());
    public void Dispose() { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); }
}
