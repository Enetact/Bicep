using System.Text.Json;

namespace SelfService.Foundation;

// Explicit local composition only. No HTTP/MCP/model/worker route selects this coordinator.
public sealed class FoundationCredentialRestrictedRecoveryStageReviewCoordinator
{
    readonly FoundationStore store;
    readonly CredentialRestrictedRecoveryStageVerifier verifier;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? ReviewFault { get; set; }

    public FoundationCredentialRestrictedRecoveryStageReviewCoordinator(FoundationStore store, CredentialRestrictedRecoveryStageVerifier verifier,
        string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(verifier);
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId) throw Failure("instance-binding-invalid");
        this.store = store; this.verifier = verifier; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    // Preparation is structural eligibility and durable review/audit metadata; it never resolves a credential key.
    public Task<FoundationCredentialRestrictedRecoveryStageReview> PrepareAsync(IReadOnlyList<CustomerContext> requesters,
        CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256,
        string purpose, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(requesters);
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        deadline.Token.ThrowIfCancellationRequested(); RequireInstance(); RequirePlanBinding(plan, reviewedPlanSha256, expectedReceiptSha256);
        store.RequireCredentialRestrictedRecoveryStageReviewPreparation(contexts, plan);
        using var held = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
        held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
        return Task.FromResult(store.PrepareCredentialRestrictedRecoveryStageReview(contexts, plan, held.Receipt, purpose, expiresUtc));
    }

    public async Task<FoundationCredentialRestrictedRecoveryStageReview> ExecuteAsync(IReadOnlyList<CustomerContext> executors,
        string requestId, string expectedDigest, CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, CancellationToken cancellationToken = default)
    {
        var contexts = Snapshot(executors);
        CredentialRestrictedRecoveryStageReviewAdmission? admission = null;
        CredentialRestrictedRecoveryStageVerifier.HeldVerification? held = null;
        var admissionAttempted = false; var verified = false;
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested(); RequireInstance();
            var review = store.GetCredentialRestrictedRecoveryStageReviewForExecution(contexts, requestId);
            if (review.Digest != expectedDigest) throw Failure("review-changed");
            RequirePlanBinding(plan, reviewedPlanSha256, expectedReceiptSha256);
            if (plan.Sha256 != review.Target.PlanSha256 || expectedReceiptSha256 != review.Target.ReceiptSha256) throw Failure("target-changed");
            held = verifier.OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
            var actualTarget = FoundationStore.CredentialRestrictedRecoveryStageReviewTarget(plan, held.Receipt);
            if (JsonSerializer.Serialize(actualTarget, CredentialRules.Json) != JsonSerializer.Serialize(review.Target, CredentialRules.Json))
                throw Failure("target-changed");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            admissionAttempted = true;
            // Commit a consumed OutcomeUnknown admission and audit before any destination-key lookup.
            admission = store.AdmitCredentialRestrictedRecoveryStageReview(contexts, requestId, expectedDigest, actualTarget);
            ReviewFault?.Invoke("AfterAdmission");
            var result = await held.VerifyAsync(deadline.Token); verified = true;
            ReviewFault?.Invoke("AfterVerification");
            held.AssertCurrent(); deadline.Token.ThrowIfCancellationRequested();
            // The concrete verified session remains held. Store rechecks all current human grants/expiry,
            // then its final clock-free session snapshot/token fence precedes the atomic success/audit commit.
            return store.CompleteCredentialRestrictedRecoveryStageReview(admission, contexts, result, held, deadline.Token);
        }
        catch (Exception) when (admission is not null)
        {
            // Preserve custody through the outcome audit. The one-use admission stays consumed on every failure.
            try { return store.RecordCredentialRestrictedRecoveryStageReviewUnknown(admission, cancellationToken.IsCancellationRequested ? "CallerCancelled" :
                verified ? "ReviewCompletionUnavailable" : "StageVerificationInterrupted"); }
            catch { throw Failure("outcome-unknown"); }
        }
        catch (FoundationStoreException error) when (error.Code == "RestrictedRecoveryStageReviewAdmissionUncertain") { throw; }
        catch (Exception error)
        {
            if (admissionAttempted && error is not FoundationStoreException)
                throw new FoundationStoreException("RestrictedRecoveryStageReviewAdmissionUncertain", 503);
            if (!admissionAttempted)
                try { store.RecordCredentialRestrictedRecoveryStageReviewDenied(contexts, requestId, expectedDigest, "StageVerificationInterrupted"); } catch { }
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Restricted stage review canceled.", cancellationToken);
            if (error is OperationCanceledException && deadline.IsCancellationRequested) throw Failure("timeout");
            if (error is CredentialException or FoundationStoreException) throw;
            throw Failure("unavailable");
        }
        finally { held?.Dispose(); }
    }

    void RequireInstance()
    {
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId) throw Failure("instance-changed");
    }
    void RequirePlanBinding(CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256)
    {
        if (plan is null || plan.AccessProfile != "FoundationBoundDispatchOnly" || plan.SourceNamespace.CustomerId != store.CustomerId ||
            plan.SourceNamespace.CellId != store.CellId || plan.Sha256 != reviewedPlanSha256 || !CredentialRecoverySets.DigestShape(expectedReceiptSha256))
            throw Failure("stage-binding-invalid");
    }
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
    static CredentialException Failure(string reason) => new("credential-restricted-recovery-review-" + reason);
}
