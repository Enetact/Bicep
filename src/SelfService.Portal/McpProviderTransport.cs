using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SelfService.Portal;

// Credentials are resolved by the session-owned broker immediately before connection.
// Keep them out of generated record diagnostics as well as SDK logging.
public sealed record ProviderConnectionSpec(string Id, string? Command, string[] Args, string? Url,
    Dictionary<string, string> Environment, Dictionary<string, string> Headers, string WorkingDirectory)
{
    internal string[] RedactionValues { get; init; } = [];
    internal string[] CredentialFiles { get; init; } = [];
    public override string ToString() => $"MCP provider ({Id})";
}

public sealed record ProviderTool(string Name, string? Description, JsonElement InputSchema,
    bool? ReadOnly, bool? Destructive, bool? OpenWorld, bool SensitiveHint = false);

public interface IProviderMcpClient : IAsyncDisposable
{
    Task<ProviderTool[]> ListTools(CancellationToken cancellationToken);
    Task<JsonElement> Call(string tool, JsonElement arguments, CancellationToken cancellationToken);
    // Only the trusted broker calls this after verifying the exact human-approved
    // plan. Default implementations keep existing fake/test clients compatible.
    Task<JsonElement> CallApproved(string tool, JsonElement arguments, CancellationToken cancellationToken) => Call(tool, arguments, cancellationToken);
}

public interface IProviderMcpFactory
{
    Task<IProviderMcpClient> Connect(ProviderConnectionSpec spec, CancellationToken cancellationToken);
}

// All service calls use the same SDK transport. Authorization and approval belong to
// the broker; server annotations are discovery metadata, never authority to execute.
public sealed class McpProviderFactory : IProviderMcpFactory
{
    public async Task<IProviderMcpClient> Connect(ProviderConnectionSpec spec, CancellationToken cancellationToken)
    {
        using var activity = McpObservability.Start("mcp.connect.transport", spec.Id);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        IClientTransport? transport = null;
        try
        {
            if (string.IsNullOrWhiteSpace(spec.Id) || spec.Id.Length > 64 ||
                spec.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidOperationException();
            if (!string.IsNullOrWhiteSpace(spec.Url))
            {
                if (!string.IsNullOrWhiteSpace(spec.Command) ||
                    !Uri.TryCreate(spec.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
                    endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                    throw new InvalidOperationException();
                // Do not follow redirects with provider credentials. The broker owns
                // the allowlisted endpoint; redirects require a configuration change.
                var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxResponseHeadersLength = 32 });
                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = spec.Id,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = spec.Headers,
                    ConnectionTimeout = TimeSpan.FromSeconds(45),
                    MaxReconnectionAttempts = 0
                }, http, NullLoggerFactory.Instance, ownsHttpClient: true);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(spec.Command) || !Directory.Exists(spec.WorkingDirectory))
                    throw new InvalidOperationException();
                transport = new OwnedStdioTransport(spec);
            }
            var consent = new InvocationConsent(spec.Id);
            var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new() { Name = "platform-studio-provider-broker", Version = "1.0.0" },
                InitializationTimeout = TimeSpan.FromSeconds(50),
                Capabilities = new() { Elicitation = new() { Form = new() } },
                Handlers = new() { ElicitationHandler = consent.Respond }
            }, NullLoggerFactory.Instance, deadline.Token);
            McpObservability.Complete(activity, "connected");
            return new ProviderMcpClient(client, transport, CredentialValues(spec), consent, spec.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            McpObservability.Complete(activity, "cancelled", true);
            await SafeDispose(transport);
            throw;
        }
        catch (ProcessContainmentException error)
        {
            McpObservability.Complete(activity, "containment_unavailable", true);
            await SafeDispose(transport);
            throw new PortalException($"Windows process containment is unavailable (error {error.ErrorCode}). The MCP provider was stopped safely. Run the portal in a Windows host that permits nested job objects.", 503);
        }
        catch
        {
            McpObservability.Complete(activity, "connection_failed", true);
            await SafeDispose(transport);
            // SDK/process/HTTP errors may embed request headers, environment or URLs.
            // Never retain the provider exception as an inner exception.
            throw new InvalidOperationException("MCP provider connection failed or timed out. Check its local installation, endpoint and authentication.");
        }
        finally
        {
            // The CLI has consumed the private response file before its MCP
            // handshake finishes. Retain no second copy of its dashboard key.
            foreach (var file in spec.CredentialFiles)
                try { File.Delete(file); } catch { /* The private ACL remains fail-closed if cleanup is unavailable. */ }
        }
    }

    static string[] CredentialValues(ProviderConnectionSpec spec) => spec.Environment
        .Where(x => new[] { "TOKEN", "SECRET", "PASSWORD", "KEY", "CREDENTIAL" }.Any(part => x.Key.Contains(part, StringComparison.OrdinalIgnoreCase)))
        .Select(x => x.Value)
        .Concat(spec.Headers.Values).Concat(spec.RedactionValues)
        .SelectMany(x => x.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            ? new[] { x, x[(x.IndexOf(' ') + 1)..] } : new[] { x })
        .Where(x => x.Length >= 8).Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ToArray();

    internal static async ValueTask SafeDispose(object? value)
    {
        try { if (value is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        catch { /* Cleanup must not surface downstream credential-bearing exceptions. */ }
    }

    sealed class InvocationConsent(string provider)
    {
        int remaining;
        string? activeTool;
        public void Begin(string tool, bool approved) { activeTool = tool; Volatile.Write(ref remaining, approved ? 1 : 0); }
        public void End() { Volatile.Write(ref remaining, 0); activeTool = null; }
        public ValueTask<ElicitResult> Respond(ElicitRequestParams? request, CancellationToken cancellationToken)
        {
            // SDK inbound requests run on their own execution context, so a
            // connection-owned, serialized invocation gate is used instead of
            // AsyncLocal. Approval is never enabled during connect/discovery.
            var schema = request?.RequestedSchema;
            if (request is null || cancellationToken.IsCancellationRequested || request.Mode is not (null or "form") || request.Url is not null ||
                request.Message?.Length > 4096 || schema?.Properties?.Count != 1)
                return ValueTask.FromResult(new ElicitResult { Action = "decline" });
            var field = schema.Properties.Single();
            var booleanConfirmation = field.Key is "confirm" or "confirmation" or "approve" or "approved" or "consent" or "proceed" &&
                field.Value is ElicitRequestParams.BooleanSchema;
            // Azure 3.0.0 requests its native decision=accept/reject enum and
            // validates both the envelope action and this content value. Match
            // that fixed schema and the currently approved tool, never arbitrary
            // strings, account grants, URLs or credentials requested by a server.
            var azureDecision = provider == "azure" && field.Key == "decision" && activeTool is not null &&
                request.Message?.Contains("'" + activeTool + "'", StringComparison.Ordinal) == true &&
                schema.Required is { Count: 1 } && schema.Required[0] == "decision" && (field.Value switch
                {
                    ElicitRequestParams.TitledSingleSelectEnumSchema choice => IsDecision(choice.OneOf.Select(x => x.Const)),
                    ElicitRequestParams.UntitledSingleSelectEnumSchema choice => IsDecision(choice.Enum),
                    _ => false
                });
            if (!(booleanConfirmation || azureDecision) ||
                schema.Required?.Any(name => name != field.Key) == true ||
                Interlocked.CompareExchange(ref remaining, 0, 1) != 1)
                return ValueTask.FromResult(new ElicitResult { Action = "decline" });
            return ValueTask.FromResult(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement> { [field.Key] = azureDecision ? JsonSerializer.SerializeToElement("accept") : JsonSerializer.SerializeToElement(true) }
            });
        }
        static bool IsDecision(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).SequenceEqual(new[] { "accept", "reject" });
    }

    sealed class ProcessContainmentException(int errorCode) : Exception
    {
        public int ErrorCode { get; } = errorCode;
    }

    // Own the process handle instead of asking the SDK to launch it, so the
    // governed stop script can verify and stop a child after a forced host exit.
    // The protocol itself still uses the SDK's real stream transport/session.
    sealed class OwnedStdioTransport(ProviderConnectionSpec spec) : IClientTransport, IAsyncDisposable
    {
        Process? process;
        WindowsProcessJob? job;
        Task? stderr;
        string? receipt;
        public string Name => spec.Id;
        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (process is not null) throw new InvalidOperationException();
            var start = new ProcessStartInfo(spec.Command!)
            {
                WorkingDirectory = spec.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in spec.Args) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var item in StdioClientTransportOptions.GetDefaultEnvironmentVariables()) start.Environment[item.Key] = item.Value;
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(spec.WorkingDirectory, ".net-bundle");
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            foreach (var item in spec.Environment) start.Environment[item.Key] = item.Value;
            cancellationToken.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new InvalidOperationException();
            try
            {
                if (OperatingSystem.IsWindows()) job = WindowsProcessJob.Assign(process);
                var parent = Process.GetCurrentProcess();
                var receiptPath = Path.Combine(spec.WorkingDirectory, "process.json");
                var executable = await VerifiedExecutable(process, cancellationToken);
                var record = new
                {
                    schema = "platform.mcp-process/v1",
                    provider = spec.Id,
                    pid = process.Id,
                    parentPid = parent.Id,
                    parentStartedUtc = parent.StartTime.ToUniversalTime().ToString("O"),
                    startedUtc = process.StartTime.ToUniversalTime().ToString("O"),
                    executable
                };
                // A receipt contains only identity/ownership metadata. Arguments,
                // environment, headers and raw stderr are deliberately excluded.
                await using (var receiptFile = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    receipt = receiptPath;
                    await JsonSerializer.SerializeAsync(receiptFile, record, cancellationToken: cancellationToken);
                }
                stderr = DrainStderr(process);
                return await new StreamClientTransport(process.StandardInput.BaseStream,
                    new BudgetedReadStream(process.StandardOutput.BaseStream), NullLoggerFactory.Instance)
                    .ConnectAsync(cancellationToken);
            }
            catch { await DisposeAsync(); throw; }
        }

        static async Task<string> VerifiedExecutable(Process owned, CancellationToken cancellationToken)
        {
            // Process.Start can return before Windows has published its main
            // module. Wait briefly for observed identity; never substitute the
            // configured command or write an unverifiable ownership receipt.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owned.Refresh();
                if (owned.HasExited) throw new InvalidOperationException();
                try
                {
                    var executable = owned.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable) && Path.IsPathFullyQualified(executable) && File.Exists(executable)) return executable;
                }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 299) { }
                await Task.Delay(25, cancellationToken);
            }
            throw new InvalidOperationException();
        }

        static async Task DrainStderr(Process owned)
        {
            var buffer = new char[2048];
            var total = 0;
            try
            {
                int read;
                while ((read = await owned.StandardError.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total <= 64 * 1024) continue;
                    // Reject a runaway log producer; never store its credential-
                    // bearing stderr or let a ReadLine allocate without a bound.
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                    break;
                }
            }
            catch { }
            finally { Array.Clear(buffer); }
        }

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref process, null);
            if (owned is null) return;
            try
            {
                // Terminate the owned tree before closing stdin. A parent that
                // exits on EOF could otherwise leave a still-running tool child
                // behind before Kill(entireProcessTree) can enumerate it.
                job?.Dispose();
                job = null;
                if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                try { owned.StandardInput.Close(); } catch { }
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await owned.WaitForExitAsync(shutdown.Token); }
                catch (OperationCanceledException)
                {
                    if (!owned.HasExited) owned.Kill(entireProcessTree: true);
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await owned.WaitForExitAsync(killed.Token);
                }
                if (stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(2));
                if (receipt is not null) File.Delete(receipt);
            }
            finally { job?.Dispose(); job = null; owned.Dispose(); }
        }

        // Unnamed Windows job objects are kernel-owned process containment. The
        // last job handle closing kills all members, including tool descendants
        // whose stdio parent has already exited. No process-name adoption occurs.
        sealed class WindowsProcessJob(SafeFileHandle handle) : IDisposable
        {
            public static WindowsProcessJob Assign(Process owned)
            {
                var handle = CreateJobObjectW(IntPtr.Zero, null);
                if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new ProcessContainmentException(code); }
                try
                {
                    var information = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
                    if (!SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                        throw new ProcessContainmentException(Marshal.GetLastWin32Error());
                    if (!AssignProcessToJobObject(handle, owned.SafeHandle))
                        throw new ProcessContainmentException(Marshal.GetLastWin32Error());
                    return new(handle);
                }
                catch { handle.Dispose(); throw; }
            }
            public void Dispose() => handle.Dispose();
            [StructLayout(LayoutKind.Sequential)]
            struct BasicLimitInformation
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public nuint Affinity;
                public uint PriorityClass, SchedulingClass;
            }
            [StructLayout(LayoutKind.Sequential)]
            struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
            [StructLayout(LayoutKind.Sequential)]
            struct ExtendedLimitInformation
            {
                public BasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint informationLength);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        }

        // Cap raw stdio output before the SDK parses it. Accepted individual
        // catalogs/results have the tighter limits below; excessive output fails
        // the connection instead of returning a silently truncated result.
        sealed class BudgetedReadStream(Stream inner) : Stream
        {
            const long MaximumWireBytes = 128L * 1024 * 1024;
            long consumed;
            void Accept(int count)
            {
                consumed += count;
                if (consumed > MaximumWireBytes) throw new IOException("MCP wire output budget exceeded.");
            }
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Accept(read); return read; }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            { var read = await inner.ReadAsync(buffer, cancellationToken); Accept(read); return read; }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
    }

    sealed class ProviderMcpClient(McpClient client, IClientTransport transport, string[] secrets, InvocationConsent consent, string provider) : IProviderMcpClient
    {
        readonly SemaphoreSlim calls = new(1, 1);
        int faulted;
        const int MaximumTools = 4096;
        const int MaximumCatalogBytes = 16 * 1024 * 1024;
        const int MaximumResultBytes = 2 * 1024 * 1024;
        const int MaximumArgumentsBytes = 256 * 1024;

        public async Task<ProviderTool[]> ListTools(CancellationToken cancellationToken)
        {
            using var activity = McpObservability.Start("mcp.tools.list", provider);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var result = new List<ProviderTool>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                var bytes = 0;
                do
                {
                    var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, deadline.Token);
                    var bounded = BoundedJson(page, MaximumCatalogBytes - bytes);
                    bytes += Encoding.UTF8.GetByteCount(bounded.GetRawText());
                    foreach (var tool in page.Tools)
                    {
                        if (result.Count >= MaximumTools || string.IsNullOrWhiteSpace(tool.Name) || !names.Add(tool.Name))
                            throw new InvalidOperationException();
                        var metadata = tool.Annotations;
                        // Azure's native secret-elicitation contract marks _meta
                        // SecretHint independently of standard tool annotations.
                        // Preserve it for broker approval/output withholding; a
                        // present malformed flag must not downgrade a secret tool.
                        var sensitive = tool.Meta?.Any(item => item.Key.Equals("SecretHint", StringComparison.OrdinalIgnoreCase) &&
                            (item.Value is not JsonValue flag || !flag.TryGetValue<bool>(out var secret) || secret)) == true;
                        result.Add(new(tool.Name, Redact(tool.Description), Redact(tool.InputSchema),
                            metadata?.ReadOnlyHint, metadata?.DestructiveHint, metadata?.OpenWorldHint, sensitive));
                    }
                    cursor = page.NextCursor;
                    if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException();
                } while (cursor is not null);
                activity?.SetTag("platform.item.count", result.Count);
                McpObservability.Complete(activity, "discovered");
                return result.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { McpObservability.Complete(activity, "cancelled", true); throw; }
            catch { McpObservability.Complete(activity, "discovery_failed", true); throw new InvalidOperationException("MCP tool discovery failed, timed out or exceeded the catalog budget. No partial catalog was accepted."); }
        }

        public Task<JsonElement> Call(string tool, JsonElement arguments, CancellationToken cancellationToken) => CallCore(tool, arguments, false, cancellationToken);
        public Task<JsonElement> CallApproved(string tool, JsonElement arguments, CancellationToken cancellationToken) => CallCore(tool, arguments, true, cancellationToken);
        async Task<JsonElement> CallCore(string tool, JsonElement arguments, bool approved, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tool) || tool.Length > 256 || arguments.ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumArgumentsBytes)
                throw new InvalidOperationException("MCP tool arguments are invalid or exceed the request budget.");
            using var activity = McpObservability.Start("mcp.tools.call.transport", provider, tool);
            await calls.WaitAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                if (Volatile.Read(ref faulted) != 0) throw new InvalidOperationException();
                consent.Begin(tool, approved);
                var result = await client.CallToolAsync(new CallToolRequestParams
                {
                    Name = tool,
                    Arguments = arguments.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
                }, deadline.Token);
                var output = Redact(BoundedJson(result, MaximumResultBytes));
                var failed = output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                McpObservability.Complete(activity, failed ? "tool_error" : "succeeded", failed);
                return output;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { McpObservability.Complete(activity, "cancelled", true); Volatile.Write(ref faulted, 1); consent.End(); await SafeDispose(transport); await SafeDispose(client); throw; }
            catch
            {
                McpObservability.Complete(activity, "call_failed", true);
                Volatile.Write(ref faulted, 1);
                consent.End(); await SafeDispose(transport); await SafeDispose(client);
                // A timed-out write can have succeeded remotely. The broker records
                // unknown outcome and must not automatically replay this call.
                throw new InvalidOperationException("MCP tool call failed, timed out or exceeded the response budget. Its outcome may be unknown; reconcile before retrying.");
            }
            finally { consent.End(); calls.Release(); }
        }

        static JsonElement BoundedJson<T>(T value, int maximum)
        {
            using var stream = new BoundedStream(maximum);
            JsonSerializer.Serialize(stream, value, McpJsonUtilities.DefaultOptions);
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

        string? Redact(string? value)
        {
            if (value is null) return null;
            foreach (var secret in secrets) value = value.Replace(secret, "[credential redacted]", StringComparison.Ordinal);
            return value;
        }

        JsonElement Redact(JsonElement value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(value, writer);
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

        void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String: writer.WriteStringValue(Redact(value.GetString())); break;
                default: value.WriteTo(writer); break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            consent.End();
            await SafeDispose(transport);
            await SafeDispose(client);
            Array.Clear(secrets);
        }

        sealed class BoundedStream(int maximum) : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Length + count > maximum) throw new InvalidOperationException();
                base.Write(buffer, offset, count);
            }
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (Length + buffer.Length > maximum) throw new InvalidOperationException();
                base.Write(buffer);
            }
        }
    }
}
