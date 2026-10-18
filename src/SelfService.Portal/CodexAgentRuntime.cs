using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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
    public string? RuntimeVersion { get; private set; }
    public string? LoginId { get; private set; }
    public Func<JsonElement, CancellationToken, Task<object>>? ToolHandler { get; set; }
    public bool Alive => !process.HasExited;
    public bool InferenceAttempted { get; private set; }
    public bool TurnInProgress { get; private set; }

    CodexAgentRuntime(Process child, string home)
    {
        process = child; Home = home;
        reader = Read();
        _ = DrainErrors(); // Never persist raw provider errors, credentials or prompt fragments.
    }
    public static string? FindExecutable(string configured, string? searchPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && Path.GetFileName(configured).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured) ? configured : null;
        var directories = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Directory.Exists).ToArray();
        // Match native executable resolution before considering npm's nested vendor fallback.
        // An earlier npm shim directory must not shadow a newer native Codex on PATH.
        foreach (var dir in directories)
        {
            var direct = Path.Combine(dir, "codex.exe");
            if (File.Exists(direct)) return direct;
        }
        foreach (var dir in directories)
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            var triple = arch == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            var npm = Path.Combine(dir, "node_modules", "@openai", "codex", "node_modules", "@openai", $"codex-win32-{arch}", "vendor", triple, "bin", "codex.exe");
            if (File.Exists(npm)) return npm;
        }
        return null;
    }
    public static void ValidateRestoredAuthentication(byte[] authentication)
    {
        try
        {
            if (authentication.Length is <= 0 or > 64 * 1024) throw new InvalidOperationException();
            using var document = JsonDocument.Parse(authentication, new JsonDocumentOptions { MaxDepth = 8 });
            var auth = document.RootElement;
            if (auth.ValueKind != JsonValueKind.Object ||
                auth.TryGetProperty("auth_mode", out var mode) && (mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt") ||
                auth.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind != JsonValueKind.Null ||
                !auth.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException();
            foreach (var name in new[] { "id_token", "access_token", "refresh_token" })
                if (!tokens.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new InvalidOperationException();
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new InvalidOperationException();
                        Unique(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Unique(value);
            }
            Unique(auth);
        }
        catch { throw new PortalException("Saved Codex authentication is invalid or unsupported. Connect Codex again.", 409); }
    }
    // Restore only a host-supplied credential cache. Configuration and tool policy are always rebuilt.
    public static string PrepareHome(PortalOptions options, byte[]? restoredAuthentication = null)
    {
        if (restoredAuthentication is not null) ValidateRestoredAuthentication(restoredAuthentication);
        var root = Path.GetFullPath(Path.Combine(options.RepositoryRoot, ".local", "portal", "agents"));
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new PortalException("Codex runtime home cannot use a linked directory.", 409);
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(home).SetAccessControl(acl);
            }
            else File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(home, "config.toml"), AgentPolicy.BootstrapConfiguration, new UTF8Encoding(false));
            if (restoredAuthentication is not null) File.WriteAllBytes(Path.Combine(home, "auth.json"), restoredAuthentication);
            return home;
        }
        catch
        {
            File.Delete(Path.Combine(home, "auth.json"));
            throw;
        }
    }
    public static async Task<CodexAgentRuntime> Start(PortalOptions options, CancellationToken ct, byte[]? restoredAuthentication = null)
    {
        var exe = FindExecutable(options.CodexPath) ?? throw new PortalException("Install Codex CLI or configure Portal:CodexPath to its native codex.exe. See the agent workflows guide.", 409);
        ct.ThrowIfCancellationRequested();
        // New per-browser home: never copy desktop configuration, plugins, MCP servers or hooks.
        var home = PrepareHome(options, restoredAuthentication);
        CodexAgentRuntime? runtime = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(exe) { WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
            ConfigureEnvironment(start.Environment, home);
            runtime = new CodexAgentRuntime(Process.Start(start) ?? throw new PortalException("Codex could not start.", 503), home);
            await File.WriteAllTextAsync(Path.Combine(home, "process.json"), JsonSerializer.Serialize(new { pid = runtime.process.Id, parentPid = Environment.ProcessId,
                executable = exe, startedUtc = runtime.process.StartTime.ToUniversalTime() }), ct);
            var initialized = await runtime.Call("initialize", new { clientInfo = new { name = "platform_studio", title = "Platform Studio", version = "1.0.0" }, capabilities = new { experimentalApi = true } }, ct);
            if (initialized.TryGetProperty("userAgent", out var userAgent))
            {
                var version = System.Text.RegularExpressions.Regex.Match(userAgent.GetString() ?? "", @"\b\d+\.\d+\.\d+\b");
                if (version.Success) runtime.RuntimeVersion = version.Value;
            }
            await runtime.Write(new { method = "initialized" }, ct);
            return runtime;
        }
        catch
        {
            if (runtime is not null) await runtime.DisposeAsync();
            else File.Delete(Path.Combine(home, "auth.json"));
            throw;
        }
    }
    // The inference child receives OS paths only. Provider credentials belong to the
    // session-owned MCP broker, never ambient process inheritance.
    public static void ConfigureEnvironment(IDictionary<string, string?> environment, string home)
    {
        var allowed = new HashSet<string>(["PATH", "PATHEXT", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH"], StringComparer.OrdinalIgnoreCase);
        foreach (var key in environment.Keys.Where(k => !allowed.Contains(k)).ToArray()) environment.Remove(key);
        environment["CODEX_HOME"] = home;
    }
    public async Task<object> Status(CancellationToken ct)
    {
        var status = await ReadStatus(Call, LoginId is not null, RuntimeVersion, ct);
        if (JsonSerializer.SerializeToElement(status).GetProperty("authenticated").GetBoolean()) LoginId = null;
        return status;
    }
    public static async Task<object> ReadRestoredStatus(Func<string, object, CancellationToken, Task<JsonElement>> call, string? runtimeVersion, CancellationToken ct)
    {
        var status = JsonSerializer.SerializeToElement(await ReadStatus(call, false, runtimeVersion, ct));
        if (!status.GetProperty("authenticated").GetBoolean() || !status.GetProperty("ready").GetBoolean())
            throw new PortalException("Saved Codex sign-in could not restore the required account and model readiness. Connect Codex again.", 409);
        return status;
    }
    // Shared by the live protocol path and deterministic catalog/authentication regression tests.
    public static async Task<object> ReadStatus(Func<string, object, CancellationToken, Task<JsonElement>> call, bool loginPending, string? runtimeVersion, CancellationToken ct)
    {
        object Result(bool authenticated, bool ready, string state) => new { authenticated, ready, state, runtimeVersion, model = AgentPolicy.Model, effort = AgentPolicy.Effort, speed = AgentPolicy.Speed };
        var account = await call("account/read", new { refreshToken = false }, ct);
        if (!account.TryGetProperty("account", out var a) || a.ValueKind == JsonValueKind.Null)
            return Result(false, false, loginPending ? "Waiting for Codex browser sign-in" : "Connect Codex");
        if (a.GetProperty("type").GetString() != "chatgpt") return Result(false, false, "Use ChatGPT sign-in for Codex. API-key authentication is not enabled.");
        try
        {
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var models = await call("model/list", new { cursor, limit = 100, includeHidden = false }, ct);
            var model = models.GetProperty("data").EnumerateArray().FirstOrDefault(m => m.GetProperty("model").GetString() == AgentPolicy.Model);
            if (model.ValueKind != JsonValueKind.Undefined)
            {
                AgentPolicy.ValidateModel(model);
                return Result(true, true, "Signed in to ChatGPT · Model ready");
            }
            cursor = models.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is null) break;
        }
        return Result(true, false, $"Signed in to ChatGPT, but this runtime's catalog does not list {AgentPolicy.DisplayName}. Update Codex or configure Portal:CodexPath, then reconnect. If it persists, check account availability. No fallback is allowed.");
        }
        catch (PortalException)
        {
            return Result(true, false, "Signed in to ChatGPT, but the requested model settings could not be verified. Refresh readiness; check the Codex runtime and account if this persists. No fallback is allowed.");
        }
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
    public async Task<string> Review(string skillPath, string skillName, object[] tools, CancellationToken ct, bool structured = false, bool orchestration = false)
    {
        InferenceAttempted = false;
        var status = JsonSerializer.SerializeToElement(await Status(ct));
        if (!status.GetProperty("ready").GetBoolean()) throw new PortalException("Connect Codex before running an agent workflow.", 401);
        var configuration = await Call("config/read", new { cwd = Home, includeLayers = false }, ct);
        AgentPolicy.ValidateToolOutputBudget(configuration);
        var serverNames = configuration.GetProperty("config").TryGetProperty("mcp_servers", out var configured) && configured.ValueKind == JsonValueKind.Object
            ? configured.EnumerateObject().Select(p => p.Name).ToArray() : [];
        var thread = await Call("thread/start", new { model = AgentPolicy.Model, serviceTier = AgentPolicy.Tier,
            cwd = Home, runtimeWorkspaceRoots = Array.Empty<string>(), approvalPolicy = "never", approvalsReviewer = "user", sandbox = "read-only",
            ephemeral = true, allowProviderModelFallback = false, personality = "none", environments = Array.Empty<object>(), selectedCapabilityRoots = Array.Empty<object>(),
            config = AgentPolicy.IsolatedThreadConfiguration(serverNames), developerInstructions = orchestration ? AgentPolicy.OrchestrationInstructions : AgentPolicy.Instructions,
            dynamicTools = new[] { new { type = "namespace", name = AgentPolicy.ToolNamespace, description = "Platform Studio's host-mediated evidence and authorized provider broker tools.", tools } } }, ct);
        AgentPolicy.ValidateThread(thread);
        activeThread = thread.GetProperty("thread").GetProperty("id").GetString();
        finalText = ""; turnFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await VerifyMcpIsolation(Call, activeThread!, ct);
            InferenceAttempted = true; // A failed configuration preflight must not discard a valid login.
            TurnInProgress = true;
            await Call("turn/start", new { threadId = activeThread, model = AgentPolicy.Model, effort = AgentPolicy.Effort,
                serviceTier = AgentPolicy.Tier, serviceTierForTurn = AgentPolicy.Tier,
                input = new object[] { new { type = "text", text = orchestration ? "Read platform_skill and platform_evidence. Complete the requested task using connected MCP providers through the broker. Discover exact schemas first. Execute useful reads, propose concrete changes as exact plans, and execute only plans the human has already approved. Report plan IDs and observed receipts." : "Review the selected platform workflow. First call platform_skill and platform_evidence; apply the pinned skill within read-only limits. " + (structured ? "Return only the skill's strict JSON review contract, not Markdown." : "Return Markdown evidence review; do not execute scripts.") }, new { type = "skill", name = skillName, path = skillPath } } }, ct);
            return await turnFinished.Task.WaitAsync(ct);
        }
        finally { activeThread = null; ToolHandler = null; }
    }
    public static async Task VerifyMcpIsolation(Func<string, object, CancellationToken, Task<JsonElement>> call, string threadId, CancellationToken ct)
    {
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 20; page++)
        {
            var result = await call("mcpServerStatus/list", new { threadId, cursor, limit = 100, detail = "full" }, ct);
            AgentPolicy.ValidateMcpIsolation(result);
            if (!result.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null) return;
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(cursor = next.GetString()) || !cursors.Add(cursor)) break;
        }
        throw new PortalException("Codex MCP inventory exceeded its verification limit. Workflow blocked.", 409);
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
                                TurnInProgress = false;
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
