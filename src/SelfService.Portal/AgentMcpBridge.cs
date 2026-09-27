using System.IO.Pipelines;
using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SelfService.Portal;

// An actual MCP session over local pipes. The model can read only the immutable evidence
// selected and collected by this browser session; it cannot choose subscriptions or URLs.
public sealed class AgentMcpBridge : IAsyncDisposable
{
    readonly McpServer server;
    readonly McpClient client;
    readonly CancellationTokenSource lifetime;
    readonly Task serving;
    readonly Func<bool> authorized;
    readonly HashSet<string> read = [];
    readonly ConcurrentQueue<object> audit = new();
    public IReadOnlyList<object> Audit => audit.ToArray();
    public bool EvidenceRead => read.Contains("platform_evidence") && read.Contains("platform_skill");
    public object[] Tools { get; private set; } = [];
    AgentMcpBridge(McpServer server, McpClient client, Task serving, CancellationTokenSource lifetime, Func<bool> authorized)
    { this.server = server; this.client = client; this.serving = serving; this.lifetime = lifetime; this.authorized = authorized; }
    public static async Task<AgentMcpBridge> Create(string evidence, string skill, Func<bool> authorized, CancellationToken ct)
    {
        var upstream = new Pipe(); var downstream = new Pipe();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string Guard(string content) => authorized() ? content : throw new InvalidOperationException("Session disconnected.");
        var server = McpServer.Create(new StreamServerTransport(upstream.Reader.AsStream(), downstream.Writer.AsStream()), new McpServerOptions
        {
            ServerInfo = new() { Name = "platform-studio-evidence", Version = "1.0.0" },
            ToolCollection = [
                McpServerTool.Create(() => Guard(evidence), new() { Name = "platform_evidence", Description = "Read the frozen evidence and scope of this workflow. Contains no credentials. Missing evidence remains unknown." }),
                McpServerTool.Create(() => Guard(skill), new() { Name = "platform_skill", Description = "Read the selected pinned skill and this adapter's coverage limitations." })
            ]
        });
        var serving = server.RunAsync(lifetime.Token);
        try
        {
            var client = await McpClient.CreateAsync(new StreamClientTransport(upstream.Writer.AsStream(), downstream.Reader.AsStream()), cancellationToken: ct);
            var bridge = new AgentMcpBridge(server, client, serving, lifetime, authorized);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            bridge.Tools = tools.Select(t => (object)new { type = "function", name = t.Name, description = t.Description, inputSchema = t.JsonSchema }).ToArray();
            return bridge;
        }
        catch { lifetime.Cancel(); await server.DisposeAsync(); lifetime.Dispose(); throw; }
    }
    public async Task<object> Call(JsonElement request, CancellationToken ct)
    {
        var name = request.GetProperty("tool").GetString() ?? "";
        var args = request.GetProperty("arguments");
        if (!authorized() || name is not ("platform_evidence" or "platform_skill") ||
            args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any() ||
            request.TryGetProperty("namespace", out var ns) && ns.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) || Audit.Count >= 12)
        {
            audit.Enqueue(new { timeUtc = DateTimeOffset.UtcNow, method = "tools/call", tool = name is "platform_evidence" or "platform_skill" ? name : "unregistered", success = false, reason = "Policy rejected tool, arguments, session or call budget" });
            throw new PortalException("Agent requested an unauthorized or excessive tool call. Review stopped.", 403);
        }
        var result = await client.CallToolAsync(name, new Dictionary<string, object?>(), cancellationToken: ct);
        var success = result.IsError != true;
        audit.Enqueue(new { timeUtc = DateTimeOffset.UtcNow, method = "tools/call", tool = name, success });
        if (success) read.Add(name);
        return new { success, contentItems = result.Content.OfType<TextContentBlock>().Select(x => new { type = "inputText", text = x.Text }).ToArray() };
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); await client.DisposeAsync(); await server.DisposeAsync();
        try { await serving; } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
