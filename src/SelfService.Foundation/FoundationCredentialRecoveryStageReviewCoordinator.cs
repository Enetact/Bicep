using System.Text.Json;

namespace SelfService.Foundation;

// Explicit composition only. No HTTP/MCP/model/worker registration selects this coordinator.
public sealed class FoundationCredentialRecoveryStageReviewCoordinator
{
    readonly FoundationStore store;
    readonly CredentialRecoveryStageVerifier verifier;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? ReviewFault { get; set; }

    public FoundationCredentialRecoveryStageReviewCoordinator(FoundationStore store, CredentialRecoveryStageVerifier verifier,
        string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(verifier);
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId)
            throw Failure("instance-binding-invalid");
        this.store = store; this.verifier = verifier; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    // Preparation reads only completed metadata and writes scoped review/audit rows; it never resolves a destination key.
    public Task<FoundationCredentialRecoveryStageReview> PrepareAsync(IReadOnlyList<CustomerContext> requesters,
        CredentialRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256,
        string purpose, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(requesters);
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        deadline.Token.ThrowIfCancellationRequested(); RequireInstance(); RequirePlanBinding(plan, reviewedPlanSha256, expectedReceiptSha256);
        store.RequireCredentialRecoveryStageReviewPreparation(contexts, plan);
        using var held = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
        held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
        return Task.FromResult(store.PrepareCredentialRecoveryStageReview(contexts, plan, held.Receipt, purpose, expiresUtc));
    }

    public async Task<FoundationCredentialRecoveryStageReview> ExecuteAsync(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedDigest, CredentialRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(executors);
        CredentialRecoveryStageReviewAdmission? admission = null;
        CredentialRecoveryStageVerifier.HeldVerification? held = null;
        var admissionAttempted = false; var verified = false;
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            var review = store.GetCredentialRecoveryStageReviewForExecution(contexts, requestId);
            if (review.Digest != expectedDigest) throw Failure("review-changed");
            RequirePlanBinding(plan, reviewedPlanSha256, expectedReceiptSha256);
            if (plan.Sha256 != review.Target.PlanSha256 || expectedReceiptSha256 != review.Target.ReceiptSha256) throw Failure("target-changed");
            held = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
            var actualTarget = FoundationStore.CredentialRecoveryStageReviewTarget(plan, held.Receipt);
            if (JsonSerializer.Serialize(actualTarget, CredentialRules.Json) != JsonSerializer.Serialize(review.Target, CredentialRules.Json))
                throw Failure("target-changed");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            admissionAttempted = true;
            // This transaction durably consumes one exact admission as OutcomeUnknown before any destination-key read.
            admission = store.AdmitCredentialRecoveryStageReview(contexts, requestId, expectedDigest, actualTarget);
            ReviewFault?.Invoke("AfterAdmission");
            var result = await held.VerifyAsync(deadline.Token); verified = true;
            ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            // Current scoped contexts, all original standing grants, instance/policy and expiry are checked again here.
            return store.CompleteCredentialRecoveryStageReview(admission, contexts, result);
        }
        catch (Exception) when (admission is not null)
        {
            // Preserve the read guard through the outcome audit. The admission stays consumed; no automatic retry.
            try { return store.RecordCredentialRecoveryStageReviewUnknown(admission, cancellationToken.IsCancellationRequested ? "CallerCancelled" :
                verified ? "ReviewCompletionUnavailable" : "StageVerificationInterrupted"); }
            catch { throw Failure("outcome-unknown"); }
        }
        catch (FoundationStoreException error) when (error.Code == "RecoveryStageReviewAdmissionUncertain")
        { throw; }
        catch (Exception error)
        {
            if (admissionAttempted && error is not FoundationStoreException)
                throw new FoundationStoreException("RecoveryStageReviewAdmissionUncertain", 503);
            if (!admissionAttempted)
                try { store.RecordCredentialRecoveryStageReviewDenied(contexts, requestId, expectedDigest, "StageVerificationInterrupted"); } catch { }
            throw;
        }
        finally { held?.Dispose(); }
    }

    void RequireInstance()
    {
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId) throw Failure("instance-changed");
    }
    void RequirePlanBinding(CredentialRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256)
    {
        if (plan is null || plan.SourceNamespace.CustomerId != store.CustomerId || plan.SourceNamespace.CellId != store.CellId ||
            plan.Sha256 != reviewedPlanSha256 || !CredentialRecoverySets.DigestShape(expectedReceiptSha256)) throw Failure("stage-binding-invalid");
    }
    static CustomerContext[] Snapshot(IReadOnlyList<CustomerContext> contexts)
    {
        if (contexts is null || contexts.Count is < 1 or > CredentialRecoveryStaging.MaximumProfiles) throw Failure("contexts-invalid");
        var copy = contexts.ToArray();
        if (copy.Length != contexts.Count || copy.Length is < 1 or > CredentialRecoveryStaging.MaximumProfiles || copy.Any(context => context is null))
            throw Failure("contexts-invalid");
        return copy;
    }
    static CredentialException Failure(string reason) => new("credential-recovery-review-" + reason);
}
