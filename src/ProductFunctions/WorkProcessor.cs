using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProductFunctions;

public record WorkItem(string Id, string Operation);
public record WorkReceipt(string Id, string BodySha256, string Status);
public enum StoreResult { Created, Duplicate, Conflict }
public interface IReceiptStore { Task<StoreResult> Put(WorkReceipt receipt, CancellationToken ct); }
public sealed class InvalidWorkException : Exception { public InvalidWorkException() : base("Invalid work contract.") {} }

// The sample performs only a receipt operation. Business side effects need their own idempotency contract.
public static class WorkProcessor
{
    public static WorkReceipt Parse(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) > 16384) throw new InvalidWorkException();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("operation", out var op) || op.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(id.GetString(), "D", out var guid) || guid == Guid.Empty || op.GetString() != "record") throw new InvalidWorkException();
            return new(guid.ToString("D"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), "Recorded");
        }
        catch (JsonException) { throw new InvalidWorkException(); }
    }
    public static Task<StoreResult> Process(string body, IReceiptStore store, CancellationToken ct) => store.Put(Parse(body), ct);
}
