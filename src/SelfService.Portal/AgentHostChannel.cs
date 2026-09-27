using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

// Bounded AHP coordination profile. Model turns stay behind the typed workflow API.
// No arbitrary chat dispatch, filesystem, terminal, or MCP side-channel is advertised.
public static class AgentHostChannel
{
    public const string Version = "0.9.0";
    public const string Root = "ahp-root://";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Handle(HttpContext context, BrowserSession session, AgentWorkflows workflows, PortalOptions options)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin != options.Origin ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains("platform-studio") ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(session.Csrf))
            throw new PortalException("Invalid agent-host connection origin or session.", 403);
        using var socket = await context.WebSockets.AcceptWebSocketAsync("platform-studio");
        var initialized = false; string? channel = null; long seq = 0;
        async Task Send(object value) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)), WebSocketMessageType.Text, true, context.RequestAborted);
        object RootState() => new { agents = new[] { new { provider = "codex", displayName = "Codex", description = "Platform Studio bounded workflows", models = new[] { new { id = AgentPolicy.Model, name = "GPT-6 Astra", provider = "codex" } } } },
            platformStudio = new { profile = "coordination-only", modelTurns = "/api/agent/run", arbitraryDispatch = false, mcpSideChannel = false, version = Version } };
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
                    else if (method == "createSession" && channel is null && requested is not null &&
                        requested.StartsWith("ahp-session:/", StringComparison.Ordinal) && Guid.TryParse(requested[13..], out _) && p.GetProperty("provider").GetString() == "codex")
                    { channel = requested; result = new { }; }
                    else if (method == "subscribe" && requested == Root)
                        result = new { snapshot = new { resource = Root, state = RootState(), fromSeq = ++seq } };
                    else if (method == "subscribe" && channel is not null && requested == channel)
                        result = new { snapshot = new { resource = channel, state = new { lifecycle = "ready", provider = "codex", platformStudio = await workflows.Status(session, context.RequestAborted) }, fromSeq = ++seq } };
                    else throw new PortalException("Unsupported command or session channel. Use the typed workflow menu for model turns.");
                    await Send(new { jsonrpc = "2.0", id = id.Clone(), result });
                }
                catch (Exception e) when (e is PortalException or InvalidOperationException or KeyNotFoundException)
                { await Send(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32602, message = "Unsupported or invalid request for the Platform Studio coordination profile." } }); }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException) { }
    }
}
