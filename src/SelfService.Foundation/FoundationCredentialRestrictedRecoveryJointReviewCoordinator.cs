using System.Text.Json;
using System.Runtime.Versioning;

namespace SelfService.Foundation;

// The existing exact-stage human approval is narrowed by a separately configured current checkpoint.
[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryJointReviewCoordinator
{
    readonly FoundationStore store;
    readonly CredentialRestrictedRecoverySetCheckpointAuthority checkpointAuthority;
    readonly CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity;
    readonly CredentialRestrictedRecoveryStageVerifier verifier;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? ReviewFault { get; set; }

    public FoundationCredentialRestrictedRecoveryJointReviewCoordinator(FoundationStore store,
        CredentialRestrictedRecoverySetCheckpointAuthority configuredCheckpointAuthority,
        CredentialRestrictedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity,
        CredentialRestrictedRecoveryStageVerifier verifier, string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(configuredCheckpointAuthority);
        ArgumentNullException.ThrowIfNull(expectedCheckpointIdentity); ArgumentNullException.ThrowIfNull(verifier);
        if (expectedCheckpointIdentity.AccessProfile != "FoundationBoundDispatchOnly" || expectedCheckpointIdentity.CustomerId != store.CustomerId ||
            expectedCheckpointIdentity.CellId != store.CellId || expectedCheckpointIdentity.SourceNamespace.CustomerId != store.CustomerId ||
            expectedCheckpointIdentity.SourceNamespace.CellId != store.CellId || configuredCheckpointAuthority.DescribeIdentity() != expectedCheckpointIdentity)
            throw Failure("checkpoint-identity-mismatch");
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId) throw Failure("instance-binding-invalid");
        this.store = store; checkpointAuthority = configuredCheckpointAuthority; this.expectedCheckpointIdentity = expectedCheckpointIdentity;
        this.verifier = verifier; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    public async Task<FoundationCredentialRestrictedRecoveryJointVerification> ExecuteApprovedAsync(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedReviewDigest, CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, long expectedCheckpointSequence, string expectedCheckpointSha256, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(executors);
        CredentialRestrictedRecoveryJointAdmission? admission = null;
        CredentialRestrictedRecoveryJointSession? held = null;
        var admissionAttempted = false; var verified = false;
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            var review = store.GetCredentialRestrictedRecoveryJointReviewForExecution(contexts, requestId);
            if (review.Digest != expectedReviewDigest) throw Failure("review-changed");
            if (plan is null || plan.AccessProfile != "FoundationBoundDispatchOnly" || plan.SourceNamespace.CustomerId != store.CustomerId ||
                plan.SourceNamespace.CellId != store.CellId || plan.Sha256 != reviewedPlanSha256 || plan.Sha256 != review.Target.PlanSha256 ||
                !CredentialRecoverySets.DigestShape(expectedReceiptSha256) || expectedReceiptSha256 != review.Target.ReceiptSha256)
                throw Failure("stage-binding-invalid");
            held = CredentialRestrictedRecoveryJointSession.Open(checkpointAuthority, expectedCheckpointIdentity, verifier, plan,
                reviewedPlanSha256, expectedReceiptSha256, expectedCheckpointSequence, expectedCheckpointSha256);
            var actualStage = FoundationStore.CredentialRestrictedRecoveryStageReviewTarget(plan, held.Receipt);
            if (JsonSerializer.Serialize(actualStage, CredentialRules.Json) != JsonSerializer.Serialize(review.Target, CredentialRules.Json))
                throw Failure("target-changed");
            var target = FoundationStore.CredentialRestrictedRecoveryJointTarget(plan, held.Receipt, held.Identity, held.Checkpoint);
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            admissionAttempted = true;
            admission = store.AdmitCredentialRestrictedRecoveryJointVerification(contexts, requestId, expectedReviewDigest, target);
            ReviewFault?.Invoke("AfterAdmission");
            var result = await held.VerifyAsync(deadline.Token); verified = true;
            ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            return store.CompleteCredentialRestrictedRecoveryJointVerification(admission, contexts, target, result, held, deadline.Token);
        }
        catch (Exception) when (admission is not null)
        {
            // Retain both real guards through the terminal audit attempt; Unknown consumes the same ordinary admission.
            try { return store.RecordCredentialRestrictedRecoveryJointUnknown(admission, cancellationToken.IsCancellationRequested ? "CallerCancelled" :
                verified ? "ReviewCompletionUnavailable" : "StageVerificationInterrupted"); }
            catch { throw Failure("outcome-unknown"); }
        }
        catch (FoundationStoreException error) when (error.Code == "RestrictedRecoveryJointAdmissionUncertain") { throw; }
        catch (Exception error)
        {
            if (admissionAttempted && error is not FoundationStoreException) throw new FoundationStoreException("RestrictedRecoveryJointAdmissionUncertain", 503);
            if (!admissionAttempted)
                try { store.RecordCredentialRestrictedRecoveryJointDenied(contexts, requestId, expectedReviewDigest, "StageVerificationInterrupted"); } catch { }
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Restricted joint review canceled.", cancellationToken);
            if (error is OperationCanceledException && deadline.IsCancellationRequested) throw Failure("timeout");
            if (error is CredentialException or FoundationStoreException) throw;
            throw Failure("unavailable");
        }
        finally { held?.Dispose(); }
    }
    void RequireInstance()
    { if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId) throw Failure("instance-changed"); }
    static CustomerContext[] Snapshot(IReadOnlyList<CustomerContext> contexts)
    {
        try
        {
            if (contexts is null) throw Failure("contexts-invalid");
            var count = contexts.Count;
            if (count is < 1 or > CredentialRestrictedRecoveryStaging.MaximumProfiles) throw Failure("contexts-invalid");
            var copy = new CustomerContext[count];
            for (var i = 0; i < count; i++) copy[i] = contexts[i] ?? throw Failure("contexts-invalid");
            if (contexts.Count != count) throw Failure("contexts-invalid");
            return copy;
        }
        catch (CredentialException) { throw; }
        catch { throw Failure("contexts-invalid"); }
    }
    static CredentialException Failure(string reason) => new("credential-restricted-recovery-joint-review-" + reason);
}
