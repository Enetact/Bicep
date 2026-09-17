using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using BlobTransfer;
using System.Text.Json;

if (args.Length == 0) throw new ArgumentException("Commands: status, resume, local-seed, local-smoke. See docs/operations.md.");
if (args[0] is "local-seed" or "local-smoke")
{
    await LocalCommands.RunAsync(args);
    return;
}
var command = args[0];
var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i += 2)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length || !values.TryAdd(args[i][2..], args[i + 1]))
        throw new ArgumentException("Arguments must be unique --name value pairs.");
}
string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}");
string Optional(string key, string fallback) => values.GetValueOrDefault(key, fallback);
Uri Endpoint(string key)
{
    var uri = new Uri(Required(key));
    if (uri.Scheme != "https" || uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        throw new ArgumentException($"--{key} must be an HTTPS service endpoint without credentials.");
    return uri;
}
var credential = new AzureCliCredential();
var key = new TransferKey(Required("scope"), Required("request-id"));
key.Validate();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
if (command is "status" or "resume")
{
    var ledger = new BlobLedger(new BlobServiceClient(Endpoint("ledger"), credential)
        .GetBlobContainerClient(Optional("ledger-container", "transfer-ledger")));
    if (command == "status")
    {
        var blob = ledger.Container.GetBlobClient(key.LedgerName);
        var result = await blob.DownloadContentAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        { LedgerETag = result.Value.Details.ETag.ToString(), Record = result.Value.Content.ToObjectFromJson<TransferRecord>() }, jsonOptions));
    }
    else
    {
        if (Required("apply") != "true") throw new ArgumentException("Review status first; --apply true explicitly executes recovery.");
        var queue = new QueueServiceClient(Endpoint("queue-service"), credential, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None })
            .GetQueueClient(Optional("queue", "transfer-work"));
        await new TransferRecovery(ledger, queue).ResumeAsync(key, Required("expected-ledger-etag"),
            Required("expected-source-etag"), Required("approval-reference"), Required("operator-label"), CancellationToken.None);
        Console.WriteLine("Recovery recorded and queued. This is not confirmation of a completed transfer.");
    }
}
else throw new ArgumentException("Unknown command.");
