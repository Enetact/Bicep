using Microsoft.Data.Sqlite;
using SelfService.Foundation;

namespace SelfService.Foundation.Host;

public sealed class FoundationJobRunner(FoundationStore store, PipelineDispatcher dispatcher, EntitlementVerifier? entitlements = null,
    FoundationCredentialBoundPipelineConsumer? boundConsumer = null, FoundationRuntimeActivation? runtimeActivation = null)
{
    // Compatibility adapter; the shared application service owns execution and verification.
    readonly FoundationPipelineExecutionService execution = new(store, dispatcher, entitlements, boundConsumer, runtimeActivation?.Execution);
    public Task<bool> RunOnceAsync(string workerId, CancellationToken cancellationToken = default) =>
        execution.RunOnceAsync(workerId, cancellationToken);
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
