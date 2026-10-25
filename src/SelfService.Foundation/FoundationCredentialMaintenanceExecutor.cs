namespace SelfService.Foundation;

// Explicit trusted composition only; no host, HTTP, MCP, model or worker registration selects this executor.
public sealed class FoundationCredentialMaintenanceExecutor
{
    readonly FoundationStore store;
    readonly CredentialVault vault;
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly string databaseInstanceId;
    readonly TimeSpan timeout;
    internal Action<string>? MaintenanceFault { get; set; }

    public FoundationCredentialMaintenanceExecutor(FoundationStore store, CredentialVault vault,
        CredentialVaultNamespaceIdentity expectedNamespace, string expectedDatabaseInstanceId, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(expectedNamespace);
        if (expectedNamespace.CustomerId != store.CustomerId || expectedNamespace.CellId != store.CellId ||
            vault.DescribeNamespace() != expectedNamespace || store.GetCellInstanceIdentity().DatabaseInstanceId != expectedDatabaseInstanceId)
            throw new CredentialException("credential-maintenance-executor-binding-invalid");
        this.store = store; this.vault = vault; this.expectedNamespace = expectedNamespace; databaseInstanceId = expectedDatabaseInstanceId;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
    }

    public async Task<FoundationCredentialMaintenanceIntent> ExecuteAsync(CustomerContext executor, string intentId,
        string expectedDigest, CancellationToken cancellationToken = default)
    {
        CredentialMaintenanceAdmission? admission = null;
        var mutationCompleted = false;
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (store.GetCellInstanceIdentity().DatabaseInstanceId != databaseInstanceId)
                throw new CredentialException("credential-maintenance-instance-changed");
            var intent = store.GetCredentialMaintenanceForExecution(executor, intentId);
            if (intent.Digest != expectedDigest) throw new CredentialException("credential-maintenance-intent-changed");
            using var prepared = await vault.PrepareMaintenanceAsync(intent.Operation, intent.Target, expectedNamespace, deadline.Token);
            await prepared.RevalidateAsync(deadline.Token); deadline.Token.ThrowIfCancellationRequested();
            admission = store.AdmitCredentialMaintenance(executor, intentId, expectedDigest, prepared.Target, prepared.ExpectedAfterSha256);
            MaintenanceFault?.Invoke("AfterAdmission");
            var result = await prepared.ApplyAsync(admission, deadline.Token);
            mutationCompleted = true;
            MaintenanceFault?.Invoke("AfterMutation");
            return store.CompleteCredentialMaintenance(admission, result);
        }
        catch (Exception error) when (admission is not null)
        {
            // The consumed durable admission is already OutcomeUnknown. Never retry or assert no effect.
            try { return store.RecordCredentialMaintenanceUnknown(admission, error is OperationCanceledException ? "CallerCancelled" :
                mutationCompleted ? "CompletionAuditUnavailable" : "VaultCommitInterrupted"); }
            catch { throw new CredentialException("credential-maintenance-outcome-unknown"); }
        }
        catch (Exception)
        {
            // Audit only the already scoped intent/digest; denial cannot create mutation authority.
            try { store.RecordCredentialMaintenanceDenied(executor, intentId, expectedDigest, "PreparationRejected"); } catch { }
            throw;
        }
    }
}
