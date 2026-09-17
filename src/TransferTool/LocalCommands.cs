using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using BlobTransfer;
using System.Text.Json;

internal static class LocalCommands
{
    public static async Task RunAsync(string[] args)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
            throw new InvalidOperationException("Local commands must not run in Azure.");
        if (args[0] == "local-seed" && args.Length != 1 ||
            args[0] == "local-smoke" && (args.Length != 3 || args[1] != "--evidence"))
            throw new ArgumentException("Use local-seed or local-smoke --evidence <path>.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var blobs = new BlobServiceClient(LocalDevelopment.ConnectionString);
        var source = blobs.GetBlobContainerClient("incoming");
        var target = blobs.GetBlobContainerClient("local-destination");
        var ledger = new BlobLedger(blobs.GetBlobContainerClient("transfer-ledger"));
        if (args[0] == "local-seed")
        {
            foreach (var name in new[] { "incoming", "local-destination", "transfer-ledger" })
                await blobs.GetBlobContainerClient(name).CreateIfNotExistsAsync(cancellationToken: ct);
            var queues = new QueueServiceClient(LocalDevelopment.ConnectionString);
            foreach (var name in new[] { "transfer-work", "transfer-work-poison", "webjobs-blobtrigger-poison" })
                await queues.GetQueueClient(name).CreateIfNotExistsAsync(cancellationToken: ct);
            Console.WriteLine("Local containers and queues are ready.");
            return;
        }
        var run = Guid.NewGuid().ToString("N");
        var content = "Local Functions host smoke test " + run;
        var hash = TransferPolicy.HashText(content);
        var options = new PipelineOptions(new() { [""] = "default" }, IncludeSourceVersions: false);
        var expectedDestination = $"v1/default/{hash}/payload";
        var requestIds = new List<string>();
        // Wait between revisions: Azurite does not qualify Azure retained-version semantics.
        // No queue sends and no direct Function/engine calls: the running host must do the work.
        foreach (var name in new[] { $"smoke/{run}/report.txt", $"smoke/{run}/copy.txt", $"smoke/{run}/report.txt" })
        {
            var blob = source.GetBlobClient(name);
            await blob.UploadAsync(BinaryData.FromString(content), overwrite: true, cancellationToken: ct);
            var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            var key = TransferPolicy.Key(new WorkItem(1, name, properties.ETag.ToString()), source.Uri, options);
            requestIds.Add(key.RequestId);
            while (true)
            {
                var record = await ledger.ReadAsync(key.LedgerName, ct);
                if (record?.Status == "Quarantined") throw new InvalidOperationException(record.ErrorCode);
                if (record?.Status == "Completed")
                {
                    if (record.DestinationName != expectedDestination || record.Sha256 != hash)
                        throw new InvalidOperationException("Smoke sources did not converge on the expected destination.");
                    break;
                }
                await Task.Delay(500, ct);
            }
            if (!(await blob.ExistsAsync(ct)).Value) throw new InvalidOperationException("Source was removed.");
        }
        if (requestIds.Distinct().Count() != 3) throw new InvalidOperationException("Expected three distinct revisions.");
        var downloaded = (await target.GetBlobClient(expectedDestination).DownloadContentAsync(ct)).Value.Content.ToString();
        if (downloaded != content) throw new InvalidOperationException("Destination bytes differ.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new
        {
            passed = true, runId = run, requestIds, destination = expectedDestination, sha256 = hash,
            sourceRevisions = 3, completedUtc = DateTimeOffset.UtcNow, sourceVersionsTested = false
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine("PASS: three ordinary upload revisions completed through the running host to one verified destination.");
    }
}
