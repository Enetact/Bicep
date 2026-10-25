namespace SelfService.Foundation;

public sealed record FoundationBackupCheckpointAuthorityIdentity(string CustomerId, string CellId, string AuthorityId,
    string SignerKeyId, string PublicKeySha256, string CommitmentSha256);

public sealed record FoundationBackupCheckpoint(string AuthorityId, string AuthorityCommitmentSha256, long Sequence,
    string PreviousSha256, DateTimeOffset AcceptedUtc, FoundationBackupRestoreAuthority RestoreAuthority, string Sha256);

/// <summary>Retain for the complete verification/restore operation; this is not a bearer or cloud authority.</summary>
public sealed class FoundationBackupCheckpointReadLease : IDisposable
{
    readonly FoundationBackupCheckpointAuthority owner;
    readonly object gate = new();
    FileStream? guard;
    public FoundationBackupCheckpointAuthorityIdentity Identity { get; }
    public FoundationBackupCheckpoint Checkpoint { get; }
    internal FoundationBackupCheckpointReadLease(FoundationBackupCheckpointAuthority owner, FileStream guard,
        FoundationBackupCheckpointAuthorityIdentity identity, FoundationBackupCheckpoint checkpoint)
    { this.owner = owner; this.guard = guard; Identity = identity; Checkpoint = checkpoint; }
    public void AssertCurrent()
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("BackupCheckpointLeaseDisposed", 403);
            owner.AssertLease(Identity, Checkpoint);
        }
    }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
