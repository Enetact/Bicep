using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record McpStep(string Provider, string Tool, JsonElement Arguments);
public record McpPlanRequest(McpStep[] Steps);
public record McpPlanConsent(string Digest);
public interface IProviderConnectionResolver { Task<ProviderConnectionSpec> Resolve(BrowserSession session, string provider, CancellationToken ct); }

public sealed class McpProviderConnection : IAsyncDisposable
{
    public string Id { get; init; } = "";
    public string Generation { get; } = Guid.NewGuid().ToString("N");
    public string Identity { get; init; } = "";
    public string State { get; set; } = "stopped";
    public string? Error { get; set; }
    public string CatalogDigest { get; set; } = "";
    public ProviderTool[] Tools { get; set; } = [];
    public IProviderMcpClient? Client { get; set; }
    public CancellationTokenSource Lifetime { get; } = new();
    public async ValueTask DisposeAsync() { Lifetime.Cancel(); if (Client is not null) await Client.DisposeAsync(); Client = null; Lifetime.Dispose(); }
}
public sealed class McpExecutionPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Digest { get; init; } = "";
    public string State { get; set; } = "Ready";
    public bool RequiresApproval { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public McpStep[] Steps { get; init; } = [];
    public Dictionary<string, string> Bindings { get; init; } = [];
    public List<object> Receipts { get; } = [];
    public DateTimeOffset? ApprovedUtc { get; set; }
}
public sealed class McpProviderSession : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ConcurrentDictionary<string, McpProviderConnection> Connections { get; } = new();
    public ConcurrentDictionary<string, McpExecutionPlan> Plans { get; } = new();
    public CancellationTokenSource Lifetime { get; } = new();
    public async ValueTask DisposeAsync()
    {
        Lifetime.Cancel();
        foreach (var connection in Connections.Values) await connection.DisposeAsync();
        Connections.Clear(); Plans.Clear();
    }
}

// Full provider catalogs remain behind browser-owned connections. Model proposals
// cannot mint approval: the separate human route approves an immutable exact plan.
public sealed class McpProviderBroker(PortalOptions options, IProviderConnectionResolver resolver, IProviderMcpFactory factory)
{
    public static readonly (string Id, string Name)[] Providers = [("bicep", "Azure Bicep"), ("azure", "Microsoft Azure"), ("ado", "Azure DevOps"), ("github", "GitHub"), ("learn", "Microsoft Learn")];
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Regex Secrets = new("(?i)(password|client.?secret|access.?token|refresh.?token|authorization|private.?key|connection.?string|personal.?access.?token|api.?key|account.?key|shared.?access.?signature|sas.?token|(^|_)(gh|github|ado|mcp)_?token|devops.*pat|^(token|secret|apiToken|pat|pwd)$)", RegexOptions.CultureInvariant);
    static readonly Regex Mutation = new("(?i)(create|update|delete|remove|set_|write|execute|run_|queue|approve|merge|push|deploy|assign|enable|disable|rotate|format|decompile|import|export|install|upload|download|send|add_|patch|put_|post_|invoke|command)", RegexOptions.CultureInvariant);
    static readonly Regex ReadName = new("(?i)(^|_)(get|list|search|fetch|read|show|find|query|check|validate|inspect|diagnostics|best_practices|metadata)(_|$)", RegexOptions.CultureInvariant);
    public static string Identity(BrowserSession s, string id) => id switch
    {
        "azure" => s.Connected.ContainsKey("azure") && s.Cli is not null ? $"{s.MicrosoftIdentityEpoch}:{s.AzureTenantId}:{s.Cli.DirectoryPath}" : "",
        "ado" => s.Connected.ContainsKey("ado") ? $"{s.MicrosoftIdentityEpoch}:{s.AzureTenantId}:{s.Account?.HomeAccountId.Identifier ?? s.Cli?.AccountId}" : "",
        "github" => s.GitHub.UserId is not null ? $"{s.GitHub.Epoch}:{s.GitHub.UserId}" : "",
        "bicep" => "browser:" + s.Mcp.Id + ":" + s.MicrosoftIdentityEpoch,
        "learn" => "browser:" + s.Mcp.Id,
        _ => throw new PortalException("Unknown MCP provider.", 404)
    };
    public static string Effect(string provider, ProviderTool tool)
    {
        if (tool.SensitiveHint || Secrets.IsMatch(tool.Name) || Regex.IsMatch(tool.Name, "(?i)(list.?keys|credential|secret|token)")) return "Sensitive";
        if (provider == "learn" && tool.Name is "microsoft_docs_search" or "microsoft_docs_fetch" or "microsoft_code_sample_search") return "Read";
        if (!Mutation.IsMatch(tool.Name) && (tool.ReadOnly == true && tool.Destructive != true && ReadName.IsMatch(tool.Name) || provider == "bicep" && tool.Name is "get_bicep_best_practices" or "list_azure_resource_types" or "get_azure_resource_type_schema" or "list_extension_resource_types" or "get_extension_resource_type_schema" or "list_well_known_extensions" or "list_avm_metadata" or "get_bicep_file_diagnostics" or "get_file_references")) return "Read";
        return "ChangeOrUnknown";
    }
    static object ToolView(string provider, ProviderTool t) => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema, effect = Effect(provider, t), readOnlyHint = t.ReadOnly, destructiveHint = t.Destructive, sensitiveHint = t.SensitiveHint };
    public object Status(BrowserSession s, bool includeTools = true) => new { providers = Providers.Select(p =>
    {
        s.Mcp.Connections.TryGetValue(p.Id, out var c);
        var valid = c is not null && c.Identity == Identity(s, p.Id);
        return new { id = p.Id, name = p.Name, state = c is null ? "stopped" : !valid ? "authRequired" : c.State, connected = valid && c!.State == "ready", error = !valid && c is not null ? "Reconnect this provider after signing in." : c?.Error, toolCount = valid ? c?.Tools.Length ?? 0 : 0, catalogDigest = valid ? c?.CatalogDigest : null, tools = valid && includeTools ? c!.Tools.Select(t => ToolView(p.Id, t)).ToArray() : [] };
    }).ToArray(), plans = Plans(s) };
    public static object[] Plans(BrowserSession s) => s.Mcp.Plans.Values.OrderByDescending(p => p.ExpiresUtc).Select(p => (object)new { p.Id, p.Digest, p.State, p.RequiresApproval, p.ExpiresUtc, stepCount = p.Steps.Length, steps = p.Steps.Select(x => new { x.Provider, x.Tool }).ToArray() }).ToArray();
    public static object View(McpExecutionPlan p) => new { p.Id, p.Digest, p.State, p.RequiresApproval, p.ExpiresUtc, p.ApprovedUtc, p.Steps, receipts = p.Receipts.ToArray() };
    public async Task<object> Connect(BrowserSession s, string id, CancellationToken ct)
    {
        _ = Providers.SingleOrDefault(p => p.Id == id).Name ?? throw new PortalException("Unknown MCP provider.", 404);
        if (!await s.Mcp.Gate.WaitAsync(0, ct)) throw new PortalException("Another MCP operation is active.", 409);
        try
        {
            if (s.Mcp.Connections.TryRemove(id, out var previous)) await previous.DisposeAsync();
            var identity = Identity(s, id);
            var c = new McpProviderConnection { Id = id, Identity = identity, State = "starting" }; s.Mcp.Connections[id] = c;
            try
            {
                if (identity.Length == 0) throw new PortalException(id == "azure" ? "Connect Azure using the isolated local CLI mode first." : $"Connect {id} first.", 401);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Mcp.Lifetime.Token, c.Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var spec = await resolver.Resolve(s, id, timeout.Token);
                c.Client = await factory.Connect(spec, timeout.Token);
                var tools = await c.Client.ListTools(timeout.Token);
                if (tools.Length is 0 or > 3000 || tools.Any(t => !Regex.IsMatch(t.Name, "^[a-zA-Z0-9_.:/-]{1,200}$") || t.InputSchema.ValueKind != JsonValueKind.Object) || tools.Select(t => t.Name).Distinct().Count() != tools.Length)
                    throw new PortalException("Provider tool inventory was empty, excessive or invalid.", 502);
                if (identity != Identity(s, id)) throw new PortalException("Identity changed during MCP connection.", 401);
                c.Tools = tools; c.CatalogDigest = Digest(JsonSerializer.SerializeToElement(tools.OrderBy(t => t.Name).ToArray(), Json)); c.State = "ready";
            }
            catch (Exception e)
            {
                if (c.Client is not null) await c.Client.DisposeAsync(); c.Client = null;
                c.State = e is PortalException { Status: 401 } ? "authRequired" : "error";
                c.Error = e is PortalException p ? p.Message : "Provider connection failed or timed out. Check its installation, credentials and connectivity.";
            }
            return Status(s);
        }
        finally { s.Mcp.Gate.Release(); }
    }
    public async Task<object> Disconnect(BrowserSession s, string id, CancellationToken ct)
    {
        if (s.Mcp.Connections.TryGetValue(id, out var active)) active.Lifetime.Cancel();
        await s.Mcp.Gate.WaitAsync(ct);
        try { if (s.Mcp.Connections.TryRemove(id, out var c)) await c.DisposeAsync(); return Status(s); }
        finally { s.Mcp.Gate.Release(); }
    }
    McpProviderConnection Connection(BrowserSession s, string id)
    {
        if (!s.Mcp.Connections.TryGetValue(id, out var c) || c.State != "ready" || c.Client is null || c.Lifetime.IsCancellationRequested || c.Identity.Length == 0 || c.Identity != Identity(s, id))
            throw new PortalException($"Reconnect MCP provider {id}; authorization or catalog is unavailable.", 401);
        return c;
    }
    public object Catalog(BrowserSession s, string? provider = null, string? search = null, int offset = 0, int limit = 20)
    {
        if (offset < 0 || limit is < 1 or > 30 || search?.Length > 100) throw new PortalException("Invalid catalog page.");
        var rows = s.Mcp.Connections.Values.Where(c => c.State == "ready" && c.Identity == Identity(s, c.Id) && (provider is null || c.Id == provider))
            .SelectMany(c => c.Tools.Select(t => new { provider = c.Id, tool = t })).Where(x => search is null || x.tool.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (x.tool.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).OrderBy(x => x.provider).ThenBy(x => x.tool.Name).ToArray();
        return new { total = rows.Length, offset, tools = rows.Skip(offset).Take(limit).Select(x => new { x.provider, definition = ToolView(x.provider, x.tool) }) };
    }
    public McpExecutionPlan Plan(BrowserSession s, McpPlanRequest request)
    {
        if (request.Steps is not { Length: > 0 and <= 20 }) throw new PortalException("Choose between one and twenty ordered tool steps.");
        foreach (var step in request.Steps) ValidateArguments(step.Arguments);
        var requestedSteps = request.Steps.Select(step => step.Provider == "bicep" ? step with { Arguments = NormalizeBicepArguments(step.Arguments) } : step).ToArray();
        foreach (var old in s.Mcp.Plans.Where(p => p.Value.ExpiresUtc < DateTimeOffset.UtcNow).ToArray()) s.Mcp.Plans.TryRemove(old.Key, out _);
        if (s.Mcp.Plans.Count >= 40) throw new PortalException("Too many saved tool plans. Wait for older plans to expire.", 429);
        var bindings = new Dictionary<string, string>(); var approval = false;
        foreach (var step in requestedSteps)
        {
            var c = Connection(s, step.Provider);
            var t = c.Tools.SingleOrDefault(t => t.Name == step.Tool) ?? throw new PortalException("Tool is not in this provider's discovered catalog.");
            ValidateArguments(step.Arguments); ValidateBicepPaths(s, step);
            if (t.InputSchema.TryGetProperty("required", out var required)) foreach (var field in required.EnumerateArray()) if (!step.Arguments.TryGetProperty(field.GetString()!, out _)) throw new PortalException("A required tool argument is missing.");
            approval |= Effect(step.Provider, t) != "Read";
            bindings[step.Provider] = c.Generation + ":" + c.CatalogDigest + ":" + c.Identity;
        }
        var steps = requestedSteps.Select(x => x with { Arguments = x.Arguments.Clone() }).ToArray();
        var digest = Digest(JsonSerializer.SerializeToElement(new { session = s.Mcp.Id, steps, bindings = bindings.OrderBy(p => p.Key).ToArray() }, Json));
        var plan = new McpExecutionPlan { Digest = digest, Steps = steps, Bindings = bindings, RequiresApproval = approval, State = approval ? "AwaitingApproval" : "Ready" };
        s.Mcp.Plans[plan.Id] = plan; return plan;
    }
    public static void ValidateArguments(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(args.GetRawText()) > 64 * 1024) throw new PortalException("Tool arguments must be an object smaller than 64 KiB.");
        void Walk(JsonElement value, int depth)
        {
            if (depth > 24) throw new PortalException("Tool arguments are too deeply nested.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>();
                foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name) || Secrets.IsMatch(p.Name)) throw new PortalException("Credentials and duplicate keys are not permitted in tool arguments. Use the provider connection."); Walk(p.Value, depth + 1); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var x in value.EnumerateArray()) Walk(x, depth + 1);
        }
        Walk(args, 0);
    }
    void ValidateBicepPaths(BrowserSession s, McpStep step)
    {
        if (step.Provider != "bicep") return;
        void Walk(JsonElement value, string key = "")
        {
            if (value.ValueKind == JsonValueKind.Object) foreach (var p in value.EnumerateObject()) Walk(p.Value, p.Name);
            else if (value.ValueKind == JsonValueKind.Array) foreach (var x in value.EnumerateArray()) Walk(x, key);
            else if (value.ValueKind == JsonValueKind.String && Regex.IsMatch(key, "(?i)(path|file|directory|folder)"))
            {
                var text = value.GetString()!; if (text.Length == 0) return;
                ValidateBicepPath(text);
            }
        }
        Walk(step.Arguments);
    }
    void ValidateBicepPath(string text)
    {
        var root = Path.GetFullPath(options.RepositoryRoot); var full = Path.GetFullPath(text, root);
        var relative = Path.GetRelativePath(root, full);
        var leaf = Path.GetFileName(full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.ToLowerInvariant() is ".git" or ".local" or ".tools" or "artifacts" or ".azure" or ".aws" or ".codex") || leaf.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || leaf.Equals("auth.json", StringComparison.OrdinalIgnoreCase) || leaf.Equals("hosts.yml", StringComparison.OrdinalIgnoreCase) || leaf.Equals("portal.local.json", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("local.settings.", StringComparison.OrdinalIgnoreCase)) throw new PortalException("Bicep file tools are confined to repository source paths; credentials and runtime directories are excluded.", 403);
        for (var d = new DirectoryInfo(Path.GetDirectoryName(full)!); d is not null && d.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); d = d.Parent) if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new PortalException("Linked Bicep source paths are not permitted.", 403);
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new PortalException("Linked Bicep files are not permitted.", 403);
    }
    JsonElement NormalizeBicepArguments(JsonElement args)
    {
        object? Walk(JsonElement value, string key) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Walk(p.Value, p.Name)),
            JsonValueKind.Array => value.EnumerateArray().Select(v => Walk(v, key)).ToArray(),
            JsonValueKind.String when Regex.IsMatch(key, "(?i)(path|file|directory|folder)") && !Regex.IsMatch(key, "(?i)(content|text)") && value.GetString()!.Length > 0 => Path.GetFullPath(value.GetString()!, options.RepositoryRoot),
            _ => value.Clone()
        };
        try { return JsonSerializer.SerializeToElement(Walk(args, ""), Json); }
        catch (ArgumentException) { throw new PortalException("Invalid Bicep source path."); }
    }
    void ValidateBindings(BrowserSession s, McpExecutionPlan p, string digest)
    {
        if (p.ExpiresUtc < DateTimeOffset.UtcNow || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p.Digest), Encoding.UTF8.GetBytes(digest))) throw new PortalException("Tool plan expired or changed. Review it again.", 409);
        foreach (var binding in p.Bindings)
        {
            var c = Connection(s, binding.Key);
            if (binding.Value != c.Generation + ":" + c.CatalogDigest + ":" + c.Identity) throw new PortalException("Provider identity or tool catalog changed. Review a new plan.", 409);
        }
    }
    public McpExecutionPlan ReadPlan(BrowserSession s, string id) => s.Mcp.Plans.TryGetValue(id, out var p) ? p : throw new PortalException("Tool plan was not found in this browser session.", 404);
    public object Approve(BrowserSession s, string id, string digest)
    {
        var p = ReadPlan(s, id); lock (p)
        {
            ValidateBindings(s, p, digest);
            if (p.State != "AwaitingApproval") throw new PortalException("This plan is not awaiting approval.", 409);
            p.ApprovedUtc = DateTimeOffset.UtcNow; p.State = "Approved"; return View(p);
        }
    }
    public async Task<object> Run(BrowserSession s, string id, string digest, CancellationToken ct)
    {
        var p = ReadPlan(s, id);
        if (!await s.Mcp.Gate.WaitAsync(0, ct)) throw new PortalException("Another MCP operation is active.", 409);
        try
        {
            // Refresh schemas before an approval can be consumed. Tool annotations
            // and contracts may change even while the transport remains connected.
            foreach (var provider in p.Bindings.Keys)
            {
                var c = Connection(s, provider);
                using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct, c.Lifetime.Token, s.Mcp.Lifetime.Token); refresh.CancelAfter(TimeSpan.FromSeconds(60));
                var tools = await c.Client!.ListTools(refresh.Token);
                var current = Digest(JsonSerializer.SerializeToElement(tools.OrderBy(t => t.Name).ToArray(), Json));
                if (current != c.CatalogDigest) { c.Tools = tools; c.CatalogDigest = current; throw new PortalException("Provider tool schemas changed. Create and approve a new plan.", 409); }
            }
            lock (p)
            {
                ValidateBindings(s, p, digest);
                if (p.State != "Ready" && p.State != "Approved" || p.RequiresApproval && p.ApprovedUtc is null) throw new PortalException("This exact plan needs approval or has already been attempted.", 409);
                p.State = "Running";
            }
            foreach (var step in p.Steps)
            {
                ValidateBindings(s, p, digest); ValidateBicepPaths(s, step);
                var c = Connection(s, step.Provider); var tool = c.Tools.Single(t => t.Name == step.Tool); var started = DateTimeOffset.UtcNow;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Mcp.Lifetime.Token, c.Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
                var attempted = false;
                try
                {
                    if (step.Provider == "bicep") await BicepMcpScope.ValidateDependencies(c.Client!, step.Tool, step.Arguments, ValidateBicepPath, timeout.Token);
                    ValidateBindings(s, p, digest);
                    attempted = true;
                    var raw = p.RequiresApproval && p.ApprovedUtc is not null ? await c.Client!.CallApproved(step.Tool, step.Arguments, timeout.Token) : await c.Client!.Call(step.Tool, step.Arguments, timeout.Token);
                    ValidateBindings(s, p, digest);
                    if (step.Provider == "bicep") await BicepMcpScope.ValidateDependencies(c.Client!, step.Tool, step.Arguments, ValidateBicepPath, timeout.Token);
                    var failed = raw.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                    var digestResult = Digest(raw);
                    var output = Effect(step.Provider, tool) == "Sensitive" ? JsonSerializer.SerializeToElement(new { withheld = true, reason = "Sensitive output is kept out of model context and receipts.", sha256 = digestResult }) : Redact(raw);
                    lock(p) p.Receipts.Add(new { index = p.Receipts.Count, provider = step.Provider, tool = step.Tool, startedUtc = started, completedUtc = DateTimeOffset.UtcNow, status = failed ? "Failed" : "Succeeded", resultSha256 = digestResult, output });
                    if (failed) { p.State = "Failed"; break; }
                    ValidateBindings(s, p, digest);
                }
                catch
                {
                    if (attempted) { c.State = "error"; c.Error = "Provider call failed or authorization changed. Reconnect before another plan."; }
                    p.State = !attempted || Effect(step.Provider, tool) == "Read" ? "Failed" : "UnknownOutcome";
                    lock(p) p.Receipts.Add(new { index = p.Receipts.Count, provider = step.Provider, tool = step.Tool, startedUtc = started, status = p.State, error = "Provider call failed, timed out or authorization changed. Reconcile external state before retrying a change." });
                    break;
                }
            }
            if (p.State == "Running") p.State = "Completed";
            await Save(s, p); return View(p);
        }
        finally { if (p.State == "Running") { p.State = "Failed"; await Save(s, p); } s.Mcp.Gate.Release(); }
    }
    async Task Save(BrowserSession s, McpExecutionPlan p)
    {
        var folder = Path.Combine(options.RepositoryRoot, "artifacts", "portal-mcp", s.Mcp.Id); Directory.CreateDirectory(folder);
        // Arguments can contain private infrastructure metadata. Kept in ignored local receipts.
        await File.WriteAllTextAsync(Path.Combine(folder, p.Id + ".json"), JsonSerializer.Serialize(View(p), Json));
    }
    public async Task<object> AgentCall(BrowserSession s, string provider, string tool, JsonElement args, CancellationToken ct)
    {
        var p = Plan(s, new([new(provider, tool, args)]));
        return p.RequiresApproval ? View(p) : await Run(s, p.Id, p.Digest, ct);
    }
    public static JsonElement Redact(JsonElement value)
    {
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 256 * 1024)
            return JsonSerializer.SerializeToElement(new { withheld = true, reason = "Provider output exceeds 256 KiB. Refine the tool query.", sha256 = Digest(value) });
        string Text(string text, int depth)
        {
            // MCP commonly wraps a JSON result in content[].text. Inspect that structure
            // too; checking only the outer protocol property names misses its credentials.
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
                    return JsonSerializer.Serialize(Walk(nested.RootElement, depth + 1), Json);
                }
                catch (JsonException) { /* Non-JSON prose is still checked below. */ }
            }
            if (Regex.IsMatch(text, "(?i)(client.?secret|access.?token|refresh.?token|connection.?string|AccountKey|SharedAccessSignature|Password|\\bPwd)\\s*[\"']?\\s*[:=]|[?&;]sig="))
                return "[withheld: credential-shaped provider text]";
            return Regex.Replace(text, "(?i)(Bearer\\s+)[a-z0-9._~+/=-]+|gh[pousr]_[a-z0-9_]+|github_pat_[a-z0-9_]+|eyJ[a-zA-Z0-9_-]+\\.[a-zA-Z0-9_-]+\\.[a-zA-Z0-9_-]+", "[redacted]");
        }
        object? Walk(JsonElement e, int depth)
        {
            if (depth > 24) return "[withheld: nested provider output]";
            return e.ValueKind switch
            {
                JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Secrets.IsMatch(p.Name) ? (object?)"[redacted]" : Walk(p.Value, depth + 1)),
                JsonValueKind.Array => e.EnumerateArray().Select(x => Walk(x, depth + 1)).ToArray(),
                JsonValueKind.String => Text(e.GetString()!, depth),
                _ => e.Clone()
            };
        }
        var result = JsonSerializer.SerializeToElement(Walk(value, 0), Json);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 256 * 1024) return JsonSerializer.SerializeToElement(new { withheld = true, reason = "Provider output exceeds 256 KiB. Refine the tool query.", sha256 = Digest(value) });
        return result;
    }
    public static string Digest(JsonElement value)
    {
        string Canonical(JsonElement e) => e.ValueKind switch { JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}", JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]", _ => e.GetRawText() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value)))).ToLowerInvariant();
    }
}
