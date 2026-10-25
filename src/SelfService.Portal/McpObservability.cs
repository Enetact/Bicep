using System.Diagnostics;

namespace SelfService.Portal;

// Tool payloads and downstream exceptions can contain credentials or private
// telemetry. Spans deliberately describe only protocol operations and outcomes.
internal static class McpObservability
{
    static readonly ActivitySource Source = new("PlatformStudio.Mcp");
    public static Activity? Start(string operation, string? provider = null, string? tool = null)
    {
        var activity = Source.StartActivity(operation, operation.StartsWith("mcp.plan.", StringComparison.Ordinal) || operation == "mcp.disconnect" ? ActivityKind.Internal : ActivityKind.Client);
        var method = operation switch
        {
            "mcp.connect" or "mcp.connect.transport" => "initialize",
            "mcp.tools.list" => "tools/list",
            "mcp.tools.call" or "mcp.tools.call.transport" => "tools/call",
            _ => null
        };
        if (method is not null) activity?.SetTag("mcp.method.name", method);
        if (provider is not null) activity?.SetTag("mcp.server.name", provider);
        if (tool is not null) activity?.SetTag("gen_ai.tool.name", tool);
        return activity;
    }
    public static void Complete(Activity? activity, string outcome, bool error = false)
    {
        activity?.SetTag("platform.outcome", outcome);
        activity?.SetStatus(error ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        if (error) activity?.SetTag("error.type", outcome);
    }
}
