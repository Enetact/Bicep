using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

// Project-owned agent-host backend. Codex app-server is the inference protocol, not AHP itself.
public sealed class CodexAgentRuntime : IAsyncDisposable
{
    readonly Process process;
    readonly SemaphoreSlim writes = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Task reader;
    long sequence;
    int disposed;
    TaskCompletionSource<string>? turnFinished;
    string? activeThread;
    string finalText = "";
    public string Home { get; }
    public string? LoginId { get; private set; }
    public Func<JsonElement, CancellationToken, Task<object>>? ToolHandler { get; set; }
    public bool Alive => !process.HasExited;

    CodexAgentRuntime(Process child, string home)
    {
        process = child; Home = home;
        reader = Read();
        _ = DrainErrors(); // Never persist raw provider errors, credentials or prompt fragments.
    }
    public static string? FindExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && Path.GetFileName(configured).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured) ? configured : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Directory.Exists))
        {
            var direct = Path.Combine(dir, "codex.exe");
            if (File.Exists(direct)) return direct;
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            var triple = arch == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            var npm = Path.Combine(dir, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{arch}", "vendor", triple, "bin", "codex.exe");
            if (File.Exists(npm)) return npm;
        }
        return null;
    }
    public static async Task<CodexAgentRuntime> Start(PortalOptions options, CancellationToken ct)
    {
        var exe = FindExecutable(options.CodexPath) ?? throw new PortalException("Install Codex CLI or configure Portal:CodexPath to its native codex.exe. See the agent workflows guide.", 409);
        var home = Path.Combine(options.RepositoryRoot, ".local", "portal", "agents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        // New per-browser home: do not copy desktop auth or inherit desktop plugins, MCP servers or hooks.
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), "model = \"gpt-6-astra\"\nmodel_reasoning_effort = \"high\"\nservice_tier = \"default\"\ncli_auth_credentials_store = \"file\"\nweb_search = \"disabled\"\nproject_doc_max_bytes = 0\n", ct);
        var start = new ProcessStartInfo(exe) { WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        start.Environment["CODEX_HOME"] = home;
        foreach (var key in start.Environment.Keys.Where(k => k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) || k.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && k != "CODEX_HOME").ToArray()) start.Environment.Remove(key);
        var runtime = new CodexAgentRuntime(Process.Start(start) ?? throw new PortalException("Codex could not start.", 503), home);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, "process.json"), JsonSerializer.Serialize(new { pid = runtime.process.Id, parentPid = Environment.ProcessId,
                executable = exe, startedUtc = runtime.process.StartTime.ToUniversalTime() }), ct);
            await runtime.Call("initialize", new { clientInfo = new { name = "platform_studio", title = "Platform Studio", version = "1.0.0" }, capabilities = new { experimentalApi = true } }, ct);
            await runtime.Write(new { method = "initialized" }, ct);
            return runtime;
        }
        catch { await runtime.DisposeAsync(); throw; }
    }
    public async Task<object> Status(CancellationToken ct)
    {
        var account = await Call("account/read", new { refreshToken = false }, ct);
        if (!account.TryGetProperty("account", out var a) || a.ValueKind == JsonValueKind.Null)
            return new { ready = false, state = LoginId is null ? "Connect Codex" : "Waiting for Codex browser sign-in", model = AgentPolicy.Model, effort = AgentPolicy.Effort, speed = "Standard" };
        if (a.GetProperty("type").GetString() != "chatgpt") throw new PortalException("Use ChatGPT sign-in for Codex. API-key authentication is not enabled.", 409);
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var models = await Call("model/list", new { cursor, limit = 100, includeHidden = false }, ct);
            var model = models.GetProperty("data").EnumerateArray().FirstOrDefault(m => m.GetProperty("model").GetString() == AgentPolicy.Model);
            if (model.ValueKind != JsonValueKind.Undefined)
            {
                AgentPolicy.ValidateModel(model); LoginId = null;
                return new { ready = true, state = "Ready", model = AgentPolicy.Model, effort = AgentPolicy.Effort, speed = "Standard" };
            }
            cursor = models.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is null) break;
        }
        throw new PortalException("GPT-6 Astra is not available to this Codex account. No fallback is allowed.", 409);
    }
    public async Task<string> Login(CancellationToken ct)
    {
        if (LoginId is not null) throw new PortalException("Codex sign-in is already pending. Finish or cancel it first.", 409);
        var result = await Call("account/login/start", new { type = "chatgpt", useHostedLoginSuccessPage = true }, ct);
        LoginId = result.GetProperty("loginId").GetString();
        return AgentPolicy.LoginUri(result.GetProperty("authUrl").GetString()!).AbsoluteUri;
    }
    public async Task CancelLogin(CancellationToken ct)
    {
        if (LoginId is not null) await Call("account/login/cancel", new { loginId = LoginId }, ct);
        LoginId = null;
    }
    public async Task<string> Review(string skillPath, string skillName, object[] tools, CancellationToken ct, bool structured = false)
    {
        var status = JsonSerializer.SerializeToElement(await Status(ct));
        if (!status.GetProperty("ready").GetBoolean()) throw new PortalException("Connect Codex before running an agent workflow.", 401);
        // Any inherited MCP server is forbidden: our only tool path is the per-run in-memory bridge.
        var servers = await Call("mcpServerStatus/list", new { limit = 100 }, ct);
        if (servers.GetProperty("data").GetArrayLength() != 0 || servers.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String)
            throw new PortalException("Unexpected Codex MCP configuration. Workflow blocked.", 409);
        var thread = await Call("thread/start", new { model = AgentPolicy.Model, serviceTier = AgentPolicy.Tier,
            cwd = Home, runtimeWorkspaceRoots = Array.Empty<string>(), approvalPolicy = "never", approvalsReviewer = "user", sandbox = "read-only",
            ephemeral = true, allowProviderModelFallback = false, personality = "none", environments = Array.Empty<object>(), selectedCapabilityRoots = Array.Empty<object>(),
            config = AgentPolicy.Isolation, developerInstructions = AgentPolicy.Instructions, dynamicTools = tools }, ct);
        AgentPolicy.ValidateThread(thread);
        activeThread = thread.GetProperty("thread").GetProperty("id").GetString();
        finalText = ""; turnFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Call("turn/start", new { threadId = activeThread, model = AgentPolicy.Model, effort = AgentPolicy.Effort,
                serviceTier = AgentPolicy.Tier, serviceTierForTurn = AgentPolicy.Tier,
                input = new object[] { new { type = "text", text = "Review the selected platform workflow. First call platform_skill and platform_evidence; apply the pinned skill within read-only limits. " + (structured ? "Return only the skill's strict JSON review contract, not Markdown." : "Return Markdown evidence review; do not execute scripts.") }, new { type = "skill", name = skillName, path = skillPath } } }, ct);
            return await turnFinished.Task.WaitAsync(ct);
        }
        finally { activeThread = null; ToolHandler = null; }
    }
    async Task DrainErrors() { try { var buffer = new char[4096]; while (await process.StandardError.ReadAsync(buffer, lifetime.Token) > 0) { } } catch { } }
    async Task Read()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(lifetime.Token);
                if (line is null) break;
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException();
                using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var id))
                    {
                        if (method.GetString() == "item/tool/call" && ToolHandler is not null && message.GetProperty("params").GetProperty("threadId").GetString() == activeThread)
                        {
                            var result = await ToolHandler(message.GetProperty("params").Clone(), lifetime.Token);
                            await Write(new { id = id.Clone(), result }, lifetime.Token);
                        }
                        else await Write(new { id = id.Clone(), error = new { code = -32601, message = "This agent host permits only its scoped read-only tools." } }, lifetime.Token);
                    }
                    else if (message.TryGetProperty("params", out var p))
                    {
                        if (method.GetString() == "account/login/completed") LoginId = null;
                        if (p.TryGetProperty("threadId", out var t) && t.GetString() == activeThread)
                        {
                            if (method.GetString() == "item/completed" && p.GetProperty("item").GetProperty("type").GetString() == "agentMessage")
                            { finalText = p.GetProperty("item").GetProperty("text").GetString() ?? ""; if (finalText.Length > 100_000) throw new InvalidDataException(); }
                            if (method.GetString() == "turn/completed")
                            {
                                if (p.GetProperty("turn").GetProperty("status").GetString() == "completed" && finalText.Length > 0) turnFinished?.TrySetResult(finalText);
                                else turnFinished?.TrySetException(new PortalException("Codex did not complete the review. No success or approval is inferred.", 502));
                            }
                        }
                    }
                }
                else if (message.TryGetProperty("id", out var responseId) && responseId.TryGetInt64(out var number) && pending.TryRemove(number, out var promise))
                {
                    if (message.TryGetProperty("result", out var result)) promise.TrySetResult(result.Clone());
                    else promise.TrySetException(new PortalException("Codex rejected the protocol request. Check the installed runtime and account availability.", 502));
                }
            }
        }
        catch { /* Never relay raw protocol frames into logs or HTTP errors. */ }
        finally
        {
            foreach (var pair in pending) pair.Value.TrySetException(new PortalException("Codex agent host disconnected.", 503));
            turnFinished?.TrySetException(new PortalException("Codex agent host disconnected.", 503));
        }
    }
    public async Task<JsonElement> Call(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref sequence); var promise = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = promise;
        try { await Write(new { id, method, @params = parameters }, ct); return await promise.Task.WaitAsync(TimeSpan.FromSeconds(45), ct); }
        catch (TimeoutException) { throw new PortalException("Codex protocol request timed out. Disconnect and reconnect Codex.", 504); }
        finally { pending.TryRemove(id, out _); }
    }
    async Task Write(object value, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct); await process.StandardInput.FlushAsync(ct); }
        finally { writes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        process.Dispose();
        // Only this runtime's credential file, never desktop credentials or arbitrary paths.
        try { File.Delete(Path.Combine(Home, "auth.json")); File.Delete(Path.Combine(Home, "process.json")); } catch (IOException) { }
    }
}
