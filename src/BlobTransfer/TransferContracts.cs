using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlobTransfer;
public sealed record PipelineOptions(
    Dictionary<string, string> ScopePrefixes, long MaxBytes = 1073741824,
    int MaxAttempts = 10, int RetryMinutes = 15, int ScanPageSize = 100,
    int ScanPagesPerRun = 5, int VerifyAfterHours = 24, bool IncludeSourceVersions = true);

public sealed record TransferKey(string Scope, string RequestId)
{
    public string LedgerName => $"requests/{Scope}/{RequestId}.json";
    public string DestinationName(string sha256) => $"v1/{Scope}/{sha256}/payload";
    public void Validate()
    {
        if (!Regex.IsMatch(Scope, "^[a-z0-9][a-z0-9-]{0,62}$") || !TransferPolicy.IsHash(RequestId))
            throw new PermanentTransferException("InvalidTransferKey");
    }
}
public sealed class PermanentTransferException(string code) : Exception(code);
public sealed class LeaseBusyException() : Exception("Ledger lease is busy; retry later.");
public sealed class TransferRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceName { get; set; } = "";
    public string SourceETag { get; set; } = "";
    public string? SourceVersionId { get; set; }
    public string Status { get; set; } = "Pending";
    public string Sha256 { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public long Length { get; set; }
    public int Attempts { get; set; }
    public string ErrorCode { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public string RecoveryReference { get; set; } = "";
}
public sealed record WorkItem(int SchemaVersion, string SourceName, string SourceETag, string? SourceVersionId = null);
public static class TransferPolicy
{
    public static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static bool IsHash(string value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$");
    public static string NormalizeETag(string value) => value.Trim('"');
    public static string ResolveScope(string sourceName, PipelineOptions options)
    {
        var match = options.ScopePrefixes.Where(pair => sourceName.StartsWith(pair.Key, StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Length).FirstOrDefault();
        return match.Value ?? throw new PermanentTransferException("UnmappedSourcePrefix");
    }
    public static void Validate(WorkItem item)
    {
        if (item.SchemaVersion != 1 || string.IsNullOrEmpty(item.SourceName) || item.SourceName.Length > 1024 ||
            string.IsNullOrWhiteSpace(item.SourceETag) || item.SourceETag.Length > 128 ||
            item.SourceName.Any(char.IsControl) || item.SourceVersionId?.Length > 128)
            throw new PermanentTransferException("InvalidWorkItem");
    }
    public static TransferKey Key(WorkItem item, Uri configuredSourceContainer, PipelineOptions options)
    {
        Validate(item);
        var scope = ResolveScope(item.SourceName, options);
        // Server-derived stable request ID; no uploader metadata or naming contract is required.
        var key = new TransferKey(scope, HashText(configuredSourceContainer.AbsoluteUri.TrimEnd('/') + "\n" +
            item.SourceName + "\n" + NormalizeETag(item.SourceETag)));
        key.Validate();
        return key;
    }
    public static bool IsDue(TransferRecord? record, DateTimeOffset now, int verifyAfterHours) =>
        record is null || (record.Status switch
        {
            "Quarantined" => false,
            "Completed" => record.VerifiedUtc is null || record.VerifiedUtc <= now.AddHours(-verifyAfterHours),
            _ => record.NextAttemptUtc is null || record.NextAttemptUtc <= now
        });
    public static WorkItem ParseMessage(string json)
    {
        var item = JsonSerializer.Deserialize<WorkItem>(json) ?? throw new PermanentTransferException("InvalidWorkItem");
        Validate(item);
        return item;
    }
}
