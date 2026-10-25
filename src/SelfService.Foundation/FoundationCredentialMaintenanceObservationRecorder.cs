namespace SelfService.Foundation;

// A reviewed current observation, not maintenance execution, historical success or rollback reconciliation.
public sealed class FoundationCredentialMaintenanceObservationRecorder
{
    readonly FoundationStore store;
    readonly CredentialVault vault;
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? ObservationFault { get; set; }

    public FoundationCredentialMaintenanceObservationRecorder(FoundationStore store, CredentialVault vault,
        CredentialVaultNamespaceIdentity expectedNamespace, string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(expectedNamespace);
        if (expectedNamespace.CustomerId != store.CustomerId || expectedNamespace.CellId != store.CellId ||
            vault.DescribeNamespace() != expectedNamespace || store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId)
            throw new CredentialException("credential-observation-recorder-binding-invalid");
        this.store = store; this.vault = vault; this.expectedNamespace = expectedNamespace; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    public async Task<FoundationCredentialMaintenanceObservationRequest> PrepareAsync(CustomerContext requester,
        string originalIntentId, string purpose, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        deadline.Token.ThrowIfCancellationRequested();
        RequireInstance();
        // This current scoped check precedes all key reads, including when original authority is historical.
        var original = store.GetCredentialMaintenanceForObservationPreparation(requester, originalIntentId);
        using var observation = await vault.HoldMaintenanceObservationAsync(original.Target.ProfileScopeJson,
            original.Execution!.ExpectedAfterSha256, expectedNamespace, deadline.Token);
        await observation.RevalidateAsync(deadline.Token); deadline.Token.ThrowIfCancellationRequested();
        ObservationFault?.Invoke("BeforePrepareRecord");
        return store.PrepareCredentialMaintenanceObservation(requester, originalIntentId, observation.Observation, purpose, expiresUtc);
    }

    public async Task<FoundationCredentialMaintenanceObservationRequest> RecordAsync(CustomerContext recorder,
        string requestId, string expectedDigest, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken); var recordAttempted = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            RequireInstance();
            var request = store.GetCredentialMaintenanceObservationForRecording(recorder, requestId);
            if (request.Digest != expectedDigest) throw new CredentialException("credential-observation-review-changed");
            using var observation = await vault.HoldMaintenanceObservationAsync(request.Observation.ProfileScopeJson,
                request.ExpectedAfterSha256, expectedNamespace, deadline.Token);
            if (observation.Observation != request.Observation) throw new CredentialException("credential-observation-review-changed");
            await observation.RevalidateAsync(deadline.Token); deadline.Token.ThrowIfCancellationRequested();
            ObservationFault?.Invoke("BeforeRecord");
            recordAttempted = true;
            return store.RecordCredentialMaintenanceObservation(recorder, requestId, expectedDigest, observation.Observation);
        }
        catch (Exception error)
        {
            if (recordAttempted && error is not FoundationStoreException)
                throw new FoundationStoreException("MaintenanceObservationRecordUncertain", 503);
            if (error is not FoundationStoreException { Code: "MaintenanceObservationRecordUncertain" })
                try { store.RecordCredentialMaintenanceObservationDenied(recorder, requestId, expectedDigest, "ObservationRejected"); } catch { }
            throw;
        }
    }

    void RequireInstance()
    {
        if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId)
            throw new CredentialException("credential-observation-instance-changed");
    }
}
