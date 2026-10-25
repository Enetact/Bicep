using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed record CredentialRecoveryDestinationKeyIdentity(string CustomerId, string CellId, string KeyId, string CommitmentSha256);
public sealed record CredentialRecoveryPreservedProfile(string ProfileIdSha256, string ProfileSha256);

// Prepared by explicit trusted cell-owner composition. Its digest is a review challenge, not an authenticated human approval.
public sealed class CredentialRecoveryStagePlan
{
    internal CredentialRecoveryStagePlan(string rootPath, string ownerSid, string planId, CredentialRecoverySetAuthority authority,
        CredentialRecoverySetSignature signature, CredentialVaultNamespaceIdentity sourceNamespace, string rootCommitment,
        CredentialRecoveryDestinationKeyIdentity destinationKey, string purposeSha256, DateTimeOffset preparedUtc, DateTimeOffset expiresUtc,
        IEnumerable<CredentialRecoveryPreservedProfile> preservedProfiles, IEnumerable<string> absentProfileIds, CredentialRecoveryInventory sourceInventory)
    {
        RootPath = rootPath; OwnerSid = ownerSid; PlanId = planId; SourceSetAuthority = authority; SourceSignature = signature;
        SourceNamespace = sourceNamespace; DestinationRootCommitmentSha256 = rootCommitment; DestinationKey = destinationKey;
        PurposeSha256 = purposeSha256; PreparedUtc = preparedUtc; ExpiresUtc = expiresUtc;
        PreservedProfiles = Array.AsReadOnly(preservedProfiles.ToArray());
        AbsentProfileIds = Array.AsReadOnly(absentProfileIds.ToArray());
        SourceInventory = sourceInventory;
        Sha256 = CredentialRecoveryStaging.PlanDigest(this);
    }
    internal string RootPath { get; }
    internal string OwnerSid { get; }
    internal CredentialRecoveryInventory SourceInventory { get; }
    public string Schema => "platform-studio.credential-recovery-stage-plan.v1";
    public string PlanId { get; }
    public CredentialRecoverySetAuthority SourceSetAuthority { get; }
    public CredentialRecoverySetSignature SourceSignature { get; }
    public CredentialVaultNamespaceIdentity SourceNamespace { get; }
    public string DestinationRootCommitmentSha256 { get; }
    public CredentialRecoveryDestinationKeyIdentity DestinationKey { get; }
    public string PurposeSha256 { get; }
    public DateTimeOffset PreparedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public IReadOnlyList<CredentialRecoveryPreservedProfile> PreservedProfiles { get; }
    public IReadOnlyList<string> AbsentProfileIds { get; }
    public string Sha256 { get; }
}

public sealed record CredentialRecoveryStagedProfile(CredentialRecoveryProfileDependency Source, CredentialRecoveryProfileDependency Destination,
    string PreservedProfileSha256);
internal sealed record CredentialRecoveryStageAuthentication(CredentialRecoveryProfileDependency Dependency, string PreservedProfileSha256);

public sealed class CredentialRecoveryStageReceipt
{
    internal CredentialRecoveryStageReceipt(string planSha256, string sourceSetSha256, string stageId, string vaultId, string authorityId,
        string rootCommitment, CredentialRecoveryDestinationKeyIdentity destinationKey, string bindingSha256, string stateSha256,
        DateTimeOffset reservedUtc, DateTimeOffset completedUtc, IEnumerable<CredentialRecoveryStagedProfile> profiles, IEnumerable<string> absentProfileIds)
    {
        PlanSha256 = planSha256; SourceSetSha256 = sourceSetSha256; StageId = stageId; StagedVaultNamespaceId = vaultId;
        StagedAuthorityNamespaceId = authorityId; DestinationRootCommitmentSha256 = rootCommitment; DestinationKey = destinationKey;
        BindingSha256 = bindingSha256; StateSha256 = stateSha256; ReservedUtc = reservedUtc; CompletedUtc = completedUtc;
        Profiles = Array.AsReadOnly(profiles.ToArray()); AbsentProfileIds = Array.AsReadOnly(absentProfileIds.ToArray());
        Sha256 = CredentialRecoveryStaging.ReceiptDigest(this);
    }
    public string Schema => "platform-studio.credential-recovery-stage-receipt.v1";
    public string PlanSha256 { get; }
    public string SourceSetSha256 { get; }
    public string StageId { get; }
    public string StagedVaultNamespaceId { get; }
    public string StagedAuthorityNamespaceId { get; }
    public string DestinationRootCommitmentSha256 { get; }
    public CredentialRecoveryDestinationKeyIdentity DestinationKey { get; }
    public string BindingSha256 { get; }
    public string StateSha256 { get; }
    public DateTimeOffset ReservedUtc { get; }
    public DateTimeOffset CompletedUtc { get; }
    public IReadOnlyList<CredentialRecoveryStagedProfile> Profiles { get; }
    public IReadOnlyList<string> AbsentProfileIds { get; }
    public bool InactiveRecoveryHold => true;
    public bool NormalVaultUsable => false;
    public bool ActivationPerformed => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyMaterialExported => false;
    public bool KeyRetirementAuthorized => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

internal sealed class CredentialRecoveryStageReadLease(FileStream guard, CredentialRecoveryStageReceipt receipt,
    IEnumerable<RetainedCredentialRecoveryEnvelope> envelopes, IEnumerable<byte[]> buffers, Action assertCurrent) : IDisposable
{
    readonly object gate = new();
    FileStream? retainedGuard = guard;
    readonly byte[][] owned = buffers.ToArray();
    internal CredentialRecoveryStageReceipt Receipt { get; } = receipt;
    internal IReadOnlyList<RetainedCredentialRecoveryEnvelope> Envelopes { get; } = Array.AsReadOnly(envelopes.ToArray());
    internal void AssertCurrent()
    { lock (gate) { if (retainedGuard is null) throw CredentialRecoveryStaging.Failure("lease-disposed"); assertCurrent(); } }
    public void Dispose()
    {
        lock (gate)
        {
            if (retainedGuard is null) return;
            foreach (var bytes in owned) CryptographicOperations.ZeroMemory(bytes);
            retainedGuard.Dispose(); retainedGuard = null;
        }
    }
}
