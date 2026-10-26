using System.Runtime.Versioning;

namespace SelfService.Foundation;

// Acquire checkpoint then stage; release in reverse order. Only actual concrete leases establish custody.
[SupportedOSPlatform("windows")]
internal sealed class CredentialRestrictedRecoveryJointSession : IDisposable
{
    readonly CredentialRestrictedRecoverySetCheckpointReadLease checkpoint;
    readonly CredentialRestrictedRecoveryStageVerifier.HeldVerification stage;
    int disposed;

    CredentialRestrictedRecoveryJointSession(CredentialRestrictedRecoverySetCheckpointReadLease checkpoint,
        CredentialRestrictedRecoveryStageVerifier.HeldVerification stage)
    { this.checkpoint = checkpoint; this.stage = stage; }

    internal CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity => checkpoint.Identity;
    internal CredentialRestrictedRecoverySetCheckpoint Checkpoint => checkpoint.Checkpoint;
    internal CredentialRestrictedRecoveryStageReceipt Receipt => stage.Receipt;

    internal static CredentialRestrictedRecoveryJointSession Open(CredentialRestrictedRecoverySetCheckpointAuthority authority,
        CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedIdentity, CredentialRestrictedRecoveryStageVerifier verifier,
        CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256,
        long expectedSequence, string expectedHeadSha256)
    {
        CredentialRestrictedRecoverySetCheckpointReadLease? checkpoint = null;
        CredentialRestrictedRecoveryStageVerifier.HeldVerification? stage = null;
        try
        {
            checkpoint = authority.AcquireReadLease(expectedSequence, expectedHeadSha256);
            var head = checkpoint.Checkpoint;
            if (expectedIdentity.AccessProfile != "FoundationBoundDispatchOnly" || head.AccessProfile != "FoundationBoundDispatchOnly" ||
                plan.AccessProfile != "FoundationBoundDispatchOnly" || checkpoint.Identity != expectedIdentity ||
                plan.SourceNamespace != expectedIdentity.SourceNamespace || head.SourceNamespace != expectedIdentity.SourceNamespace ||
                plan.SourceSetAuthority != head.SetAuthority || plan.SourceSignature.InventorySha256 != head.InventorySha256 ||
                head.SetAuthority.SignerKeyId != expectedIdentity.SignerKeyId || head.SetAuthority.PublicKeySha256 != expectedIdentity.PublicKeySha256)
                throw Failure("checkpoint-selection-mismatch");
            checkpoint.AssertCurrent();
            stage = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
            var result = new CredentialRestrictedRecoveryJointSession(checkpoint, stage);
            result.AssertCurrent(); checkpoint = null; stage = null; return result;
        }
        finally { stage?.Dispose(); checkpoint?.Dispose(); }
    }

    internal void AssertCurrent()
    {
        if (Volatile.Read(ref disposed) != 0) throw Failure("session-disposed");
        checkpoint.AssertCurrent(); stage.AssertCurrent();
        // The stage clock may have effects. Recheck both snapshots with no further callbacks.
        checkpoint.AssertCurrentAt(checkpoint.Checkpoint.AcceptedUtc); stage.AssertSnapshotCurrent(CancellationToken.None);
    }

    internal async Task<CredentialRestrictedRecoveryStageVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        AssertCurrent(); cancellationToken.ThrowIfCancellationRequested();
        var result = await stage.VerifyJointAsync(checkpoint, cancellationToken);
        AssertCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    internal void AssertSnapshotCurrent(CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) throw Failure("session-disposed");
        checkpoint.AssertCurrentAt(checkpoint.Checkpoint.AcceptedUtc); stage.AssertSnapshotCurrent(token);
    }
    internal void AssertCheckpointReference(CredentialRestrictedRecoverySetCheckpointReadLease actual)
    { if (!ReferenceEquals(actual, checkpoint)) throw Failure("checkpoint-session-mismatch"); }
    internal async Task<CredentialRestrictedRecoveryStageVerification> VerifyAdoptionAsync(CredentialRestrictedRecoveryAdoptionSession adoption, CancellationToken token)
    {
        AssertCurrent(); token.ThrowIfCancellationRequested();
        var result = await stage.VerifyAdoptionAsync(checkpoint, adoption, token);
        AssertCurrent(); token.ThrowIfCancellationRequested(); return result;
    }

    internal void AssertVerifiedCurrent(CredentialRestrictedRecoveryStageVerification result, DateTimeOffset observedUtc, CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) throw Failure("session-disposed");
        checkpoint.AssertCurrentAt(observedUtc);
        stage.AssertVerifiedCurrent(result, token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stage.Dispose(); } finally { checkpoint.Dispose(); }
    }
    static CredentialException Failure(string reason) => new("credential-restricted-recovery-joint-review-" + reason);
}
