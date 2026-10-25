namespace SelfService.Foundation;

public sealed record CredentialRecoverySetCheckpointAuthorityIdentity(string CustomerId, string CellId,
    CredentialVaultNamespaceIdentity SourceNamespace, string AuthorityId, string SignerKeyId,
    string PublicKeySha256, string CommitmentSha256);

public sealed record CredentialRecoverySetCheckpoint(string AuthorityId, string AuthorityCommitmentSha256, long Sequence,
    string PreviousSha256, DateTimeOffset AcceptedUtc, CredentialVaultNamespaceIdentity SourceNamespace,
    CredentialRecoverySetAuthority SetAuthority, string InventorySha256, string RehearsalSha256,
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
public sealed class CredentialRecoverySetCheckpointReadLease : IDisposable
{
    readonly CredentialRecoverySetCheckpointAuthority owner;
    readonly object gate = new();
    FileStream? guard;
    public CredentialRecoverySetCheckpointAuthorityIdentity Identity { get; }
    public CredentialRecoverySetCheckpoint Checkpoint { get; }
    internal CredentialRecoverySetCheckpointReadLease(CredentialRecoverySetCheckpointAuthority owner, FileStream guard,
        CredentialRecoverySetCheckpointAuthorityIdentity identity, CredentialRecoverySetCheckpoint checkpoint)
    { this.owner = owner; this.guard = guard; Identity = identity; Checkpoint = checkpoint; }
    public void AssertCurrent()
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("CredentialRecoveryCheckpointLeaseDisposed", 403);
            owner.AssertLease(Identity, Checkpoint);
        }
    }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
