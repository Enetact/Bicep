namespace SelfService.Foundation;

public sealed record CredentialScopedRecoverySetCheckpointAuthorityIdentity(string CustomerId, string CellId,
    CredentialVaultNamespaceIdentity SourceNamespace, string AuthorityId, string SignerKeyId,
    string PublicKeySha256, string CommitmentSha256);

public sealed record CredentialScopedRecoverySetCheckpoint(string AuthorityId, string AuthorityCommitmentSha256, long Sequence,
    string PreviousSha256, DateTimeOffset AcceptedUtc, CredentialVaultNamespaceIdentity SourceNamespace,
    CredentialScopedRecoverySetAuthority SetAuthority, string InventorySha256, string RehearsalSha256,
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
public sealed class CredentialScopedRecoverySetCheckpointReadLease : IDisposable
{
    readonly CredentialScopedRecoverySetCheckpointAuthority owner;
    readonly object gate = new();
    FileStream? guard;
    public CredentialScopedRecoverySetCheckpointAuthorityIdentity Identity { get; }
    public CredentialScopedRecoverySetCheckpoint Checkpoint { get; }
    internal CredentialScopedRecoverySetCheckpointReadLease(CredentialScopedRecoverySetCheckpointAuthority owner, FileStream guard,
        CredentialScopedRecoverySetCheckpointAuthorityIdentity identity, CredentialScopedRecoverySetCheckpoint checkpoint)
    { this.owner = owner; this.guard = guard; Identity = identity; Checkpoint = checkpoint; }
    public void AssertCurrent()
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("CredentialScopedRecoveryCheckpointLeaseDisposed", 403);
            owner.AssertLease(Identity, Checkpoint);
        }
    }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
