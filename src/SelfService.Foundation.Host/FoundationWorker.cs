using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;

namespace SelfService.Foundation.Host;

public sealed class FoundationJobRunner(FoundationStore store, PipelineDispatcher dispatcher, EntitlementVerifier? entitlements = null,
    FoundationCredentialBoundPipelineConsumer? boundConsumer = null, FoundationRuntimeActivation? runtimeActivation = null)
{
    readonly EntitlementVerifier verifier = entitlements ?? new([]);
    // The worker runs inside a trusted dedicated cell. Claim/lease/fence commands have no HTTP endpoints.
    public async Task<bool> RunOnceAsync(string workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hostUse = runtimeActivation?.RetainUse();
        using var run = boundConsumer?.OpenRun(store);
        if (run is null && store.GetCredentialRuntimeBindingStatus().BindingRequired)
            throw new FoundationStoreException("BoundPipelineConsumerRequired", 403);
        cancellationToken.ThrowIfCancellationRequested();
        var job = store.ClaimNext(store.CustomerId, workerId, TimeSpan.FromMinutes(2));
        if (job is null) return false;
        FoundationDispatch dispatch;
        try { dispatch = store.StartMockDispatch(store.CustomerId, job.Id, workerId, job.Fence, verifier, FoundationWorkflowService.RuntimeVersion); }
        catch (FoundationStoreException) { return true; } // Authority changes are durably recorded as Blocked.
        try
        {
            var connection = FoundationApplication.LoadConnection(dispatch.Connection, store.CellId);
            var request = JsonSerializer.Deserialize<PipelineDispatchRequest>(dispatch.Plan.PayloadJson, FoundationApplication.Json)!;
            if (dispatch.Plan.Operation != "pipeline.dispatch" || !dispatch.Plan.ChangesExternalState ||
                request.SourceRevision != dispatch.Plan.SourceRevision || request.Environment != dispatch.Plan.EnvironmentId)
                throw new PipelineContractException("pipeline-plan-binding-invalid");
            var approval = new PipelineApproval(store.CustomerId, store.CellId, connection.Id, connection.Generation,
                PipelineRequestFactory.ConnectionDigest(connection), PipelineRequestFactory.RequestDigest(connection, request),
                dispatch.Plan.Digest, dispatch.Plan.ExpiresAt);
            using var boundTransport = run?.CreateTransport(dispatch, connection, workerId, job.Fence, verifier, FoundationWorkflowService.RuntimeVersion);
            var selected = boundTransport is null ? dispatcher : new PipelineDispatcher(boundTransport, boundConsumer!.Clock);
            var result = await selected.DispatchAsync(connection, request, approval, cancellationToken);
            run?.AssertCurrent();
            if (result.State == "Accepted")
                store.CompleteJob(store.CustomerId, job.Id, workerId, job.Fence, JsonSerializer.Serialize(new
                { result.Mock, dispatchAccepted = true, deploymentVerified = false, result.ExternalRunId, result.Url, result.ReasonCode }, FoundationApplication.Json));
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

internal sealed class FoundationWorker(FoundationJobRunner runner, FoundationHostOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = "worker-" + Guid.NewGuid().ToString("N");
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunOnceAsync(workerId, stoppingToken);
                consecutiveFailures = 0;
            }
            catch (Exception e) when (e is SqliteException or FoundationStoreException or CredentialException)
            {
                // No POST replay: undispatched intent stays in SQL; dispatched work expires to OutcomeUnknown.
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
            }
            await Task.Delay(consecutiveFailures == 0 ? options.PollInterval :
                TimeSpan.FromSeconds(Math.Min(30, 1 << consecutiveFailures)), stoppingToken);
        }
    }
}
