using System.Runtime.Versioning;
using System.Text.Json;

namespace SelfService.Foundation;

[SupportedOSPlatform("windows")]
internal sealed class CredentialRestrictedRecoveryAdoptionSession : IDisposable
{
    readonly FoundationCellActivationMetadataReadLease activation;
    readonly CredentialRestrictedRecoveryJointSession recovery;
    readonly string vaultRoot, rollbackRoot;
    int disposed;
    internal FoundationCredentialRestrictedRecoveryAdoptionSelection Selection { get; }
    internal CredentialRestrictedRecoverySetCheckpointAuthorityIdentity CheckpointAuthority => recovery.Identity;
    internal CredentialRestrictedRecoverySetCheckpoint Checkpoint => recovery.Checkpoint;
    internal CredentialRestrictedRecoveryStageReceipt Receipt => recovery.Receipt;

    CredentialRestrictedRecoveryAdoptionSession(FoundationCellActivationMetadataReadLease activation, CredentialRestrictedRecoveryJointSession recovery,
        string vaultRoot, string rollbackRoot)
    {
        this.activation = activation; this.recovery = recovery; this.vaultRoot = vaultRoot; this.rollbackRoot = rollbackRoot;
        var ns = recovery.Identity.SourceNamespace;
        Selection = new(RootCommitment(vaultRoot, "vault", ns), RootCommitment(rollbackRoot, "rollback", ns),
            activation.Identity, activation.Generation, activation.BindingSha256);
    }

    internal static CredentialRestrictedRecoveryAdoptionSession Open(FoundationCellActivationAuthority authority,
        FoundationCellActivationAuthorityIdentity expectedActivationIdentity, long expectedActivationGeneration, string? expectedActivationBindingSha256,
        CredentialRestrictedRecoverySetCheckpointAuthority checkpoints, CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity,
        CredentialRestrictedRecoveryStageVerifier verifier, CredentialRestrictedRecoveryStagePlan plan, string planSha, string receiptSha,
        long checkpointSequence, string checkpointSha, string futureVaultDirectory, string futureRollbackDirectory)
    {
        FoundationCellActivationMetadataReadLease? activation = null; CredentialRestrictedRecoveryJointSession? recovery = null;
        try
        {
            var vault = CanonicalFutureRoot(futureVaultDirectory); var rollback = CanonicalFutureRoot(futureRollbackDirectory);
            ValidateFutureRoots(vault, rollback, plan.SourceNamespace);
            activation = authority.AcquireMetadataReadLease(expectedActivationIdentity, expectedActivationGeneration, expectedActivationBindingSha256);
            recovery = CredentialRestrictedRecoveryJointSession.Open(checkpoints, expectedCheckpointIdentity, verifier, plan, planSha, receiptSha, checkpointSequence, checkpointSha);
            var session = new CredentialRestrictedRecoveryAdoptionSession(activation, recovery, vault, rollback); session.AssertCurrent();
            activation = null; recovery = null; return session;
        }
        finally { recovery?.Dispose(); activation?.Dispose(); }
    }

    internal void AssertCurrent()
    {
        RequireHeld(); activation.AssertCurrent(); recovery.AssertCurrent();
        ValidateFutureRoots(vaultRoot, rollbackRoot, CheckpointAuthority.SourceNamespace);
        activation.AssertSnapshotCurrent(); recovery.AssertSnapshotCurrent(CancellationToken.None);
    }
    internal async Task<CredentialRestrictedRecoveryStageVerification> VerifyAsync(CancellationToken token)
    {
        AssertCurrent(); token.ThrowIfCancellationRequested();
        var result = await recovery.VerifyAdoptionAsync(this, token);
        AssertCurrent(); token.ThrowIfCancellationRequested(); return result;
    }
    // Called only by the concrete typed verifier after its stage and checkpoint clock samples.
    // Future paths are eligibility conditions, never reservations or authority grants.
    internal void AssertPreKeySelectionCurrent(CredentialRestrictedRecoverySetCheckpointReadLease checkpoint, CancellationToken token)
    {
        RequireHeld(); recovery.AssertCheckpointReference(checkpoint); activation.AssertCurrent();
        ValidateFutureRoots(vaultRoot, rollbackRoot, CheckpointAuthority.SourceNamespace);
        activation.AssertSnapshotCurrent(); checkpoint.AssertCurrentAt(checkpoint.Checkpoint.AcceptedUtc); token.ThrowIfCancellationRequested();
    }
    internal void AssertVerifiedCurrent(CredentialRestrictedRecoveryStageVerification result, DateTimeOffset observedUtc, CancellationToken token)
    {
        RequireHeld(); ValidateFutureRoots(vaultRoot, rollbackRoot, CheckpointAuthority.SourceNamespace);
        activation.AssertCurrentAt(observedUtc);
        recovery.AssertVerifiedCurrent(result, observedUtc, token);
    }
    static string CanonicalFutureRoot(string path) => CredentialNamespaceCustody.LocalPath(path).ToUpperInvariant();
    static void ValidateFutureRoots(string vault, string rollback, CredentialVaultNamespaceIdentity ns)
    {
        if (Overlaps(vault, rollback)) throw Failure("future-roots-overlap");
        _ = CredentialNamespaceCustody.PreflightNew(vault, "credential-vault-scoped-bound-dispatch", ns.CustomerId, ns.CellId);
        _ = CredentialNamespaceCustody.PreflightNew(rollback, "credential-rollback-scoped-bound-authority", ns.CustomerId, ns.CellId);
        CredentialRestrictedRecoveryStaging.CheckParent(vault); CredentialRestrictedRecoveryStaging.CheckParent(rollback);
    }
    static bool Overlaps(string first, string second) => first == second || first.StartsWith(second + "\\", StringComparison.Ordinal) || second.StartsWith(first + "\\", StringComparison.Ordinal);
    static string RootCommitment(string path, string role, CredentialVaultNamespaceIdentity ns) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = "platform-studio.restricted-adoption-future-root.v1", accessProfile = "FoundationBoundDispatchOnly", ns.CustomerId, ns.CellId, role, canonicalPath = path }, CredentialRules.Json));
    void RequireHeld() { if (Volatile.Read(ref disposed) != 0) throw Failure("session-disposed"); }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; try { recovery.Dispose(); } finally { activation.Dispose(); } }
    static CredentialException Failure(string code) => new("credential-restricted-recovery-adoption-" + code);
}
