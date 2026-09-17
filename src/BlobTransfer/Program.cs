using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using BlobTransfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder().ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        string Required(string key) => config[key] ?? throw new InvalidOperationException($"Missing setting: {key}");
        int Number(string key, int fallback, int minimum, int maximum)
        {
            var value = int.Parse(config[key] ?? fallback.ToString());
            return value >= minimum && value <= maximum ? value : throw new InvalidOperationException($"Invalid setting: {key}");
        }
        var local = LocalDevelopment.IsEnabled(config);
        Uri Endpoint(string key)
        {
            var uri = new Uri(Required(key));
            if (uri.Scheme != "https" || uri.Query.Length != 0 || uri.UserInfo.Length != 0)
                throw new InvalidOperationException($"Invalid HTTPS endpoint: {key}");
            return uri;
        }
        var sourceContainer = Required("UploadContainer");
        var ledgerContainer = Required("Ledger:container");
        if (string.Equals(sourceContainer, ledgerContainer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Upload and ledger containers must be different to prevent dispatch loops.");
        BlobServiceClient solution;
        BlobServiceClient destinationService;
        QueueServiceClient queueService;
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
        if (local)
        {
            solution = new BlobServiceClient(LocalDevelopment.ConnectionString);
            destinationService = new BlobServiceClient(LocalDevelopment.ConnectionString);
            queueService = new QueueServiceClient(LocalDevelopment.ConnectionString, queueOptions);
            if (Required("Destination:container") == sourceContainer || Required("Destination:container") == ledgerContainer)
                throw new InvalidOperationException("Local source, ledger, and destination containers must differ.");
        }
        else
        {
            TokenCredential credential = string.IsNullOrEmpty(config["WEBSITE_INSTANCE_ID"])
                ? new AzureCliCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(Required("AZURE_CLIENT_ID")));
            var solutionEndpoint = Endpoint("UploadStorage:blobServiceUri");
            if (solutionEndpoint != Endpoint("Ledger:blobServiceUri"))
                throw new InvalidOperationException("Ledger and upload storage must use the same solution account endpoint.");
            solution = new BlobServiceClient(solutionEndpoint, credential);
            destinationService = new BlobServiceClient(Endpoint("Destination:blobServiceUri"), credential);
            queueService = new QueueServiceClient(Endpoint("TransferQueueStorage:queueServiceUri"), credential, queueOptions);
        }
        var source = solution.GetBlobContainerClient(sourceContainer);
        var destination = destinationService.GetBlobContainerClient(Required("Destination:container"));
        var ledger = solution.GetBlobContainerClient(ledgerContainer);
        var queueName = Required("TransferQueue");
        services.AddSingleton(new StorageClients(source, destination));
        services.AddSingleton(new BlobLedger(ledger));
        services.AddSingleton(new QueueClients(queueService.GetQueueClient(queueName), queueService.GetQueueClient(queueName + "-poison"), queueService.GetQueueClient("webjobs-blobtrigger-poison")));
        var scopes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Required("Copy:scopePrefixes"))
            ?? throw new InvalidOperationException("Scope prefix map is required.");
        if (scopes.Count == 0) throw new InvalidOperationException("At least one source prefix mapping is required.");
        foreach (var pair in scopes) new TransferKey(pair.Value, new string('0', 64)).Validate();
        services.AddSingleton(new PipelineOptions(scopes,
            Number("Copy:maxBytes", 1073741824, 1, 1073741824),
            Number("Recovery:maxAttempts", 10, 1, 100),
            Number("Recovery:retryMinutes", 15, 1, 1440),
            Number("Recovery:scanPageSize", 100, 1, 5000),
            Number("Recovery:scanPagesPerRun", 5, 1, 100),
            Number("Recovery:verifyAfterHours", 24, 1, 8760),
            bool.Parse(config["Recovery:includeSourceVersions"] ?? "true")));
        services.AddSingleton<TransferEngine>();
    }).Build();
await host.RunAsync();
