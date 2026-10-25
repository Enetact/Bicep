using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfService.Foundation;

public sealed record CredentialRecoverySetSignature(int Version, string Kind, string SetId, string CustomerId, string CellId,
    CredentialVaultNamespaceIdentity SourceNamespace, long Generation, DateTimeOffset CapturedUtc, DateTimeOffset IssuedUtc,
    DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresUtc, string InventorySha256, int ProfileCount,
    string ContentSha256, long ContentLength, string KeyId, string PublicKeySha256, string Algorithm, string Signature);

// Trusted exact-set selection supplied separately from an incoming artifact. It is not durable latest-checkpoint custody.
public sealed record CredentialRecoverySetAuthority(string CustomerId, string CellId, string SetId, long Generation,
    string ArtifactSha256, string SignerKeyId, string PublicKeySha256);

[JsonConverter(typeof(CredentialRecoverySetJsonConverter))]
public sealed class CredentialRecoverySet : IDisposable
{
    readonly object gate = new();
    byte[]? artifact;
    internal CredentialRecoverySet(byte[] ownedArtifact, CredentialRecoverySetSignature signature)
    { artifact = ownedArtifact; Signature = signature; Sha256 = CredentialRecoverySets.Hash(ownedArtifact); SerializedLength = ownedArtifact.Length; }
    public CredentialRecoverySetSignature Signature { get; }
    public string Sha256 { get; }
    public int SerializedLength { get; }
    // Deliberate private-artifact handoff only. The caller owns destination custody; no automatic filesystem output.
    public void WritePrivateArtifact(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var copy = CopyArtifact();
        try { destination.Write(copy); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    internal byte[] CopyArtifact()
    { lock (gate) { return artifact?.ToArray() ?? throw new CredentialException("credential-recovery-set-disposed"); } }
    public void Dispose() { lock (gate) { if (artifact is not null) CryptographicOperations.ZeroMemory(artifact); artifact = null; } }
}

public sealed class CredentialRecoverySetJsonConverter : JsonConverter<CredentialRecoverySet>
{
    public override CredentialRecoverySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Private recovery artifacts require the explicit bounded parser.");
    public override void Write(Utf8JsonWriter writer, CredentialRecoverySet value, JsonSerializerOptions options) => throw new JsonException("Private recovery artifacts cannot be published as ordinary JSON.");
}

public sealed class CredentialRecoverySetVerification
{
    internal CredentialRecoverySetVerification(CredentialRecoverySetSignature signature, CredentialRecoveryInventory inventory, string sha256, int fileCount,
        IReadOnlyDictionary<string, string>? preservedProfiles = null, IEnumerable<string>? absentProfileIds = null)
    {
        Signature = signature; Inventory = inventory; Sha256 = sha256; FileCount = fileCount;
        PreservedProfileHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(preservedProfiles ?? new Dictionary<string, string>(), StringComparer.Ordinal));
        AbsentProfileIds = Array.AsReadOnly((absentProfileIds ?? []).ToArray());
    }
    internal IReadOnlyDictionary<string, string> PreservedProfileHashes { get; }
    internal IReadOnlyList<string> AbsentProfileIds { get; }
    public string Schema => "platform-studio.credential-recovery-set-verification.v1";
    public CredentialRecoverySetSignature Signature { get; }
    public CredentialRecoveryInventory Inventory { get; }
    public string Sha256 { get; }
    public int FileCount { get; }
    public bool SignatureVerified => true;
    public bool RetainedContentVerified => true;
    public bool EncryptedProfileAuthenticationPerformed => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool KeyBackupVerified => false;
    public bool KeyRetirementAuthorized => false;
    public bool RestoreAuthorized => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
    public bool ProductionRecoveryQualified => false;
}

internal sealed record RetainedCredentialRecoveryEnvelope(string ProfileIdSha256, ReadOnlyMemory<byte> EncryptedBytes);
internal sealed class ValidatedCredentialRecoverySet(CredentialRecoverySetVerification verification,
    IEnumerable<RetainedCredentialRecoveryEnvelope> envelopes, IEnumerable<byte[]> ownedBuffers, IEnumerable<string>? absentProfileIds = null) : IDisposable
{
    readonly byte[][] owned = ownedBuffers.ToArray();
    internal CredentialRecoverySetVerification Verification { get; } = verification;
    internal IReadOnlyList<RetainedCredentialRecoveryEnvelope> Envelopes { get; } = Array.AsReadOnly(envelopes.ToArray());
    internal IReadOnlyList<string> AbsentProfileIds { get; } = Array.AsReadOnly((absentProfileIds ?? []).ToArray());
    public void Dispose() { foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); }
}
