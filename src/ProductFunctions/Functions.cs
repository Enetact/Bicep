using System.Net;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ProductFunctions;

public class Functions
{
    [Function("Health")]
    public async Task<HttpResponseData> Health([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        // Azure Easy Auth validates the token before forwarding this request. Local host is not an auth emulator.
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "Healthy", product = "http-functions", version = typeof(Functions).Assembly.GetName().Version?.ToString() });
        return response;
    }

    [Function("ProcessWork")]
    public async Task ProcessWork([ServiceBusTrigger("work", Connection = "Bus", AutoCompleteMessages = false)] ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions, CancellationToken cancellation)
    {
        var endpoint = Environment.GetEnvironmentVariable("Receipts__endpoint") ?? throw new InvalidOperationException("Receipt endpoint missing.");
        var store = new BlobReceiptStore(new BlobContainerClient(new Uri(endpoint.TrimEnd('/') + "/receipts"), new DefaultAzureCredential()));
        StoreResult result;
        try { result = await WorkProcessor.Process(message.Body.ToString(), store, cancellation); }
        catch (InvalidWorkException)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "InvalidContract", cancellationToken: cancellation); return;
        }
        if (result == StoreResult.Conflict)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "ConflictingId", cancellationToken: cancellation); return;
        }
        await actions.CompleteMessageAsync(message, cancellation);
        // Transient storage errors propagate: the message is retried, never completed prematurely.
    }
}

public sealed class BlobReceiptStore(BlobContainerClient container) : IReceiptStore
{
    public async Task<StoreResult> Put(WorkReceipt receipt, CancellationToken ct)
    {
        var blob = container.GetBlobClient(receipt.Id + ".json");
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(receipt), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
            return StoreResult.Created;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var stored = JsonSerializer.Deserialize<WorkReceipt>((await blob.DownloadContentAsync(ct)).Value.Content.ToString());
            return stored == receipt ? StoreResult.Duplicate : StoreResult.Conflict;
        }
    }
}
