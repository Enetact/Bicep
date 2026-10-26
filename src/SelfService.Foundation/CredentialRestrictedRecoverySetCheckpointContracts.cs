using System.Runtime.Versioning;

namespace SelfService.Foundation;

public sealed record CredentialRestrictedRecoverySetCheckpointAuthorityIdentity(string CustomerId, string CellId, string AccessProfile,
    CredentialVaultNamespaceIdentity SourceNamespace, string AuthorityId, string SignerKeyId,
    string PublicKeySha256, string CommitmentSha256);

public sealed record CredentialRestrictedRecoverySetCheckpoint(string AuthorityId, string AuthorityCommitmentSha256, string AccessProfile, long Sequence,
    string PreviousSha256, DateTimeOffset AcceptedUtc, CredentialVaultNamespaceIdentity SourceNamespace,
    CredentialRestrictedRecoverySetAuthority SetAuthority, string InventorySha256, string RehearsalSha256,
    DateTimeOffset CapturedUtc, DateTimeOffset IssuedUtc, DateTimeOffset NotBeforeUtc, DateTimeOffset ExpiresUtc, string Sha256)
{
    public long SourceGeneration => SetAuthority.Generation;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool KeyBackupVerified => false;
    public bool PromotionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
}

/// <summary>Retain through the complete consuming operation. This is local cooperating-owner custody, not a bearer grant.</summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoverySetCheckpointReadLease : IDisposable
{
    readonly CredentialRestrictedRecoverySetCheckpointAuthority owner;
    readonly object gate = new();
    FileStream? guard;
    public CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity { get; }
    public CredentialRestrictedRecoverySetCheckpoint Checkpoint { get; }
    internal CredentialRestrictedRecoverySetCheckpointReadLease(CredentialRestrictedRecoverySetCheckpointAuthority owner, FileStream guard,
        CredentialRestrictedRecoverySetCheckpointAuthorityIdentity identity, CredentialRestrictedRecoverySetCheckpoint checkpoint)
    { this.owner = owner; this.guard = guard; Identity = identity; Checkpoint = checkpoint; }
    public void AssertCurrent()
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("CredentialRestrictedRecoveryCheckpointLeaseDisposed", 403);
            owner.AssertLease(Identity, Checkpoint);
        }
    }
    internal void AssertStagingCurrent(CredentialRestrictedRecoveryStagePlan plan)
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("CredentialRestrictedRecoveryCheckpointLeaseDisposed", 403);
            owner.AssertStagingLease(Identity, Checkpoint, plan);
        }
    }
    internal void AssertCurrentAt(DateTimeOffset observedUtc)
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("CredentialRestrictedRecoveryCheckpointLeaseDisposed", 403);
            owner.AssertLeaseAt(Identity, Checkpoint, observedUtc);
        }
    }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
