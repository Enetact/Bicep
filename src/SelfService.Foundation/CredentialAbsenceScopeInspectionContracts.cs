using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed record CredentialAbsenceScopeDependency(string ProfileIdSha256, string ScopeSha256,
    string ProjectId, string EnvironmentId, string SourceKeyId, string ProofSha256);

/// <summary>Authenticated absence-scope dependencies only; never a full recovery inventory or scope authorization.</summary>
public sealed class CredentialAbsenceScopeInspection
{
    internal CredentialAbsenceScopeInspection(CredentialVaultNamespaceIdentity identity, DateTimeOffset observedUtc,
        long authorityRevision, string authorityStateSha256, int positiveEnvelopeCount,
        IEnumerable<CredentialAbsenceScopeDependency> scopes)
    {
        CredentialRecoverySets.RequireIdentity(identity);
        var captured = scopes.OrderBy(scope => scope.ProfileIdSha256, StringComparer.Ordinal).ToArray();
        if (identity.AuthorityNamespaceId is null || !CredentialRecoverySets.DigestShape(authorityStateSha256) ||
            authorityRevision is < 0 or > CredentialRecoveryInventory.MaximumCounter || positiveEnvelopeCount is < 0 or > CredentialRecoveryInventory.MaximumProfiles ||
            captured.Length > CredentialRecoveryInventory.MaximumProfiles ||
            captured.Length + positiveEnvelopeCount > CredentialRecoveryInventory.MaximumProfiles ||
            captured.Select(scope => scope.ProfileIdSha256).Distinct(StringComparer.Ordinal).Count() != captured.Length ||
            captured.Any(scope => !CredentialRecoverySets.DigestShape(scope.ProfileIdSha256) || !CredentialRecoverySets.DigestShape(scope.ScopeSha256) ||
                !CredentialRecoverySets.DigestShape(scope.ProofSha256) || !CustomerContextResolver.ValidId(scope.ProjectId) ||
                !CustomerContextResolver.ValidId(scope.EnvironmentId) || !CredentialRules.ProfileId(scope.SourceKeyId)))
            throw new CredentialException("credential-absence-scope-inspection-result-invalid");
        Namespace = identity; ObservedUtc = observedUtc; AuthorityRevision = authorityRevision; AuthorityStateSha256 = authorityStateSha256;
        PositiveEnvelopeCount = positiveEnvelopeCount; AbsenceScopes = Array.AsReadOnly(captured);
        AbsenceRequiredKeyIds = Array.AsReadOnly(captured.Select(scope => scope.SourceKeyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, Namespace, observedUnixMilliseconds = ObservedUtc.ToUnixTimeMilliseconds(), AuthorityRevision, AuthorityStateSha256,
            PositiveEnvelopeCount, AbsenceScopes, AbsenceRequiredKeyIds, AuthenticatedAbsenceCount, AbsenceScopeAuthenticationPerformed,
            PositiveEnvelopeAuthenticationPerformed, PositiveEnvelopeKeyDependenciesIncluded, CompleteRecoveryInventory,
            FullRecoverySupported, CustomerReviewAuthorized, ExternalExecutionAuthorized, KeyRetirementAuthorized,
            IndependentKeyBackupVerified, IndependentBackupCoverage, SourceHostFenced, ProductionRecoveryQualified
        }, CredentialRules.Json)));
    }
    public string Schema => "platform-studio.credential-absence-scope-inspection.v1";
    public CredentialVaultNamespaceIdentity Namespace { get; }
    public DateTimeOffset ObservedUtc { get; }
    public long AuthorityRevision { get; }
    public string AuthorityStateSha256 { get; }
    public int PositiveEnvelopeCount { get; }
    public IReadOnlyList<CredentialAbsenceScopeDependency> AbsenceScopes { get; }
    public IReadOnlyList<string> AbsenceRequiredKeyIds { get; }
    public int AuthenticatedAbsenceCount => AbsenceScopes.Count;
    public bool AbsenceScopeAuthenticationPerformed => AuthenticatedAbsenceCount > 0;
    public bool PositiveEnvelopeAuthenticationPerformed => false;
    public bool PositiveEnvelopeKeyDependenciesIncluded => false;
    public bool CompleteRecoveryInventory => false;
    public bool FullRecoverySupported => false;
    public bool CustomerReviewAuthorized => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool SourceHostFenced => false;
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}
