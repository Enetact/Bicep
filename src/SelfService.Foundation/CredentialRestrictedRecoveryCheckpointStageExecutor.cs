using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

/// <summary>One restricted inactive stage produced while its configured current checkpoint remained read-held.</summary>
public sealed class CredentialRestrictedRecoveryCheckpointStageResult
{
    internal CredentialRestrictedRecoveryCheckpointStageResult(CredentialRestrictedRecoverySetCheckpointAuthorityIdentity identity,
        CredentialRestrictedRecoverySetCheckpoint checkpoint, CredentialRestrictedRecoveryStageReceipt stage)
    {
        AuthorityIdentity = identity; Checkpoint = checkpoint; StageReceipt = stage;
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, AccessProfile, authorityIdentity = identity, checkpoint,
            stageReceiptSha256 = stage.Sha256, stageCompletionCoreSha256 = stage.CompletionCoreSha256,
            stageCompletionProofSha256 = stage.CompletionProofSha256, planSha256 = stage.PlanSha256,
            sourceSetSha256 = stage.SourceSetSha256, CheckpointReadLeaseRetainedDuringStaging, InactiveRecoveryHold,
            NormalVaultUsable, PromotionAuthorized, ActivationPerformed, NamespaceAdopted, SourceHostFenced,
            ExternalExecutionAuthorized, KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage,
            CustomerReviewAdmissionPerformed, ProductionRecoveryQualified
        }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-restricted-recovery-checkpoint-stage.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public CredentialRestrictedRecoverySetCheckpointAuthorityIdentity AuthorityIdentity { get; }
    public CredentialRestrictedRecoverySetCheckpoint Checkpoint { get; }
    public CredentialRestrictedRecoveryStageReceipt StageReceipt { get; }
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

/// <summary>Trusted-owner composition of a concrete restricted checkpoint authority and restricted stage executor.</summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoveryCheckpointStageExecutor
{
    const string Profile = "FoundationBoundDispatchOnly";
    readonly CredentialRestrictedRecoverySetCheckpointAuthority authority;
    readonly CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedIdentity;
    readonly CredentialRestrictedRecoveryStageExecutor executor;

    public CredentialRestrictedRecoveryCheckpointStageExecutor(CredentialRestrictedRecoverySetCheckpointAuthority authority,
        CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedIdentity, CredentialRestrictedRecoveryStageExecutor executor)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("windows-required");
        this.authority = authority ?? throw Failure("identity-mismatch");
        this.expectedIdentity = expectedIdentity ?? throw Failure("identity-mismatch");
        this.executor = executor ?? throw Failure("configuration-invalid");
        if (expectedIdentity.AccessProfile != Profile || authority.DescribeIdentity() != expectedIdentity) throw Failure("identity-mismatch");
    }

    public async Task<CredentialRestrictedRecoveryCheckpointStageResult> StageLatestAsync(CredentialRestrictedRecoverySet set,
        CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, RSA pinnedPublicKey,
        long expectedCheckpointSequence, string expectedCheckpointSha256, CancellationToken cancellationToken = default)
    {
        var stageReturned = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = authority.AcquireReadLease(expectedCheckpointSequence, expectedCheckpointSha256);
            if (lease.Identity != expectedIdentity) throw Failure("identity-mismatch");
            var head = lease.Checkpoint;
            // Actual current authority constrains selection before caller RSA, credential providers or destination access.
            if (plan is null || set is null || plan.Sha256 != reviewedPlanSha256 || plan.AccessProfile != Profile ||
                head.AccessProfile != Profile || plan.SourceSetAuthority.AccessProfile != Profile || head.SetAuthority.AccessProfile != Profile ||
                plan.SourceSignature.AccessProfile != Profile || plan.SourceNamespace != expectedIdentity.SourceNamespace ||
                head.SourceNamespace != expectedIdentity.SourceNamespace || plan.SourceNamespace != head.SourceNamespace ||
                plan.SourceSetAuthority != head.SetAuthority || set.Sha256 != head.SetAuthority.ArtifactSha256 ||
                head.SetAuthority.SignerKeyId != expectedIdentity.SignerKeyId || head.SetAuthority.PublicKeySha256 != expectedIdentity.PublicKeySha256 ||
                plan.SourceSignature.InventorySha256 != head.InventorySha256) throw Failure("selection-mismatch");
            using var publicKey = FoundationBackupSignatures.PublicCopy(pinnedPublicKey);
            if (FoundationAuditAnchors.PublicKeyFingerprint(publicKey) != expectedIdentity.PublicKeySha256) throw Failure("signer-mismatch");
            lease.AssertStagingCurrent(plan); cancellationToken.ThrowIfCancellationRequested();
            // Retain this actual shared lease through every inner await, fresh reservation and keyed output readback.
            var stage = await executor.StageAsync(set, plan, reviewedPlanSha256, publicKey, cancellationToken);
            stageReturned = true;
            var result = new CredentialRestrictedRecoveryCheckpointStageResult(expectedIdentity, head, stage);
            using var heldStage = CredentialRestrictedRecoveryStaging.OpenCompletedReadLease(plan, reviewedPlanSha256, stage.Sha256);
            lease.AssertStagingCurrent(plan); heldStage.AssertCurrent(); cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (stageReturned)
        { throw Failure("outcome-unknown"); } // A completed held stage may exist. Preserve it; never retry automatically.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Restricted checkpoint-backed credential staging canceled.", cancellationToken); }
        catch (CredentialException) { throw; }
        catch (FoundationStoreException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }
    static CredentialException Failure(string reason) => new("credential-restricted-recovery-checkpoint-stage-" + reason);
}
