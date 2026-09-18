using Azure.Storage.Queues;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BlobTransfer;
public sealed record QueueClients(QueueClient Work, QueueClient Poison, QueueClient DispatchPoison);

public sealed class QueueFunctions(TransferEngine engine, StorageClients storage, BlobLedger ledger,
    QueueClients queues, PipelineOptions options, ILogger<QueueFunctions> log)
{
    [Function("CopyUploadedBlob")]
    public Task CopyAsync(
        [QueueTrigger("%TransferQueue%", Connection = "TransferQueueStorage")] string message,
        CancellationToken ct) =>
        engine.ProcessAsync(TransferPolicy.ParseMessage(message), ct);

    [Function("DispatchUploadedBlob")]
    public async Task DispatchAsync(
        [BlobTrigger("%UploadContainer%/{name}", Connection = "UploadStorage", Source = BlobTriggerSource.LogsAndContainerScan)] BlobClient source,
        string name, CancellationToken ct)
    {
        var properties = (await source.GetPropertiesAsync(cancellationToken: ct)).Value;
        var work = new WorkItem(1, name, properties.ETag.ToString(), properties.VersionId);
        var key = TransferPolicy.Key(work, storage.Source.Uri, options);
        await queues.Work.SendMessageAsync(BinaryData.FromObjectAsJson(work),
            timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: ct);
        log.LogInformation("BlobDispatched request={Request}", key.RequestId);
    }

    [Function("ReconcileTransfers")]
    public async Task ReconcileAsync([TimerTrigger("%RecoverySchedule%", UseMonitor = true)] TimerInfo timer, CancellationToken ct)
    {
        // Persisted continuation prevents repeatedly scanning just the first page.
        await using var scan = await ledger.LockAsync("system/reconcile-cursor.json", ct);
        var cursor = await scan.ReadAsync<ScanCursor>();
        int pages = 0, scheduled = 0;
        await foreach (var page in storage.Source.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None,
            options.IncludeSourceVersions ? BlobStates.Version : BlobStates.None, prefix: null, cancellationToken: scan.Token)
            .AsPages(cursor.Continuation, options.ScanPageSize))
        {
            foreach (var item in page.Values)
            {
                TransferKey key;
                var work = new WorkItem(1, item.Name, item.Properties.ETag!.Value.ToString(), item.VersionId);
                try { key = TransferPolicy.Key(work, storage.Source.Uri, options); }
                catch (PermanentTransferException)
                { log.LogError("InvalidUploadContractDetected object={Object}", TransferPolicy.HashText(item.Name)); continue; }
                var record = await ledger.ReadAsync(key.LedgerName, scan.Token);
                if (record?.Status == "Quarantined")
                { log.LogError("TransferNeedsReview request={Request} code={Code}", key.RequestId, record.ErrorCode); continue; }
                if (TransferPolicy.IsDue(record, DateTimeOffset.UtcNow, options.VerifyAfterHours))
                {
                    await queues.Work.SendMessageAsync(BinaryData.FromObjectAsJson(
                        work),
                        timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: scan.Token);
                    scheduled++;
                }
            }
            cursor.Continuation = page.ContinuationToken;
            cursor.LastScanUtc = DateTimeOffset.UtcNow;
            await scan.SaveAsync(cursor); // Advance only after all messages in this page were accepted.
            if (++pages >= options.ScanPagesPerRun) break;
        }
        log.LogInformation("ReconcileHeartbeat scheduled={Scheduled} pages={Pages}", scheduled, pages);
    }

    [Function("MonitorTransferPoison")]
    public async Task MonitorAsync([TimerTrigger("%PoisonMonitorSchedule%", UseMonitor = true)] TimerInfo timer, CancellationToken ct)
    {
        // Observe only: never remove poison messages or automatically reset a quarantined request.
        var properties = await queues.Poison.GetPropertiesAsync(ct);
        if (properties.Value.ApproximateMessagesCount > 0)
            log.LogError("PoisonBacklog count={Count}", properties.Value.ApproximateMessagesCount);
        var dispatchPoison = await queues.DispatchPoison.GetPropertiesAsync(ct);
        if (dispatchPoison.Value.ApproximateMessagesCount > 0)
            log.LogError("PoisonBacklog dispatcherCount={Count}", dispatchPoison.Value.ApproximateMessagesCount);
        log.LogInformation("PoisonMonitorHeartbeat");
    }

    [Function("AuditTransferLedger")]
    public async Task AuditAsync([TimerTrigger("%RecoverySchedule%", UseMonitor = true)] TimerInfo timer, CancellationToken ct)
    {
        await using var scan = await ledger.LockAsync("system/ledger-cursor.json", ct);
        var cursor = await scan.ReadAsync<ScanCursor>();
        int pages = 0;
        await foreach (var page in ledger.Container.GetBlobsAsync(
            Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None,
            prefix: "requests/", cancellationToken: scan.Token).AsPages(cursor.Continuation, options.ScanPageSize))
        {
            foreach (var item in page.Values)
            {
                var record = await ledger.ReadAsync(item.Name, scan.Token);
                if (record is null || record.SourceName.Length == 0) continue;
                var work = new WorkItem(1, record.SourceName, record.SourceETag, record.SourceVersionId);
                var key = TransferPolicy.Key(work, storage.Source.Uri, options);
                if (record.Status == "Quarantined")
                    log.LogError("TransferNeedsReview request={Request} code={Code}", key.RequestId, record.ErrorCode);
                var source = storage.Source.GetBlobClient(record.SourceName);
                if (!string.IsNullOrEmpty(record.SourceVersionId)) source = source.WithVersion(record.SourceVersionId);
                if (!(await source.ExistsAsync(scan.Token)).Value)
                    log.LogError("SourceMissing request={Request} status={Status}", key.RequestId, record.Status);
            }
            cursor.Continuation = page.ContinuationToken;
            cursor.LastScanUtc = DateTimeOffset.UtcNow;
            await scan.SaveAsync(cursor);
            if (++pages >= options.ScanPagesPerRun) break;
        }
        log.LogInformation("LedgerAuditHeartbeat pages={Pages}", pages);
    }
}
public sealed class ScanCursor
{
    public string? Continuation { get; set; }
    public DateTimeOffset LastScanUtc { get; set; }
}
