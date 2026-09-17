using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BlobTransfer;

public sealed class CopyUploadedBlob(BlobContainerClient destination, CopyLimits limits, ILogger<CopyUploadedBlob> logger)
{
    [Function(nameof(CopyUploadedBlob))]
    public async Task RunAsync(
        [BlobTrigger("%UploadContainer%/{name}", Connection = "UploadStorage", Source = BlobTriggerSource.LogsAndContainerScan)] BlobClient source,
        string name,
        CancellationToken cancellationToken)
    {
        var properties = (await source.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        CopyPolicy.ValidateLength(properties.ContentLength, limits.MaxBytes);
        var fingerprint = CopyPolicy.Fingerprint(source.Uri, properties.ETag.ToString());
        var target = destination.GetBlobClient(name);

        // Reads fail if the source changes mid-copy; clients must use immutable, unique upload names.
        await using var input = await source.OpenReadAsync(new BlobOpenReadOptions(false)
        {
            Conditions = new BlobRequestConditions { IfMatch = properties.ETag },
            BufferSize = 4 * 1024 * 1024
        }, cancellationToken);
        try
        {
            await target.UploadAsync(input, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = new Dictionary<string, string> { ["sourcefingerprint"] = fingerprint },
                HttpHeaders = new BlobHttpHeaders { ContentType = properties.ContentType },
                TransferOptions = new StorageTransferOptions
                {
                    InitialTransferSize = 4 * 1024 * 1024,
                    MaximumTransferSize = 4 * 1024 * 1024,
                    MaximumConcurrency = 2
                }
            }, cancellationToken);
            logger.LogInformation("CopyComplete fingerprint={Fingerprint} bytes={Bytes}", fingerprint, properties.ContentLength);
        }
        catch (RequestFailedException exception) when (
            exception.ErrorCode == "BlobAlreadyExists" || exception.ErrorCode == "ConditionNotMet")
        {
            var existing = (await target.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (!CopyPolicy.IsCompletedDuplicate(existing.Metadata, fingerprint, existing.ContentLength, properties.ContentLength))
                throw new InvalidOperationException("Copy conflict: destination belongs to a different source revision; human review required.");
            logger.LogInformation("CopyDuplicate fingerprint={Fingerprint}", fingerprint);
        }
        // All other failures propagate so Functions retries and eventually writes a poison message.
        // Source is never deleted. No server-side StartCopyFromUri: the private source is read over the app VNet.
    }
}
