using System.Text.Json;

namespace SelfService.Foundation;

// Explicit local composition. The human approval covers the exact inactive stage. The current
// configured checkpoint narrows execution; it was not necessarily current when approval occurred.
public sealed class FoundationCredentialScopedRecoveryJointReviewCoordinator
{
    readonly FoundationStore store;
    readonly CredentialScopedRecoverySetCheckpointAuthority checkpointAuthority;
    readonly CredentialScopedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity;
    readonly CredentialScopedRecoveryStageVerifier verifier;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? ReviewFault { get; set; }

    public FoundationCredentialScopedRecoveryJointReviewCoordinator(FoundationStore store,
        CredentialScopedRecoverySetCheckpointAuthority configuredCheckpointAuthority,
        CredentialScopedRecoverySetCheckpointAuthorityIdentity expectedCheckpointIdentity,
        CredentialScopedRecoveryStageVerifier verifier, string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(configuredCheckpointAuthority);
        ArgumentNullException.ThrowIfNull(expectedCheckpointIdentity); ArgumentNullException.ThrowIfNull(verifier);
        if (expectedCheckpointIdentity.CustomerId != store.CustomerId || expectedCheckpointIdentity.CellId != store.CellId ||
            expectedCheckpointIdentity.SourceNamespace.CustomerId != store.CustomerId || expectedCheckpointIdentity.SourceNamespace.CellId != store.CellId ||
            configuredCheckpointAuthority.DescribeIdentity() != expectedCheckpointIdentity) throw Failure("checkpoint-identity-mismatch");
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId) throw Failure("instance-binding-invalid");
        this.store = store; checkpointAuthority = configuredCheckpointAuthority; this.expectedCheckpointIdentity = expectedCheckpointIdentity;
        this.verifier = verifier; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    public async Task<FoundationCredentialScopedRecoveryJointVerification> ExecuteApprovedAsync(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedReviewDigest, CredentialScopedRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, long expectedCheckpointSequence, string expectedCheckpointSha256,
        CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(executors);
        CredentialScopedRecoveryJointAdmission? admission = null;
        CredentialScopedRecoveryJointSession? held = null;
        var admissionAttempted = false; var verified = false;
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            // All current scopes/human authority are checked before private checkpoint or stage access.
            var review = store.GetCredentialScopedRecoveryJointReviewForExecution(contexts, requestId);
            if (review.Digest != expectedReviewDigest) throw Failure("review-changed");
            if (plan is null || plan.SourceNamespace.CustomerId != store.CustomerId || plan.SourceNamespace.CellId != store.CellId ||
                plan.Sha256 != reviewedPlanSha256 || plan.Sha256 != review.Target.PlanSha256 ||
                !CredentialRecoverySets.DigestShape(expectedReceiptSha256) || expectedReceiptSha256 != review.Target.ReceiptSha256)
                throw Failure("stage-binding-invalid");
            held = CredentialScopedRecoveryJointSession.Open(checkpointAuthority, expectedCheckpointIdentity, verifier, plan,
                reviewedPlanSha256, expectedReceiptSha256, expectedCheckpointSequence, expectedCheckpointSha256);
            var actualStage = FoundationStore.CredentialScopedRecoveryStageReviewTarget(plan, held.Receipt);
            if (JsonSerializer.Serialize(actualStage, CredentialRules.Json) != JsonSerializer.Serialize(review.Target, CredentialRules.Json))
                throw Failure("target-changed");
            var target = FoundationStore.CredentialScopedRecoveryJointTarget(plan, held.Receipt, held.Identity, held.Checkpoint);
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            admissionAttempted = true;
            // One transaction consumes both the ordinary review and its checkpoint-bound join before keys.
            admission = store.AdmitCredentialScopedRecoveryJointVerification(contexts, requestId, expectedReviewDigest, target);
            ReviewFault?.Invoke("AfterAdmission");
            var result = await held.VerifyAsync(deadline.Token); verified = true;
            ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            return store.CompleteCredentialScopedRecoveryJointVerification(admission, contexts, target, result);
        }
        catch (Exception) when (admission is not null)
        {
            // Both concrete guards remain retained through the terminal audit attempt. Unknown is consumed.
            try { return store.RecordCredentialScopedRecoveryJointUnknown(admission, cancellationToken.IsCancellationRequested ? "CallerCancelled" :
                verified ? "ReviewCompletionUnavailable" : "StageVerificationInterrupted"); }
            catch { throw Failure("outcome-unknown"); }
        }
        catch (FoundationStoreException error) when (error.Code == "ScopedRecoveryJointAdmissionUncertain") { throw; }
        catch (Exception error)
        {
            if (admissionAttempted && error is not FoundationStoreException)
                throw new FoundationStoreException("ScopedRecoveryJointAdmissionUncertain", 503);
            if (!admissionAttempted)
                try { store.RecordCredentialScopedRecoveryJointDenied(contexts, requestId, expectedReviewDigest, "StageVerificationInterrupted"); } catch { }
            throw;
        }
        finally { held?.Dispose(); }
    }

    void RequireInstance()
    { if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId) throw Failure("instance-changed"); }

    static CustomerContext[] Snapshot(IReadOnlyList<CustomerContext> contexts)
    {
        if (contexts is null || contexts.Count is < 1 or > CredentialScopedRecoveryStaging.MaximumProfiles) throw Failure("contexts-invalid");
        var copy = contexts.ToArray();
        if (copy.Length != contexts.Count || copy.Length is < 1 or > CredentialScopedRecoveryStaging.MaximumProfiles || copy.Any(context => context is null))
            throw Failure("contexts-invalid");
        return copy;
    }
    static CredentialException Failure(string reason) => new("credential-scoped-recovery-joint-review-" + reason);
}
