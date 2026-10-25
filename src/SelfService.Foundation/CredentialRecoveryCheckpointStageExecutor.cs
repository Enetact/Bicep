using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

/// <summary>One inactive stage produced while the configured credential-set checkpoint remained read-held.</summary>
public sealed class CredentialRecoveryCheckpointStageResult
{
    internal CredentialRecoveryCheckpointStageResult(CredentialRecoverySetCheckpointAuthorityIdentity identity,
        CredentialRecoverySetCheckpoint checkpoint, CredentialRecoveryStageReceipt stage)
    {
        AuthorityIdentity = identity; Checkpoint = checkpoint; StageReceipt = stage;
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, authorityCommitmentSha256 = identity.CommitmentSha256, checkpointSha256 = checkpoint.Sha256,
            checkpointSequence = checkpoint.Sequence, stageReceiptSha256 = stage.Sha256, planSha256 = stage.PlanSha256,
            sourceSetSha256 = stage.SourceSetSha256, CheckpointReadLeaseRetainedDuringStaging, InactiveRecoveryHold,
            NormalVaultUsable, PromotionAuthorized, ActivationPerformed, NamespaceAdopted, SourceHostFenced,
            ExternalExecutionAuthorized, KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage,
            CustomerReviewAdmissionPerformed, ProductionRecoveryQualified
        }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-recovery-checkpoint-stage.v1";
    public CredentialRecoverySetCheckpointAuthorityIdentity AuthorityIdentity { get; }
    public CredentialRecoverySetCheckpoint Checkpoint { get; }
    public CredentialRecoveryStageReceipt StageReceipt { get; }
    public bool CheckpointReadLeaseRetainedDuringStaging => true;
    public bool InactiveRecoveryHold => true;
    public bool NormalVaultUsable => false;
    public bool PromotionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool NamespaceAdopted => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool CustomerReviewAdmissionPerformed => false;
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

/// <summary>Explicit trusted-owner composition. An incoming set cannot supply its own latest-checkpoint authority.</summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryCheckpointStageExecutor
{
    readonly CredentialRecoverySetCheckpointAuthority authority;
    readonly CredentialRecoverySetCheckpointAuthorityIdentity expectedIdentity;
    readonly CredentialRecoveryStageExecutor executor;

    public CredentialRecoveryCheckpointStageExecutor(CredentialRecoverySetCheckpointAuthority authority,
        CredentialRecoverySetCheckpointAuthorityIdentity expectedIdentity, CredentialRecoveryStageExecutor executor)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        this.authority = authority ?? throw Failure("identity-mismatch");
        this.expectedIdentity = expectedIdentity ?? throw Failure("identity-mismatch");
        this.executor = executor ?? throw Failure("configuration-invalid");
        if (authority.DescribeIdentity() != expectedIdentity) throw Failure("identity-mismatch");
    }

    public async Task<CredentialRecoveryCheckpointStageResult> StageLatestAsync(CredentialRecoverySet set,
        CredentialRecoveryStagePlan plan, string reviewedPlanSha256, RSA pinnedPublicKey,
        long expectedCheckpointSequence, string expectedCheckpointSha256, CancellationToken cancellationToken = default)
    {
        var stageReturned = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = authority.AcquireReadLease(expectedCheckpointSequence, expectedCheckpointSha256);
            if (lease.Identity != expectedIdentity) throw Failure("identity-mismatch");
            var head = lease.Checkpoint;
            // Current stored authority is a constraint before any caller RSA/key-provider or destination access.
            if (plan is null || set is null || plan.Sha256 != reviewedPlanSha256 || plan.SourceNamespace != expectedIdentity.SourceNamespace ||
                head.SourceNamespace != expectedIdentity.SourceNamespace || plan.SourceNamespace != head.SourceNamespace ||
                plan.SourceSetAuthority != head.SetAuthority || set.Sha256 != head.SetAuthority.ArtifactSha256 ||
                head.SetAuthority.SignerKeyId != expectedIdentity.SignerKeyId || head.SetAuthority.PublicKeySha256 != expectedIdentity.PublicKeySha256 ||
                plan.SourceSignature.InventorySha256 != head.InventorySha256) throw Failure("selection-mismatch");
            using var publicKey = FoundationBackupSignatures.PublicCopy(pinnedPublicKey);
            if (FoundationAuditAnchors.PublicKeyFingerprint(publicKey) != expectedIdentity.PublicKeySha256) throw Failure("signer-mismatch");
            lease.AssertCurrent(); cancellationToken.ThrowIfCancellationRequested();
            // The actual concrete shared lease spans every inner await, fresh reservation, output check and final readback.
            var stage = await executor.StageAsync(set, plan, reviewedPlanSha256, publicKey, cancellationToken);
            stageReturned = true;
            lease.AssertCurrent(); cancellationToken.ThrowIfCancellationRequested();
            return new(expectedIdentity, head, stage);
        }
        catch (Exception) when (stageReturned)
        { throw Failure("outcome-unknown"); } // A completed held stage may exist. Preserve it; never retry automatically.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Checkpoint-backed credential staging canceled.", cancellationToken); }
        catch (CredentialException) { throw; }
        catch (FoundationStoreException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }
    static CredentialException Failure(string reason) => new("credential-recovery-checkpoint-stage-" + reason);
}
