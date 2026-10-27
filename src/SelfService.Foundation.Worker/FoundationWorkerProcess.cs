using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;

namespace SelfService.Foundation.Worker;

public sealed record WorkerConfiguration(string CustomerId, string CellId, string DatabasePath,
    string Mode = "Disabled", bool QualificationOnly = false, string? MockEndpoint = null,
    string? WorkerId = null, int PollIntervalMilliseconds = 250, int HeartbeatIntervalMilliseconds = 1000,
    int RunSeconds = 0, string? ReceiptPath = null, EntitlementTrustKey[]? EntitlementTrustKeys = null,
    bool RequireSignedEntitlement = false, bool RequireActivationAuthority = false, string? ActivationAuthorityDirectory = null);

public sealed record WorkerProcessReceipt(int ProcessId, DateTimeOffset ProcessStartedUtc, string ExecutablePath,
    string WorkerAssemblyPath, string ConfigurationPath, string ConfigurationSha256, string Mode,
    string WorkerId, string InstanceId, long Generation, DateTimeOffset RegisteredUtc,
    string Schema = "platform-studio.foundation-worker-process.v1");

public static class FoundationWorkerProcess
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, PropertyNameCaseInsensitive = false
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try { return await RunValidatedAsync(args); }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or UnauthorizedAccessException or
            SqliteException or FoundationStoreException or PipelineContractException or InvalidOperationException)
        {
            // Never echo configuration, payload, connection, path or exception text into process logs.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = e is FoundationStoreException store ? store.Code : "WorkerConfigurationOrStorageRejected",
                category = e.GetType().Name, nativeCode = e.HResult }, Json));
            return 2;
        }
    }

    static async Task<int> RunValidatedAsync(string[] args)
    {
        var validateOnly = args.Length == 2 && args[0] == "--validate-config";
        if (!validateOnly && (args.Length is not (2 or 3) || args[0] != "--config" || args.Length == 3 && args[2] != "--allow-mock-execution"))
            throw new ArgumentException("Worker arguments rejected.");
        var configPath = Path.GetFullPath(args[1]); RejectLinks(configPath);
        byte[] bytes;
        using (var input = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is < 2 or > 32768) throw new ArgumentException("Worker configuration rejected.");
            bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); Unique(document.RootElement);
        var config = document.RootElement.Deserialize<WorkerConfiguration>(Json) ?? throw new JsonException();
        if (config.Mode is not ("Disabled" or "MockLoopback" or "ClaimHold") ||
            config.PollIntervalMilliseconds is < 50 or > 5000 || config.HeartbeatIntervalMilliseconds is < 250 or > 10000 ||
            config.RunSeconds is < 0 or > 3600 || string.IsNullOrWhiteSpace(config.DatabasePath))
            throw new ArgumentException("Worker configuration rejected.");
        if (!CustomerContextResolver.ValidId(config.CustomerId) || !CustomerContextResolver.ValidId(config.CellId) ||
            config.WorkerId is not null && !CustomerContextResolver.ValidId(config.WorkerId))
            throw new ArgumentException("Worker scope rejected.");
        if (config.Mode != "Disabled" && (!config.QualificationOnly || !validateOnly && args.Length != 3))
            throw new FoundationStoreException("SyntheticWorkerOptInRequired", 403);
        if (config.Mode == "Disabled" && (config.MockEndpoint is not null || !validateOnly && args.Length == 3) ||
            config.Mode == "ClaimHold" && config.MockEndpoint is not null ||
            config.Mode == "MockLoopback" && config.MockEndpoint is null)
            throw new ArgumentException("Worker transport rejected.");
        if (config.Mode == "ClaimHold" && config.RunSeconds is < 1 or > 600)
            throw new ArgumentException("Qualification claim hold must be time bounded.");
        if (config.EntitlementTrustKeys?.Length > 16) throw new ArgumentException("Worker trust store rejected.");
        var entitlements = new EntitlementVerifier(config.EntitlementTrustKeys ?? [], requireSignedMode: config.RequireSignedEntitlement);
        var directory = Path.GetDirectoryName(configPath)!;
        var databasePath = Path.GetFullPath(config.DatabasePath, directory);
        if (config.RequireActivationAuthority != (config.ActivationAuthorityDirectory is not null))
            throw new ArgumentException("An explicit existing activation authority is required.");
        var authorityPath = config.ActivationAuthorityDirectory is not null ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ActivationAuthorityDirectory, directory)) : null;
        using var transport = config.Mode == "MockLoopback" ? new PipelineLoopbackTransport(new Uri(config.MockEndpoint!, UriKind.Absolute)) : null;
        if (validateOnly)
        {
            if (config.ReceiptPath is null) throw new FoundationStoreException("WorkerReceiptRequired", 400);
            _ = WorkerReceiptFile.Validate(config.ReceiptPath, directory, databasePath, configPath, authorityPath);
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "platform-studio.foundation-worker-preflight.v1", receiptPathValidated = true,
                workerStartupPerformed = false, externalDispatch = false, configurationSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, Json));
            return 0;
        }
        // Receipt refusal and exclusive destination reservation precede every SQLite open or worker mutation.
        using var receiptFile = WorkerReceiptFile.Reserve(config.ReceiptPath, directory, databasePath, configPath, authorityPath);
        // Preflight is read-only and precedes FoundationStore's normal constructor/migration path.
        Preflight(databasePath, config.CellId, config.CustomerId);
        // These standalone qualification modes have no trusted bound-consumer composition.
        // Refuse before registration/heartbeat/claim; configuration-only validation remains software/path-only.
        if (config.Mode is "MockLoopback" or "ClaimHold" &&
            FoundationStore.ExistingCredentialRuntimeBindingRequired(databasePath, config.CustomerId, config.CellId))
            throw new FoundationStoreException("BoundPipelineConsumerRequired", 403);
        using var activation = config.RequireActivationAuthority
            ? new FoundationCellActivationAuthority(authorityPath!, config.CustomerId, config.CellId)
                .AcquireLease(databasePath)
            : null;
        var store = new FoundationStore(databasePath, config.CellId, config.CustomerId, initializeSchema: false,
            activationGuard: activation is not null ? activation.AssertCurrent : null);
        var workerId = config.WorkerId ?? "worker-" + Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        var handle = store.RegisterWorker(config.CustomerId, workerId, process.Id, process.StartTime.ToUniversalTime(), config.Mode);
        using var cancellation = new CancellationTokenSource();
        if (config.RunSeconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(config.RunSeconds));
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        Task? heartbeats = null;
        try
        {
            var receipt = new WorkerProcessReceipt(process.Id, process.StartTime.ToUniversalTime(), process.MainModule!.FileName,
                Assembly.GetExecutingAssembly().Location, configPath, Convert.ToHexStringLower(SHA256.HashData(bytes)), config.Mode,
                handle.WorkerId, handle.InstanceId, handle.Generation, DateTimeOffset.UtcNow);
            receiptFile?.Publish(receipt, Json);
            Console.WriteLine(JsonSerializer.Serialize(receipt, Json));
            heartbeats = HeartbeatsAsync(store, handle, config, cancellation);
            var runner = new FoundationPipelineExecutionService(store, new PipelineDispatcher(transport ?? (IPipelineTransport)new DeniedPipelineTransport()), entitlements);
            var delay = TimeSpan.FromMilliseconds(config.PollIntervalMilliseconds);
            var failures = 0;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (config.Mode == "MockLoopback")
                        await runner.RunOnceAsync(handle.WorkerId, cancellation.Token);
                    else if (config.Mode == "ClaimHold")
                    {
                        // Synthetic qualification only: leave a pre-effect claim to exercise real process crash recovery.
                        var claimed = store.ClaimNext(config.CustomerId, handle.WorkerId, TimeSpan.FromSeconds(2));
                        if (claimed is not null) await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                    }
                    failures = 0;
                    await Task.Delay(delay, cancellation.Token);
                }
                catch (Exception e) when (e is SqliteException || e is FoundationStoreException { Code: not "WorkerFenceConflict" })
                {
                    failures++;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, 250 * Math.Pow(2, Math.Min(failures, 7)))), cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Cancel(); Console.CancelKeyPress -= onCancel;
            if (heartbeats is not null)
                try { await heartbeats; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            store.StopWorker(config.CustomerId, handle);
        }
        return 0;
    }

    static async Task HeartbeatsAsync(FoundationStore store, FoundationWorkerHandle handle, WorkerConfiguration config,
        CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.HeartbeatIntervalMilliseconds));
            while (await timer.WaitForNextTickAsync(cancellation.Token)) store.HeartbeatWorker(config.CustomerId, handle);
        }
        catch (Exception e) when (e is SqliteException or FoundationStoreException)
        { cancellation.Cancel(); throw; }
    }

    public static void Preflight(string databasePath, string cellId, string customerId)
    {
        RejectLinks(databasePath);
        if (!File.Exists(databasePath)) throw new FoundationStoreException("WorkerExistingCellRequired", 400);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT schema_version,cell_id,customer_id FROM cell WHERE singleton=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != FoundationStore.CurrentSchemaVersion ||
            reader.GetString(1) != cellId || reader.GetString(2) != customerId)
            throw new FoundationStoreException("WorkerCellSchemaOrBindingRejected", 403);
    }

    static void RejectLinks(string path)
    {
        for (var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; current is not null;
            current = current is DirectoryInfo dir ? dir.Parent : new FileInfo(current.FullName).Directory)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked worker paths rejected.");
    }

    static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException(); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
