using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System.Text.Json;

namespace BlobTransfer;

// The atomic create and renewable blob lease serialize all workers for this key.
// Every ledger write is lease-conditioned; failed renewal cancels processing.
public sealed class BlobLedger(BlobContainerClient container)
{
    public BlobContainerClient Container => container;
    public async Task<TransferRecord?> ReadAsync(string name, CancellationToken ct)
    {
        try
        {
            var download = await container.GetBlobClient(name).DownloadContentAsync(ct);
            return download.Value.Content.ToObjectFromJson<TransferRecord>();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }
    public Task<LedgerLease> LockAsync(string name, CancellationToken ct) =>
        LedgerLease.AcquireAsync(container.GetBlobClient(name), ct);
}

public sealed class LedgerLease : IAsyncDisposable
{
    private readonly BlobClient blob;
    private readonly BlobLeaseClient lease;
    private readonly CancellationTokenSource lifetime;
    private readonly Task renewal;
    public CancellationToken Token => lifetime.Token;
    public string LeaseId => lease.LeaseId;
    public async Task<string> CurrentETagAsync() =>
        (await blob.GetPropertiesAsync(new BlobRequestConditions { LeaseId = lease.LeaseId }, Token)).Value.ETag.ToString();

    private LedgerLease(BlobClient blob, BlobLeaseClient lease, CancellationToken ct)
    {
        this.blob = blob;
        this.lease = lease;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        renewal = RenewAsync();
    }
    public static async Task<LedgerLease> AcquireAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(new TransferRecord()), new BlobUploadOptions
            { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { }
        var client = blob.GetBlobLeaseClient();
        try { await client.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct); }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { throw new LeaseBusyException(); }
        return new(blob, client, ct);
    }
    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token);
                await lease.RenewAsync(cancellationToken: lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { await lifetime.CancelAsync(); }
    }
    public async Task<T> ReadAsync<T>() where T : new()
    {
        var data = await blob.DownloadContentAsync(new BlobDownloadOptions
        { Conditions = new BlobRequestConditions { LeaseId = lease.LeaseId } }, Token);
        return data.Value.Content.ToObjectFromJson<T>() ?? new T();
    }
    public async Task SaveAsync<T>(T value)
    {
        Token.ThrowIfCancellationRequested();
        await blob.UploadAsync(BinaryData.FromObjectAsJson(value), new BlobUploadOptions
        { Conditions = new BlobRequestConditions { LeaseId = lease.LeaseId } }, Token);
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await renewal;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await lease.ReleaseAsync(cancellationToken: timeout.Token); }
        catch (RequestFailedException) { /* Lease expiry releases the lock if release failed. */ }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
