using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace BlobTransfer;

public sealed record StorageClients(BlobContainerClient Source, BlobContainerClient Destination);
public sealed class TransferEngine(StorageClients storage, BlobLedger ledger, PipelineOptions options, ILogger<TransferEngine> log)
{
    public async Task ProcessAsync(WorkItem work, CancellationToken ct)
    {
        var key = TransferPolicy.Key(work, storage.Source.Uri, options);
        await using var requestLease = await ledger.LockAsync(key.LedgerName, ct);
        var record = await requestLease.ReadAsync<TransferRecord>();
        var token = requestLease.Token;
        var source = storage.Source.GetBlobClient(work.SourceName);
        if (!string.IsNullOrEmpty(work.SourceVersionId)) source = source.WithVersion(work.SourceVersionId);
        // Version-addressed reads survive later overwrites; never reinterpret a stale message as new content.
        if (record.Status == "Quarantined") return;
        BlobProperties props;
        try { props = (await source.GetPropertiesAsync(cancellationToken: token)).Value; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            record.SourceName = work.SourceName;
            record.SourceETag = work.SourceETag;
            record.SourceVersionId = work.SourceVersionId;
            record.Attempts++;
            var missing = e is RequestFailedException storageError && storageError.Status == 404;
            record.Status = missing || record.Attempts >= options.MaxAttempts ? "Quarantined" : "Retry";
            record.ErrorCode = missing ? "SourceRevisionUnavailable" : (e is RequestFailedException azure ? $"Storage{azure.Status}" : e.GetType().Name);
            record.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(options.RetryMinutes);
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferSourceFailure request={Request} code={Code}", key.RequestId, record.ErrorCode);
            if (missing) return;
            throw;
        }
        if (TransferPolicy.NormalizeETag(work.SourceETag) != TransferPolicy.NormalizeETag(props.ETag.ToString()))
        {
            record.SourceName = work.SourceName;
            record.SourceETag = work.SourceETag;
            record.SourceVersionId = work.SourceVersionId;
            record.Status = "Quarantined";
            record.ErrorCode = "SourceRevisionUnavailable";
            await requestLease.SaveAsync(record);
            log.LogError("TransferQuarantined request={Request} code={Code}", key.RequestId, record.ErrorCode);
            return;
        }
        if (record.Status == "Quarantined") return;
        if (record.SourceETag.Length > 0 && record.SourceETag != props.ETag.ToString())
        {
            record.Status = "Quarantined";
            record.ErrorCode = "RequestIdReusedWithDifferentRevision";
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferQuarantined request={Request} code={Code}", key.RequestId, record.ErrorCode);
            return;
        }
        record.SourceName = work.SourceName;
        record.SourceVersionId = work.SourceVersionId;
        record.SourceETag = props.ETag.ToString();
        // Completed requests are still checked, so destination deletion can be repaired.
        try
        {
            if (record.Status == "Completed" && await VerifyAsync(record.DestinationName, record.Sha256, record.Length, token))
            {
                record.VerifiedUtc = DateTimeOffset.UtcNow;
                await requestLease.SaveAsync(record);
                return;
            }
        }
        catch (PermanentTransferException e)
        {
            record.Status = "Quarantined";
            record.ErrorCode = e.Message;
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferQuarantined request={Request} code={Code}", key.RequestId, record.ErrorCode);
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            record.Attempts++;
            record.Status = record.Attempts >= options.MaxAttempts ? "Quarantined" : "Retry";
            record.ErrorCode = e is RequestFailedException azure ? $"Storage{azure.Status}" : e.GetType().Name;
            record.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(options.RetryMinutes);
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferVerificationRetry request={Request} code={Code}", key.RequestId, record.ErrorCode);
            throw;
        }
        if (record.Attempts >= options.MaxAttempts)
        {
            record.Status = "Quarantined";
            record.ErrorCode = "AttemptBudgetExhausted";
            await requestLease.SaveAsync(record);
            log.LogError("TransferQuarantined request={Request} code={Code}", key.RequestId, record.ErrorCode);
            return;
        }
        record.Attempts++;
        record.Status = "Processing";
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        record.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(options.RetryMinutes);
        await requestLease.SaveAsync(record);
        try
        {
            if (props.BlobType != BlobType.Block)
                throw new PermanentTransferException("UnsupportedBlobType");
            if (props.ContentLength < 0 || props.ContentLength > options.MaxBytes)
                throw new PermanentTransferException("InvalidContentLength");
            var hash = await HashAsync(source, props.ETag, token);
            record.Sha256 = hash;
            record.Length = props.ContentLength;
            record.DestinationName = key.DestinationName(hash);
            await requestLease.SaveAsync(record);

            // All requests containing identical content in this scope share this atomic key.
            await using var contentLease = await ledger.LockAsync($"content/{key.Scope}/{hash}.json", token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, contentLease.Token);
            var copyToken = linked.Token;
            var target = storage.Destination.GetBlobClient(record.DestinationName);
            var exists = await target.ExistsAsync(copyToken);
            if (!exists.Value)
            {
                await using var input = await source.OpenReadAsync(new BlobOpenReadOptions(false)
                { Conditions = new BlobRequestConditions { IfMatch = props.ETag }, BufferSize = 4 * 1024 * 1024 }, copyToken);
                try
                {
                    await target.UploadAsync(input, new BlobUploadOptions
                    {
                        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                        Metadata = new Dictionary<string, string> { ["sha256"] = hash, ["scopeid"] = key.Scope },
                        HttpHeaders = new BlobHttpHeaders { ContentType = props.ContentType },
                        TransferOptions = new Azure.Storage.StorageTransferOptions
                        { InitialTransferSize = 4 * 1024 * 1024, MaximumTransferSize = 4 * 1024 * 1024, MaximumConcurrency = 2 }
                    }, copyToken);
                }
                catch (RequestFailedException e) when (e.Status is 409 or 412)
                { /* Another create won, or a retained destination exists. Verify it below. */ }
            }
            if (!await VerifyAsync(record.DestinationName, hash, props.ContentLength, copyToken))
                throw new PermanentTransferException("DestinationIntegrityConflict");
            // Commit content first, then request. A crash between them is repaired by verification on retry.
            await contentLease.SaveAsync(new
            {
                SchemaVersion = 1, Scope = key.Scope, Sha256 = hash, Length = props.ContentLength,
                DestinationName = record.DestinationName, Status = "Completed", VerifiedUtc = DateTimeOffset.UtcNow
            });
            record.Status = "Completed";
            record.ErrorCode = "";
            record.VerifiedUtc = DateTimeOffset.UtcNow;
            record.NextAttemptUtc = null;
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogInformation("TransferCompleted request={Request} hash={Hash} bytes={Bytes}", key.RequestId, hash, record.Length);
        }
        catch (PermanentTransferException e)
        {
            record.Status = "Quarantined";
            record.ErrorCode = e.Message;
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferQuarantined request={Request} code={Code}", key.RequestId, record.ErrorCode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            record.Status = record.Attempts >= options.MaxAttempts ? "Quarantined" : "Retry";
            record.ErrorCode = e is RequestFailedException azure ? $"Storage{azure.Status}" : e.GetType().Name;
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            await requestLease.SaveAsync(record);
            log.LogError("TransferRetry request={Request} code={Code}", key.RequestId, record.ErrorCode);
            throw; // QueueTrigger retry and poison handling remain active.
        }
    }
    private async Task<bool> VerifyAsync(string name, string expectedHash, long length, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name) || !TransferPolicy.IsHash(expectedHash)) return false;
        var blob = storage.Destination.GetBlobClient(name);
        BlobProperties props;
        try { props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value; }
        catch (RequestFailedException e) when (e.Status == 404) { return false; }
        if (props.ContentLength != length) throw new PermanentTransferException("DestinationIntegrityConflict");
        if (await HashAsync(blob, props.ETag, ct) != expectedHash)
            throw new PermanentTransferException("DestinationIntegrityConflict");
        return true;
    }
    private static async Task<string> HashAsync(BlobClient blob, ETag etag, CancellationToken ct)
    {
        await using var input = await blob.OpenReadAsync(new BlobOpenReadOptions(false)
        { Conditions = new BlobRequestConditions { IfMatch = etag }, BufferSize = 4 * 1024 * 1024 }, ct);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
    }
}
