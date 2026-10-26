using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed record CredentialRestrictedRecoveryDestinationKeyIdentity(string CustomerId, string CellId, string KeyId, string CommitmentSha256);

public sealed class CredentialRestrictedRecoveryStagePlan
{
    internal CredentialRestrictedRecoveryStagePlan(string root, string sid, string planId, CredentialRestrictedRecoverySetAuthority authority,
        CredentialRestrictedRecoverySetSignature signature, CredentialVaultNamespaceIdentity sourceNamespace, string rootCommitment,
        CredentialRestrictedRecoveryDestinationKeyIdentity destinationKey, string purposeSha, DateTimeOffset prepared, DateTimeOffset expires,
        IEnumerable<CredentialRecoveryPreservedProfile> preserved, CredentialRestrictedRecoveryInventory inventory)
    {
        RootPath = root; OwnerSid = sid; PlanId = planId; SourceSetAuthority = authority; SourceSignature = signature; SourceNamespace = sourceNamespace;
        DestinationRootCommitmentSha256 = rootCommitment; DestinationKey = destinationKey; PurposeSha256 = purposeSha; PreparedUtc = prepared; ExpiresUtc = expires;
        PreservedProfiles = Array.AsReadOnly(preserved.ToArray()); SourceInventory = inventory; AbsenceScopes = Array.AsReadOnly(inventory.AbsenceScopes.ToArray());
        AffectedScopes = Array.AsReadOnly(inventory.ProfileDependencies.Select(p => new FoundationCredentialRecoveryScope(p.ProjectId, p.EnvironmentId))
            .Concat(inventory.AbsenceScopes.Select(p => new FoundationCredentialRecoveryScope(p.ProjectId, p.EnvironmentId))).Distinct()
            .OrderBy(p => p.ProjectId, StringComparer.Ordinal).ThenBy(p => p.EnvironmentId, StringComparer.Ordinal).ToArray());
        Sha256 = CredentialRestrictedRecoveryStaging.PlanDigest(this);
    }
    internal string RootPath { get; }
    internal string OwnerSid { get; }
    internal CredentialRestrictedRecoveryInventory SourceInventory { get; }
    public string Schema => "platform-studio.credential-restricted-recovery-stage-plan.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string PlanId { get; }
    public CredentialRestrictedRecoverySetAuthority SourceSetAuthority { get; }
    public CredentialRestrictedRecoverySetSignature SourceSignature { get; }
    public CredentialVaultNamespaceIdentity SourceNamespace { get; }
    public string DestinationRootCommitmentSha256 { get; }
    public CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey { get; }
    public string PurposeSha256 { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public IReadOnlyList<CredentialRecoveryPreservedProfile> PreservedProfiles { get; }
    public IReadOnlyList<CredentialAbsenceScopeDependency> AbsenceScopes { get; }
    public IReadOnlyList<FoundationCredentialRecoveryScope> AffectedScopes { get; }
    public string Sha256 { get; }
}
public sealed record CredentialRestrictedRecoveryStagedAbsence(CredentialAbsenceScopeDependency Source, CredentialAbsenceScopeDependency Destination);
public sealed class CredentialRestrictedRecoveryStageReceipt
{
    internal CredentialRestrictedRecoveryStageReceipt(string planSha, string sourceSetSha, string stageId, string vaultId, string authorityId, string rootCommitment,
        CredentialRestrictedRecoveryDestinationKeyIdentity key, string bindingSha, string stateSha, string coreSha, string completionProofSha, DateTimeOffset reserved, DateTimeOffset completed,
        IEnumerable<CredentialRecoveryStagedProfile> profiles, IEnumerable<CredentialRestrictedRecoveryStagedAbsence> absences)
    {
        PlanSha256 = planSha; SourceSetSha256 = sourceSetSha; StageId = stageId; StagedVaultNamespaceId = vaultId; StagedAuthorityNamespaceId = authorityId;
        DestinationRootCommitmentSha256 = rootCommitment; DestinationKey = key; BindingSha256 = bindingSha; StateSha256 = stateSha; CompletionCoreSha256 = coreSha;
        CompletionProofSha256 = completionProofSha;
        ReservedUtc = reserved; CompletedUtc = completed; Profiles = Array.AsReadOnly(profiles.ToArray()); Absences = Array.AsReadOnly(absences.ToArray());
        Sha256 = CredentialRestrictedRecoveryStaging.ReceiptDigest(this);
    }
    public string Schema => "platform-studio.credential-restricted-recovery-stage-receipt.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string PlanSha256 { get; }
    public string SourceSetSha256 { get; }
    public string StageId { get; }
    public string StagedVaultNamespaceId { get; }
    public string StagedAuthorityNamespaceId { get; }
    public string DestinationRootCommitmentSha256 { get; }
    public CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey { get; }
    public string BindingSha256 { get; }
    public string StateSha256 { get; }
    public string CompletionCoreSha256 { get; }
    public string CompletionProofSha256 { get; }
    public DateTimeOffset ReservedUtc { get; }
    public DateTimeOffset CompletedUtc { get; }
    public IReadOnlyList<CredentialRecoveryStagedProfile> Profiles { get; }
    public IReadOnlyList<CredentialRestrictedRecoveryStagedAbsence> Absences { get; }
    public bool InactiveRecoveryHold => true;
    public bool NormalVaultUsable => false;
    public bool PromotionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyMaterialExported => false;
    public bool KeyRetirementAuthorized => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}
internal sealed record CredentialRestrictedStageEnvelopeContext(string StageId, string StagedVaultNamespaceId, string StagedAuthorityNamespaceId, string BindingSha256);
internal sealed record CredentialRestrictedStageCompletionProof(int Version, string Kind, string AccessProfile, string Algorithm, string Tag, string Sha256);
internal sealed record CredentialRestrictedStageAbsenceProof(int Version, string Kind, string AccessProfile, string ProfileIdSha256, CredentialScope Scope, string ScopeSha256,
    string SourceProofSha256, string SourceKeyId, CredentialVaultNamespaceIdentity SourceNamespace, string StageId, string StagedVaultNamespaceId,
    string StagedAuthorityNamespaceId, string PlanSha256, string SourceSetSha256, string DestinationRootCommitmentSha256,
    CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey, string BindingSha256, string CompletionCoreSha256, string Algorithm, string Tag, string Sha256);

internal sealed class CredentialRestrictedRecoveryStageReadLease(FileStream guard, CredentialRestrictedRecoveryStageReceipt receipt,
    IEnumerable<RetainedCredentialRecoveryEnvelope> envelopes, IEnumerable<CredentialRestrictedStageAbsenceProof> proofs,
    CredentialRestrictedStageCompletionProof completionProof, IEnumerable<byte[]> buffers, Action assertCurrent) : IDisposable
{
    FileStream? held = guard; readonly object gate = new(); readonly byte[][] owned = buffers.ToArray();
    internal CredentialRestrictedRecoveryStageReceipt Receipt { get; } = receipt;
    internal IReadOnlyList<RetainedCredentialRecoveryEnvelope> Envelopes { get; } = Array.AsReadOnly(envelopes.ToArray());
    internal IReadOnlyList<CredentialRestrictedStageAbsenceProof> AbsenceProofs { get; } = Array.AsReadOnly(proofs.ToArray());
    internal CredentialRestrictedStageCompletionProof CompletionProof { get; } = completionProof;
    internal void AssertCurrent() { lock (gate) { if (held is null) throw CredentialRestrictedRecoveryStaging.Failure("lease-disposed"); assertCurrent(); } }
    public void Dispose() { lock (gate) { if (held is null) return; foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes); held.Dispose(); held = null; } }
}
