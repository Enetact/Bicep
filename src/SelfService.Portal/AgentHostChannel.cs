using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

// Bounded AHP profile with typed workflow/provider orchestration. No arbitrary
// chat dispatch, filesystem, terminal, or MCP App side-channel is advertised.
public static class AgentHostChannel
{
    public const string Version = "0.9.0";
    public const string Root = "ahp-root://";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Handle(HttpContext context, BrowserSession session, AgentWorkflows workflows, PortalOptions options, McpProviderBroker? broker = null)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin != options.Origin ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains("platform-studio") ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(session.Csrf))
            throw new PortalException("Invalid agent-host connection origin or session.", 403);
        using var socket = await context.WebSockets.AcceptWebSocketAsync("platform-studio");
        var initialized = false; string? channel = null; long seq = 0;
        async Task Send(object value) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)), WebSocketMessageType.Text, true, context.RequestAborted);
        object RootState() => new { agents = new[] { new { provider = "codex", displayName = "Codex", description = "Platform Studio bounded workflows", models = new[] { new { id = AgentPolicy.Model, name = AgentPolicy.DisplayName, provider = "codex" } } } },
            platformStudio = new { profile = "workflow-orchestration", modelTurns = "/api/agent/run", arbitraryDispatch = false, mcpSideChannel = false, version = Version,
                mcpProviders = broker?.Status(session, false), actions = new[] { "platform/workflowRun", "platform/mcpConnect", "platform/mcpDisconnect", "platform/mcpPlan", "platform/mcpRun" }, approvalRoute = "/api/mcp/plans/{id}/approve" } };
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var bytes = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted); return; }
                    if (received.MessageType != WebSocketMessageType.Text || bytes.Length + received.Count > 16_384) { await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid message", context.RequestAborted); return; }
                    bytes.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var doc = JsonDocument.Parse(bytes.ToArray()); var message = doc.RootElement;
                if (!message.TryGetProperty("id", out var id)) continue; // No writable actions supported by this profile.
                try
                {
                    var method = message.GetProperty("method").GetString(); var p = message.GetProperty("params");
                    var requested = p.GetProperty("channel").GetString(); object result;
                    if (method == "initialize" && !initialized && requested == Root)
                    {
                        if (!p.GetProperty("protocolVersions").EnumerateArray().Any(v => v.GetString() == Version)) throw new PortalException("Unsupported AHP version.");
                        initialized = true;
                        result = new { protocolVersion = Version, serverSeq = ++seq, snapshots = new[] { new { resource = Root, state = RootState(), fromSeq = seq } } };
                    }
                    else if (!initialized) throw new PortalException("Initialize the AHP connection first.");
                    else if (method == "dispatchAction" && channel is not null && requested == channel && broker is not null)
                    {
                        using var resumeAction = session.ResumeGate.BeginAction();
                        var action = p.GetProperty("action"); var kind = action.GetProperty("type").GetString();
                        var payload = action.GetProperty("request");
                        result = kind switch
                        {
                            "platform/workflowRun" => await workflows.Run(session, payload.Deserialize<AgentRequest>(Json) ?? throw new PortalException("Missing workflow request."), context.RequestAborted, "ahp"),
                            "platform/mcpConnect" => await broker.Connect(session, payload.GetProperty("provider").GetString()!, context.RequestAborted),
                            "platform/mcpDisconnect" => await broker.Disconnect(session, payload.GetProperty("provider").GetString()!, context.RequestAborted),
                            "platform/mcpPlan" => McpProviderBroker.View(broker.Plan(session, payload.Deserialize<McpPlanRequest>(Json) ?? throw new PortalException("Missing tool plan."))),
                            "platform/mcpRun" => await broker.Run(session, payload.GetProperty("id").GetString()!, payload.GetProperty("digest").GetString()!, context.RequestAborted),
                            _ => throw new PortalException("Unsupported agent-host action.", 403)
                        };
                    }
                    else if (method == "createSession" && channel is null && requested is not null &&
                        requested.StartsWith("ahp-session:/", StringComparison.Ordinal) && Guid.TryParse(requested[13..], out _) && p.GetProperty("provider").GetString() == "codex")
                    { channel = requested; result = new { }; }
                    else if (method == "subscribe" && requested == Root)
                        result = new { snapshot = new { resource = Root, state = RootState(), fromSeq = ++seq } };
                    else if (method == "subscribe" && channel is not null && requested == channel)
                        result = new { snapshot = new { resource = channel, state = new { lifecycle = "ready", provider = "codex", customizations = McpCustomizations(session, options.Origin), platformStudio = await workflows.Status(session, context.RequestAborted) }, fromSeq = ++seq } };
                    else throw new PortalException("Unsupported command or session channel. Use the typed workflow menu for model turns.");
                    await Send(new { jsonrpc = "2.0", id = id.Clone(), result });
                }
                catch (Exception e) when (e is PortalException or InvalidOperationException or KeyNotFoundException)
                { await Send(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32602, message = e is PortalException p ? p.Message : "Unsupported or invalid request for the Platform Studio orchestration profile." } }); }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException) { }
    }
    // Host-originated customization handles retain the session's provider identity.
    // Delegated sign-in uses the portal Connections adapters, not bearer tokens
    // sent through arbitrary AHP authenticate requests.
    public static object[] McpCustomizations(BrowserSession session, string origin) => McpProviderBroker.Providers.Select(p =>
    {
        session.Mcp.Connections.TryGetValue(p.Id, out var c);
        var valid = c is not null && c.Identity == McpProviderBroker.Identity(session, p.Id) && !c.Lifetime.IsCancellationRequested;
        var kind = c is null ? "stopped" : !valid || c.State == "authRequired" ? "error" : c.State;
        return (object)new { type = "mcpServer", id = "mcp:" + p.Id, uri = origin + "/api/mcp/status#" + p.Id, name = p.Name,
            state = kind == "error" ? (object)new { kind, error = new { code = "provider_connection_required", message = "Connect this provider using the browser-owned Connections controls." } } : new { kind },
            _meta = new { connectionRoute = "/api/mcp/" + p.Id + "/connect", toolCount = valid ? c!.Tools.Length : 0, catalogDigest = valid ? c!.CatalogDigest : null } };
    }).ToArray();
}
