using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed class CredentialRecoveryProfileDependency
{
    internal CredentialRecoveryProfileDependency(string profileIdSha256, string scopeSha256, string projectId, string environmentId,
        long generation, bool enabled, long expiresUnixMilliseconds, string sourceKeyId, string envelopeSha256, long envelopeLength)
    {
        ProfileIdSha256 = profileIdSha256; ScopeSha256 = scopeSha256; ProjectId = projectId; EnvironmentId = environmentId;
        Generation = generation; Enabled = enabled; ExpiresUnixMilliseconds = expiresUnixMilliseconds;
        SourceKeyId = sourceKeyId; EnvelopeSha256 = envelopeSha256; EnvelopeLength = envelopeLength;
    }
    public string ProfileIdSha256 { get; }
    public string ScopeSha256 { get; }
    public string ProjectId { get; }
    public string EnvironmentId { get; }
    public long Generation { get; }
    public bool Enabled { get; }
    public long ExpiresUnixMilliseconds { get; }
    public string SourceKeyId { get; }
    public string EnvelopeSha256 { get; }
    public long EnvelopeLength { get; }
}

// This metadata is an observation of one existing cell's custody, never a key-retirement or backup authority.
public sealed class CredentialRecoveryInventory
{
    public const int MaximumProfiles = 256, MaximumEntries = 264, MaximumReportBytes = 1048576;
    public const long MaximumCustodyBytes = 40L * 1024 * 1024, MaximumCounter = 9007199254740991;
    static readonly Regex PublicSecretShape = new("(?i)(bearer\\s+|gh[pousr]_[A-Za-z0-9]|github_pat_|AccountKey=|SharedAccessSignature=|-----BEGIN .*PRIVATE KEY|eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]+\\.)", RegexOptions.CultureInvariant);

    internal CredentialRecoveryInventory(CredentialVaultNamespaceIdentity identity, DateTimeOffset observedUtc,
        IEnumerable<CredentialRecoveryProfileDependency> profileDependencies, long authorityRevision, string? authorityStateSha256)
    {
        if (identity is null || profileDependencies is null) throw Invalid();
        CustomerId = identity.CustomerId; CellId = identity.CellId; VaultNamespaceId = identity.VaultNamespaceId;
        NamespaceCommitmentSha256 = identity.NamespaceCommitmentSha256; AuthorityNamespaceId = identity.AuthorityNamespaceId;
        AuthorityBindingSha256 = identity.AuthorityBindingSha256; AuthorityRevision = authorityRevision; AuthorityStateSha256 = authorityStateSha256;
        ObservedUtc = observedUtc;
        var profiles = profileDependencies.Take(MaximumProfiles + 1).ToArray();
        if (profiles.Length > MaximumProfiles || profiles.Any(p => p is null)) throw Invalid();
        ProfileDependencies = Array.AsReadOnly(profiles.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal).ToArray());
        CurrentRequiredKeyIds = Array.AsReadOnly(profiles.Select(p => p.SourceKeyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        ValidateMetadata(); Sha256 = Digest(CanonicalPayload()); ValidateIntegrity();
    }

    public string Schema => "platform-studio.credential-recovery-inventory.v1";
    public string CustomerId { get; }
    public string CellId { get; }
    public string VaultNamespaceId { get; }
    public string NamespaceCommitmentSha256 { get; }
    public string? AuthorityNamespaceId { get; }
    public string? AuthorityBindingSha256 { get; }
    public long AuthorityRevision { get; }
    public string? AuthorityStateSha256 { get; }
    public DateTimeOffset ObservedUtc { get; }
    public IReadOnlyList<CredentialRecoveryProfileDependency> ProfileDependencies { get; }
    public IReadOnlyList<string> CurrentRequiredKeyIds { get; }
    public string IndependentBackupCoverage => "NotAssessed";
    public bool KeyRetirementAuthorized => false;
    public string Sha256 { get; }

    internal void ValidateIntegrity()
    {
        ValidateMetadata();
        if (!HashShape(Sha256) || Digest(CanonicalPayload()) != Sha256 || JsonSerializer.SerializeToUtf8Bytes(this, CredentialRules.Json).Length > MaximumReportBytes)
            throw new CredentialException("credential-recovery-inventory-integrity-invalid");
    }

    internal byte[] CanonicalPayload()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, CustomerId, CellId, VaultNamespaceId, NamespaceCommitmentSha256, AuthorityNamespaceId, AuthorityBindingSha256,
            AuthorityRevision, AuthorityStateSha256, observedUnixMilliseconds = ObservedUtc.ToUnixTimeMilliseconds(),
            ProfileDependencies, CurrentRequiredKeyIds, IndependentBackupCoverage, KeyRetirementAuthorized
        }, CredentialRules.Json);
        if (bytes.Length > MaximumReportBytes) throw new CredentialException("credential-recovery-inventory-capacity-exceeded");
        return bytes;
    }

    void ValidateMetadata()
    {
        if (!Guid.TryParseExact(CustomerId, "D", out var customer) || customer == Guid.Empty || !Guid.TryParseExact(CellId, "D", out var cell) || cell == Guid.Empty ||
            !NamespaceShape(VaultNamespaceId) || !HashShape(NamespaceCommitmentSha256) || ObservedUtc.Offset != TimeSpan.Zero || ObservedUtc.UtcTicks % TimeSpan.TicksPerMillisecond != 0 ||
            ProfileDependencies is null || CurrentRequiredKeyIds is null || ProfileDependencies.Count > MaximumProfiles ||
            (AuthorityNamespaceId is null ? AuthorityBindingSha256 is not null || AuthorityStateSha256 is not null || AuthorityRevision != 0 :
                !NamespaceShape(AuthorityNamespaceId) || !HashShape(AuthorityBindingSha256) || !HashShape(AuthorityStateSha256) || AuthorityRevision is < 0 or > MaximumCounter)) throw Invalid();
        foreach (var profile in ProfileDependencies) ValidateProfile(profile);
        if (!ProfileDependencies.Select(p => p.ProfileIdSha256).SequenceEqual(ProfileDependencies.Select(p => p.ProfileIdSha256).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !CurrentRequiredKeyIds.SequenceEqual(ProfileDependencies.Select(p => p.SourceKeyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw Invalid();
    }

    internal static void ValidateProfile(CredentialRecoveryProfileDependency profile)
    {
        if (profile is null || !HashShape(profile.ProfileIdSha256) || !HashShape(profile.ScopeSha256) || !HashShape(profile.EnvelopeSha256) ||
            !CustomerContextResolver.ValidId(profile.ProjectId) || !CustomerContextResolver.ValidId(profile.EnvironmentId) || !CredentialRules.ProfileId(profile.SourceKeyId) ||
            profile.Generation is <= 0 or > MaximumCounter || profile.EnvelopeLength is < 1 or > 131072 ||
            profile.ExpiresUnixMilliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || profile.ExpiresUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) throw Invalid();
        if (PublicSecretShape.IsMatch(profile.SourceKeyId) || PublicSecretShape.IsMatch(profile.ProjectId) || PublicSecretShape.IsMatch(profile.EnvironmentId)) throw Invalid();
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(profile, CredentialRules.Json)); }
        catch (FoundationStoreException) { throw Invalid(); }
    }
    static bool NamespaceShape(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && value.Any(c => c != '0');
    static bool HashShape(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static CredentialException Invalid() => new("credential-recovery-inventory-metadata-invalid");
}
