using System.Runtime.Versioning;
using System.Text.Json;

namespace SelfService.Foundation;

// Explicit trusted-owner intent review. This class has no changing operation or execution admission API.
[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryAdoptionCoordinator
{
    readonly FoundationStore store;
    readonly FoundationCellActivationAuthority activation;
    readonly FoundationCellActivationAuthorityIdentity activationIdentity;
    readonly long activationGeneration;
    readonly string? activationHead;
    readonly CredentialRestrictedRecoverySetCheckpointAuthority checkpoints;
    readonly CredentialRestrictedRecoverySetCheckpointAuthorityIdentity checkpointIdentity;
    readonly CredentialRestrictedRecoveryStageVerifier verifier;
    readonly string instance, futureVault, futureRollback;
    readonly TimeSpan timeout;
    internal Action<string>? ReviewFault { get; set; }

    public FoundationCredentialRestrictedRecoveryAdoptionCoordinator(FoundationStore store, FoundationCellActivationAuthority configuredActivationAuthority,
        FoundationCellActivationAuthorityIdentity expectedActivationIdentity, long expectedActivationGeneration, string? expectedActivationBindingSha256,
        CredentialRestrictedRecoverySetCheckpointAuthority configuredCheckpointAuthority, CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity,
        CredentialRestrictedRecoveryStageVerifier verifier, string expectedDatabaseInstanceId, string freshVaultDirectory, string freshRollbackDirectory,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(configuredActivationAuthority); ArgumentNullException.ThrowIfNull(expectedActivationIdentity);
        ArgumentNullException.ThrowIfNull(configuredCheckpointAuthority); ArgumentNullException.ThrowIfNull(expectedCheckpointIdentity); ArgumentNullException.ThrowIfNull(verifier);
        if (expectedActivationIdentity.CustomerId != store.CustomerId || expectedActivationIdentity.CellId != store.CellId ||
            expectedCheckpointIdentity.CustomerId != store.CustomerId || expectedCheckpointIdentity.CellId != store.CellId ||
            expectedCheckpointIdentity.AccessProfile != "FoundationBoundDispatchOnly" || store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId)
            throw Failure("composition-invalid");
        this.store = store; activation = configuredActivationAuthority; activationIdentity = expectedActivationIdentity; activationGeneration = expectedActivationGeneration;
        activationHead = expectedActivationBindingSha256; checkpoints = configuredCheckpointAuthority; checkpointIdentity = expectedCheckpointIdentity;
        this.verifier = verifier; instance = expectedDatabaseInstanceId; futureVault = freshVaultDirectory; futureRollback = freshRollbackDirectory;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    public async Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> PrepareAsync(IReadOnlyList<CustomerContext> requesters,
        CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256, long expectedCheckpointSequence,
        string expectedCheckpointSha256, string purpose, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(requesters); using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequirePlan(plan, reviewedPlanSha256, expectedReceiptSha256);
            store.RequireCredentialRestrictedRecoveryAdoptionPreparation(contexts, plan);
            using var held = Open(plan, reviewedPlanSha256, expectedReceiptSha256, expectedCheckpointSequence, expectedCheckpointSha256);
            ReviewFault?.Invoke("BeforeVerification"); var verification = await held.VerifyAsync(deadline.Token); ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            return store.PrepareCredentialRestrictedRecoveryAdoptionIntent(contexts, plan, verification, held, purpose, expiresUtc, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Restricted adoption intent review canceled.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw Failure("timeout"); }
        catch (CredentialException) { throw; } catch (FoundationStoreException) { throw; }
        catch { throw Failure("unavailable"); }
    }

    public async Task<FoundationCredentialRestrictedRecoveryAdoptionIntent> ApproveAsync(IReadOnlyList<CustomerContext> approvers,
        string requestId, string expectedDigest, CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256,
        long expectedCheckpointSequence, string expectedCheckpointSha256, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(approvers); using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequirePlan(plan, reviewedPlanSha256, expectedReceiptSha256);
            var intent = store.GetCredentialRestrictedRecoveryAdoptionIntentForApproval(contexts, requestId, expectedDigest);
            if (intent.Target.RecoveryTarget.StageTarget.PlanSha256 != reviewedPlanSha256 || intent.Target.RecoveryTarget.StageTarget.ReceiptSha256 != expectedReceiptSha256)
                throw Failure("target-changed");
            using var held = Open(plan, reviewedPlanSha256, expectedReceiptSha256, expectedCheckpointSequence, expectedCheckpointSha256);
            var recovery = FoundationStore.CredentialRestrictedRecoveryJointTarget(plan, held.Receipt, held.CheckpointAuthority, held.Checkpoint);
            if (JsonSerializer.Serialize(held.Selection, CredentialRules.Json) != JsonSerializer.Serialize(intent.Target.Selection, CredentialRules.Json) ||
                JsonSerializer.Serialize(recovery, CredentialRules.Json) != JsonSerializer.Serialize(intent.Target.RecoveryTarget, CredentialRules.Json)) throw Failure("target-changed");
            ReviewFault?.Invoke("BeforeVerification"); var verification = await held.VerifyAsync(deadline.Token); ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            return store.ApproveCredentialRestrictedRecoveryAdoptionIntent(contexts, requestId, expectedDigest, plan, verification, held, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Restricted adoption intent review canceled.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw Failure("timeout"); }
        catch (CredentialException) { throw; } catch (FoundationStoreException) { throw; }
        catch { throw Failure("unavailable"); }
    }

    CredentialRestrictedRecoveryAdoptionSession Open(CredentialRestrictedRecoveryStagePlan plan, string planSha, string receiptSha, long sequence, string head) =>
        CredentialRestrictedRecoveryAdoptionSession.Open(activation, activationIdentity, activationGeneration, activationHead, checkpoints, checkpointIdentity,
            verifier, plan, planSha, receiptSha, sequence, head, futureVault, futureRollback);
    void RequirePlan(CredentialRestrictedRecoveryStagePlan plan, string planSha, string receiptSha)
    {
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != instance || plan is null || plan.AccessProfile != "FoundationBoundDispatchOnly" ||
            plan.SourceNamespace.CustomerId != store.CustomerId || plan.SourceNamespace.CellId != store.CellId || plan.Sha256 != planSha ||
            !CredentialRecoverySets.DigestShape(receiptSha)) throw Failure("selection-invalid");
    }
    static CustomerContext[] Snapshot(IReadOnlyList<CustomerContext> contexts)
    {
        try
        {
            if (contexts is null) throw Failure("contexts-invalid"); var count = contexts.Count;
            if (count is < 1 or > CredentialRestrictedRecoveryStaging.MaximumProfiles) throw Failure("contexts-invalid");
            var copy = new CustomerContext[count]; for (var i = 0; i < count; i++) copy[i] = contexts[i] ?? throw Failure("contexts-invalid");
            if (contexts.Count != count) throw Failure("contexts-invalid"); return copy;
        }
        catch (CredentialException) { throw; } catch { throw Failure("contexts-invalid"); }
    }
    static CredentialException Failure(string code) => new("credential-restricted-recovery-adoption-" + code);
}
