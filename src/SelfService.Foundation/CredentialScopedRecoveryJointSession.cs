namespace SelfService.Foundation;

// The lock order is checkpoint then stage. Only this concrete composition owns these guards;
// neither a caller-supplied receipt nor an incoming checkpoint DTO can stand in for custody.
internal sealed class CredentialScopedRecoveryJointSession : IDisposable
{
    readonly CredentialScopedRecoverySetCheckpointReadLease checkpoint;
    readonly CredentialScopedRecoveryStageVerifier.HeldVerification stage;
    int disposed;

    CredentialScopedRecoveryJointSession(CredentialScopedRecoverySetCheckpointReadLease checkpoint,
        CredentialScopedRecoveryStageVerifier.HeldVerification stage)
    { this.checkpoint = checkpoint; this.stage = stage; }

    internal CredentialScopedRecoverySetCheckpointAuthorityIdentity Identity => checkpoint.Identity;
    internal CredentialScopedRecoverySetCheckpoint Checkpoint => checkpoint.Checkpoint;
    internal CredentialScopedRecoveryStageReceipt Receipt => stage.Receipt;

    internal static CredentialScopedRecoveryJointSession Open(CredentialScopedRecoverySetCheckpointAuthority authority,
        CredentialScopedRecoverySetCheckpointAuthorityIdentity expectedIdentity, CredentialScopedRecoveryStageVerifier verifier,
        CredentialScopedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256,
        long expectedSequence, string expectedHeadSha256)
    {
        CredentialScopedRecoverySetCheckpointReadLease? checkpoint = null;
        CredentialScopedRecoveryStageVerifier.HeldVerification? stage = null;
        try
        {
            checkpoint = authority.AcquireReadLease(expectedSequence, expectedHeadSha256);
            var head = checkpoint.Checkpoint;
            if (checkpoint.Identity != expectedIdentity || plan.SourceNamespace != expectedIdentity.SourceNamespace ||
                head.SourceNamespace != expectedIdentity.SourceNamespace || plan.SourceSetAuthority != head.SetAuthority ||
                plan.SourceSignature.InventorySha256 != head.InventorySha256 ||
                head.SetAuthority.SignerKeyId != expectedIdentity.SignerKeyId || head.SetAuthority.PublicKeySha256 != expectedIdentity.PublicKeySha256)
                throw Failure("checkpoint-selection-mismatch");
            checkpoint.AssertCurrent();
            stage = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
            var result = new CredentialScopedRecoveryJointSession(checkpoint, stage);
            result.AssertCurrent(); checkpoint = null; stage = null; return result;
        }
        finally { stage?.Dispose(); checkpoint?.Dispose(); }
    }

    internal void AssertCurrent()
    {
        if (Volatile.Read(ref disposed) != 0) throw Failure("session-disposed");
        checkpoint.AssertCurrent(); stage.AssertCurrent();
    }

    internal async Task<CredentialScopedRecoveryStageVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        AssertCurrent(); cancellationToken.ThrowIfCancellationRequested();
        var verified = await stage.VerifyAsync(cancellationToken);
        AssertCurrent(); cancellationToken.ThrowIfCancellationRequested(); return verified;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stage.Dispose(); } finally { checkpoint.Dispose(); }
    }

    static CredentialException Failure(string reason) => new("credential-scoped-recovery-joint-review-" + reason);
}
