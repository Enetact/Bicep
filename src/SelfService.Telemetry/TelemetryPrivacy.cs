using System.Diagnostics;
using OpenTelemetry;

namespace SelfService.Telemetry;

/// <summary>Metadata allowlist applied at creation and again immediately before trace export.</summary>
public static class TelemetryPrivacy
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "platform.operation", "platform.outcome", "platform.stage", "platform.provider",
        "platform.product", "platform.workload", "platform.phase", "platform.action",
        "platform.mode", "platform.result", "platform.attempt", "platform.item.count",
        "platform.queue.depth",
        "platform.checkpoint", "platform.process.kind", "platform.process.exit_code",
        "platform.connection.kind", "platform.connection.resumed", "platform.tool.category",
        "gen_ai.operation.name", "gen_ai.provider.name", "gen_ai.request.model", "gen_ai.response.model",
        "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.agent.name", "gen_ai.tool.name", "gen_ai.tool.type",
        "mcp.method.name", "mcp.server.name", "mcp.tool.name", "mcp.protocol.version",
        "rpc.system", "rpc.service", "rpc.method", "jsonrpc.error.code",
        "http.request.method", "http.response.status_code", "http.route", "network.protocol.version",
        "server.port", "error.type", "retry.count"
    };

    public static bool IsAllowedTag(string key, object? value) =>
        AllowedTags.Contains(key) && (value is null or bool or byte or short or int or long or float or double ||
            value is string text && text.Length <= 256 && !text.Contains('\r') && !text.Contains('\n'));

    internal static void Scrub(Activity activity)
    {
        // Activity events and link attributes are immutable. Drop an unsafe span rather than export its content.
        if (activity.Events.Any() || activity.Links.Any(link =>
            !string.IsNullOrEmpty(link.Context.TraceState) || link.Tags?.Any() == true))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }
        activity.TraceStateString = null;
        foreach (var baggage in activity.Baggage.ToArray()) activity.SetBaggage(baggage.Key, null);
        foreach (var tag in activity.TagObjects.ToArray())
            if (!IsAllowedTag(tag.Key, tag.Value)) activity.SetTag(tag.Key, null);
        // Neither raw URL paths, URL queries nor arbitrary exception descriptions belong in local telemetry.
        if (activity.Status == ActivityStatusCode.Error) activity.SetStatus(ActivityStatusCode.Error);
        if (activity.Source.Name is "Microsoft.AspNetCore" or "System.Net.Http")
        {
            var method = activity.GetTagItem("http.request.method") as string ?? "HTTP";
            var route = activity.GetTagItem("http.route") as string;
            activity.DisplayName = route is null ? $"{method} request" : $"{method} {route}";
        }
    }
}

internal sealed class PrivacyActivityProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity) => TelemetryPrivacy.Scrub(activity);
}
