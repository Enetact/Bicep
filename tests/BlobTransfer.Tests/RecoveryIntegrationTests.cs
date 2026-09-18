using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlobTransfer.Tests;
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BLOBTRANSFER_AZURITE") != "1")
            Skip = "Start isolated Azurite and set BLOBTRANSFER_AZURITE=1. Never runs against Azure.";
    }
}
public class RecoveryIntegrationTests
{
    private sealed record Harness(StorageClients Storage, BlobLedger Ledger, QueueClients Queues, PipelineOptions Options)
    {
        public TransferEngine Engine => new(Storage, Ledger, Options, NullLogger<TransferEngine>.Instance);
        public QueueFunctions Functions => new(Engine, Storage, Ledger, Queues, Options, NullLogger<QueueFunctions>.Instance);
        public TransferKey Key(WorkItem work) => TransferPolicy.Key(work, Storage.Source.Uri, Options);
        public async Task<WorkItem> Upload(string name, string text = "same content", bool dispatch = true)
        {
            // Simulates an uncontrolled external system: arbitrary filename and no custom metadata.
            var blob = Storage.Source.GetBlobClient(name);
            await blob.UploadAsync(BinaryData.FromString(text), overwrite: true);
            var props = (await blob.GetPropertiesAsync()).Value;
            if (dispatch) await Functions.DispatchAsync(blob, name, default);
            return new(1, name, props.ETag.ToString());
        }
        public Task<TransferRecord?> Record(WorkItem work) => Ledger.ReadAsync(Key(work).LedgerName, default);
    }
    private static async Task<Harness> Create(int maxAttempts = 10, int pageSize = 100)
    {
        var blobs = new BlobServiceClient("UseDevelopmentStorage=true");
        var queues = new QueueServiceClient("UseDevelopmentStorage=true", new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
        var id = Guid.NewGuid().ToString("N");
        var source = blobs.GetBlobContainerClient("source-" + id);
        var target = blobs.GetBlobContainerClient("target-" + id);
        var ledger = blobs.GetBlobContainerClient("ledger-" + id);
        await source.CreateAsync(); await target.CreateAsync(); await ledger.CreateAsync();
        var work = queues.GetQueueClient("work-" + id);
        var poison = queues.GetQueueClient("work-" + id + "-poison");
        var dispatchPoison = queues.GetQueueClient("dispatch-" + id);
        await work.CreateAsync(); await poison.CreateAsync(); await dispatchPoison.CreateAsync();
        return new(new(source, target), new(ledger), new(work, poison, dispatchPoison),
            new(new() { ["other/"] = "other", [""] = "default" }, MaxAttempts: maxAttempts,
                ScanPageSize: pageSize, ScanPagesPerRun: 1, IncludeSourceVersions: false));
    }
    [AzuriteFact]
    public async Task PlainExternalUploadAndDuplicateDispatchNeedNoUploaderChanges()
    {
        var h = await Create(); var work = await h.Upload("incoming from ERP/report 2026.pdf");
        var blob = h.Storage.Source.GetBlobClient(work.SourceName);
        Assert.Empty((await blob.GetPropertiesAsync()).Value.Metadata);
        await h.Functions.DispatchAsync(blob, work.SourceName, default);
        var messages = (await h.Queues.Work.ReceiveMessagesAsync(10)).Value;
        Assert.Equal(2, messages.Length);
        foreach (var message in messages) await h.Functions.CopyAsync(message.MessageText, default);
        Assert.Equal("Completed", (await h.Record(work))!.Status);
        Assert.Equal(work.SourceETag, (await blob.GetPropertiesAsync()).Value.ETag.ToString());
        Assert.Equal(1, (await h.Record(work))!.Attempts);
    }
    [AzuriteFact]
    public async Task DifferentFilenamesWithSameBytesShareDestinationWithinScopeOnly()
    {
        var h = await Create();
        var a = await h.Upload("report.pdf"); var b = await h.Upload("report-copy.pdf"); var c = await h.Upload("other/report.pdf");
        foreach (var work in new[] { a, b, c }) await h.Engine.ProcessAsync(work, default);
        Assert.Equal((await h.Record(a))!.DestinationName, (await h.Record(b))!.DestinationName);
        Assert.NotEqual((await h.Record(a))!.DestinationName, (await h.Record(c))!.DestinationName);
        int count = 0; await foreach (var blob in h.Storage.Destination.GetBlobsAsync()) count++;
        Assert.Equal(2, count);
    }
    [AzuriteFact]
    public async Task ConcurrentWorkersConvergeOnSingleCommittedDestination()
    {
        var h = await Create();
        var jobs = new[] { await h.Upload("one.txt"), await h.Upload("two.txt") };
        await Task.WhenAll(jobs.Select(async job =>
        {
            for (var i = 0; ; i++)
            {
                try { await h.Engine.ProcessAsync(job, default); break; }
                catch (LeaseBusyException) when (i < 5) { await Task.Delay(100); }
            }
        }));
        int count = 0; await foreach (var blob in h.Storage.Destination.GetBlobsAsync()) count++;
        Assert.Equal(1, count);
    }
    [AzuriteFact]
    public async Task CopyBeforeLedgerCommitIsRecoveredWithoutOverwrite()
    {
        var h = await Create(); var work = await h.Upload("invoice.pdf"); var key = h.Key(work);
        await h.Engine.ProcessAsync(work, default);
        var record = (await h.Record(work))!;
        var dest = h.Storage.Destination.GetBlobClient(record.DestinationName);
        var etag = (await dest.GetPropertiesAsync()).Value.ETag;
        await using (var lease = await h.Ledger.LockAsync(key.LedgerName, default))
        { record.Status = "Processing"; record.VerifiedUtc = null; await lease.SaveAsync(record); }
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal(etag, (await dest.GetPropertiesAsync()).Value.ETag);
        Assert.Equal("Completed", (await h.Record(work))!.Status);
    }
    [AzuriteFact]
    public async Task MissingDestinationIsRecreatedAndCorruptionIsQuarantined()
    {
        var h = await Create(); var work = await h.Upload("file.dat");
        await h.Engine.ProcessAsync(work, default);
        var dest = h.Storage.Destination.GetBlobClient((await h.Record(work))!.DestinationName);
        await dest.DeleteAsync();
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal("same content", (await dest.DownloadContentAsync()).Value.Content.ToString());
        await dest.UploadAsync(BinaryData.FromString("evil content"), overwrite: true);
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal("Quarantined", (await h.Record(work))!.Status);
        Assert.Equal("evil content", (await dest.DownloadContentAsync()).Value.Content.ToString());
    }
    [AzuriteFact]
    public async Task UntrustedHashMetadataIsIgnoredAndActualBytesDetermineDestination()
    {
        var h = await Create(); var work = await h.Upload("file.dat", dispatch: false);
        var blob = h.Storage.Source.GetBlobClient(work.SourceName);
        await blob.SetMetadataAsync(new Dictionary<string, string> { ["sha256"] = new string('0', 64), ["scopeid"] = "other" });
        work = work with { SourceETag = (await blob.GetPropertiesAsync()).Value.ETag.ToString() };
        await h.Engine.ProcessAsync(work, default);
        var record = (await h.Record(work))!;
        Assert.Equal(TransferPolicy.HashText("same content"), record.Sha256);
        Assert.StartsWith("v1/default/", record.DestinationName);
    }
    [AzuriteFact]
    public async Task OverwriteGetsNewRequestButIdenticalBytesStillDeduplicate()
    {
        var h = await Create(); var first = await h.Upload("same-name.txt");
        await h.Engine.ProcessAsync(first, default);
        var second = await h.Upload("same-name.txt");
        Assert.NotEqual(h.Key(first), h.Key(second));
        await h.Engine.ProcessAsync(second, default);
        Assert.Equal((await h.Record(first))!.DestinationName, (await h.Record(second))!.DestinationName);
    }
    [AzuriteFact]
    public async Task StaleUnversionedMessageNeverCopiesReplacementBytes()
    {
        var h = await Create(); var first = await h.Upload("same-name.txt");
        var second = await h.Upload("same-name.txt", "changed");
        await h.Engine.ProcessAsync(first, default);
        Assert.Equal("SourceRevisionUnavailable", (await h.Record(first))!.ErrorCode);
        await h.Engine.ProcessAsync(second, default);
        Assert.Equal("Completed", (await h.Record(second))!.Status);
    }
    [AzuriteFact]
    public async Task MissedDispatcherIsRecoveredWithPersistentPagination()
    {
        var h = await Create(pageSize: 1);
        for (var i = 0; i < 3; i++) await h.Upload($"external/report-{i}.txt", $"file {i}", dispatch: false);
        Assert.Equal(0, (await h.Queues.Work.GetPropertiesAsync()).Value.ApproximateMessagesCount);
        for (var i = 0; i < 3; i++) await h.Functions.ReconcileAsync(null!, default);
        var messages = (await h.Queues.Work.ReceiveMessagesAsync(10)).Value;
        Assert.Equal(3, messages.Length);
        foreach (var message in messages) await h.Functions.CopyAsync(message.MessageText, default);
        int count = 0; await foreach (var blob in h.Storage.Destination.GetBlobsAsync()) count++;
        Assert.Equal(3, count);
    }
    [AzuriteFact]
    public async Task RetryBudgetAndReviewedRecoveryAreEnforced()
    {
        var h = await Create(maxAttempts: 2); var work = await h.Upload("file.txt"); var key = h.Key(work);
        await h.Storage.Destination.DeleteAsync();
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<RequestFailedException>(() => h.Engine.ProcessAsync(work, default));
        Assert.Equal("Quarantined", (await h.Record(work))!.Status);
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal(2, (await h.Record(work))!.Attempts);
        var recovery = new TransferRecovery(h.Ledger, h.Queues.Work);
        await Assert.ThrowsAsync<PermanentTransferException>(() => recovery.ResumeAsync(key, "\"stale\"", work.SourceETag, "INC-test", "test operator", default));
        await h.Storage.Destination.CreateAsync();
        var etag = (await h.Ledger.Container.GetBlobClient(key.LedgerName).GetPropertiesAsync()).Value.ETag.ToString();
        await recovery.ResumeAsync(key, etag, work.SourceETag, "INC-test", "test operator", default);
        await h.Engine.ProcessAsync(work, default);
        var completed = (await h.Record(work))!;
        Assert.Equal("Completed", completed.Status); Assert.NotEmpty(completed.RecoveryReference);
        int audits = 0; await foreach (var blob in h.Ledger.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"audit/{key.Scope}/{key.RequestId}/", default)) audits++;
        Assert.Equal(1, audits);
    }
    [AzuriteFact]
    public async Task LedgerLeaseExcludesOtherWorkersUntilReleased()
    {
        var h = await Create(); var key = h.Key(await h.Upload("file"));
        await using (var lease = await h.Ledger.LockAsync(key.LedgerName, default))
            await Assert.ThrowsAsync<LeaseBusyException>(() => h.Ledger.LockAsync(key.LedgerName, default));
        await using var next = await h.Ledger.LockAsync(key.LedgerName, default);
        await next.SaveAsync(new TransferRecord { Status = "Completed" });
    }
    [AzuriteFact]
    public async Task BothPoisonQueuesAreMonitoredWithoutConsumingMessages()
    {
        var h = await Create(); await h.Queues.Poison.SendMessageAsync("invalid");
        await h.Queues.DispatchPoison.SendMessageAsync("invalid");
        var log = new CaptureLogger<QueueFunctions>();
        await new QueueFunctions(h.Engine, h.Storage, h.Ledger, h.Queues, h.Options, log).MonitorAsync(null!, default);
        Assert.Equal(2, log.Messages.Count(value => value.Contains("PoisonBacklog")));
        Assert.Single((await h.Queues.Poison.PeekMessagesAsync()).Value);
        Assert.Single((await h.Queues.DispatchPoison.PeekMessagesAsync()).Value);
    }
    [AzuriteFact]
    public async Task MissingSourceIsPersistedForReview()
    {
        var h = await Create(); var work = await h.Upload("file");
        await h.Storage.Source.GetBlobClient(work.SourceName).DeleteAsync();
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal("Quarantined", (await h.Record(work))!.Status);
        Assert.Equal("SourceRevisionUnavailable", (await h.Record(work))!.ErrorCode);
    }
    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
    [AzuriteFact]
    public async Task OversizeSourceIsQuarantinedWithoutDestinationWrite()
    {
        var original = await Create();
        var h = original with { Options = original.Options with { MaxBytes = 3 } };
        var work = await h.Upload("large.txt");
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal("InvalidContentLength", (await h.Record(work))!.ErrorCode);
        int count = 0; await foreach (var blob in h.Storage.Destination.GetBlobsAsync()) count++;
        Assert.Equal(0, count);
    }
    [AzuriteFact]
    public async Task PageBlobIsQuarantinedInsteadOfTreatingAnOngoingObjectAsAFile()
    {
        var h = await Create();
        var page = h.Storage.Source.GetPageBlobClient("page.bin");
        await page.CreateAsync(512);
        var work = new WorkItem(1, "page.bin", (await page.GetPropertiesAsync()).Value.ETag.ToString());
        await h.Engine.ProcessAsync(work, default);
        Assert.Equal("UnsupportedBlobType", (await h.Record(work))!.ErrorCode);
    }
}
