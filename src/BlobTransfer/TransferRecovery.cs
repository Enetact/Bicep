using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using System.Security.Cryptography;

namespace BlobTransfer;
public sealed class TransferRecovery(BlobLedger ledger, QueueClient workQueue)
{
    public async Task ResumeAsync(TransferKey key, string expectedLedgerETag, string expectedSourceETag,
        string approvalReference, string actorLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approvalReference) || approvalReference.Length > 200 ||
            string.IsNullOrWhiteSpace(actorLabel) || actorLabel.Length > 200)
            throw new ArgumentException("An approval reference and operator label are required.");
        await using var lease = await ledger.LockAsync(key.LedgerName, ct);
        if (await lease.CurrentETagAsync() != expectedLedgerETag)
            throw new PermanentTransferException("RecoveryPlanStale");
        var record = await lease.ReadAsync<TransferRecord>();
        if (record.SourceETag != expectedSourceETag || string.IsNullOrEmpty(record.SourceName) ||
            record.Status is not ("Quarantined" or "Retry") ||
            record.ErrorCode is "RequestIdReusedWithDifferentRevision" or "UploadHashMismatch" or
                "InvalidUploadMetadata" or "InvalidContentLength" or "UnsupportedBlobType")
            throw new PermanentTransferException("RecoveryNotPermitted");
        // This CLI is for trusted recovery operators with Azure RBAC, not a self-service approval API.
        // Write the audit intent before changing state. Storage data-plane logs identify the actual caller.
        var recoveryId = Guid.NewGuid().ToString("N");
        await ledger.Container.GetBlobClient($"audit/{key.Scope}/{key.RequestId}/{recoveryId}.json")
            .UploadAsync(BinaryData.FromObjectAsJson(new
            {
                SchemaVersion = 1, RecoveryId = recoveryId, ApprovalReference = approvalReference,
                ActorLabel = actorLabel, ExpectedLedgerETag = expectedLedgerETag,
                SourceETag = expectedSourceETag, PreviousState = record, CreatedUtc = DateTimeOffset.UtcNow
            }), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, lease.Token);
        record.Status = "Retry";
        record.Attempts = 0;
        record.ErrorCode = "";
        record.NextAttemptUtc = DateTimeOffset.UtcNow;
        record.RecoveryReference = recoveryId;
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        await lease.SaveAsync(record);
        await workQueue.SendMessageAsync(BinaryData.FromObjectAsJson(new WorkItem(1, record.SourceName, record.SourceETag, record.SourceVersionId)),
            timeToLive: TimeSpan.FromSeconds(-1), cancellationToken: lease.Token);
    }
}
