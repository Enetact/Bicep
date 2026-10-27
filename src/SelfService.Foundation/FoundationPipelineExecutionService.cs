using System.Text.Json;

namespace SelfService.Foundation;

// Trusted, cell-local application service shared by API and independent worker hosts.
// Claims, authority checks, held credential custody and durable outcomes stay together.
// No HTTP/browser host dependency, remote permission boolean or automatic POST replay.
public sealed class FoundationPipelineExecutionService(FoundationStore store, PipelineDispatcher dispatcher, EntitlementVerifier? entitlements = null,
    FoundationCredentialBoundPipelineConsumer? boundConsumer = null, FoundationExecutionActivation? runtimeActivation = null)
{
    readonly EntitlementVerifier verifier = entitlements ?? new([]);

    public async Task<bool> RunOnceAsync(string workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hostUse = runtimeActivation?.RetainUse();
        using var run = boundConsumer?.OpenRun(store);
        if (run is null && store.GetCredentialRuntimeBindingStatus().BindingRequired)
            throw new FoundationStoreException("BoundPipelineConsumerRequired", 403);
        cancellationToken.ThrowIfCancellationRequested();
        var job = run is null ? store.ClaimNext(store.CustomerId, workerId, TimeSpan.FromMinutes(2)) :
            run.ClaimNext(store.CustomerId, workerId, TimeSpan.FromMinutes(2));
        if (job is null) return false;
        FoundationDispatch dispatch;
        try
        {
            dispatch = run is null ? store.StartMockDispatch(store.CustomerId, job.Id, workerId, job.Fence, verifier, FoundationExecutionRuntime.RuntimeVersion) :
                run.StartMockDispatch(store.CustomerId, job.Id, workerId, job.Fence, verifier, FoundationExecutionRuntime.RuntimeVersion);
        }
        catch (FoundationStoreException) { return true; } // Authority changes are durably recorded as Blocked.
        try
        {
            var connection = FoundationPipelineConnection.FromStored(dispatch.Connection, store.CellId);
            var request = JsonSerializer.Deserialize<PipelineDispatchRequest>(dispatch.Plan.PayloadJson, FoundationExecutionRuntime.Json)!;
            if (dispatch.Plan.Operation != "pipeline.dispatch" || !dispatch.Plan.ChangesExternalState ||
                request.SourceRevision != dispatch.Plan.SourceRevision || request.Environment != dispatch.Plan.EnvironmentId)
                throw new PipelineContractException("pipeline-plan-binding-invalid");
            var approval = new PipelineApproval(store.CustomerId, store.CellId, connection.Id, connection.Generation,
                PipelineRequestFactory.ConnectionDigest(connection), PipelineRequestFactory.RequestDigest(connection, request),
                dispatch.Plan.Digest, dispatch.Plan.ExpiresAt);
            using var boundTransport = run?.CreateTransport(dispatch, connection, workerId, job.Fence, verifier, FoundationExecutionRuntime.RuntimeVersion);
            var selected = boundTransport is null ? dispatcher : new PipelineDispatcher(boundTransport, boundConsumer!.Clock);
            var result = await selected.DispatchAsync(connection, request, approval, cancellationToken);
            run?.AssertCurrent();
            if (result.State == "Accepted")
                store.CompleteJob(store.CustomerId, job.Id, workerId, job.Fence, JsonSerializer.Serialize(new
                { result.Mock, dispatchAccepted = true, deploymentVerified = false, result.ExternalRunId, result.Url, result.ReasonCode }, FoundationExecutionRuntime.Json));
            else store.FailJob(store.CustomerId, job.Id, workerId, job.Fence, result.State == "OutcomeUnknown", "PipelineDispatchUnverified");
        }
        catch (Exception e) when (e is PipelineContractException or JsonException or OperationCanceledException or HttpRequestException or CredentialException)
        {
            try { store.FailJob(store.CustomerId, job.Id, workerId, job.Fence, true, "PipelineDispatchInterrupted"); }
            catch (FoundationStoreException) { /* Cancellation/reconciliation or a newer fence owns the outcome. */ }
        }
        catch (FoundationStoreException) { /* A concurrent cancellation or expired fence cannot complete this job. */ }
        return true;
    }
}
