using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class PortalTelemetryTests
{
    [Fact]
    public async Task RequestUsesTemplateAndTraceReferenceWithoutCollectingRequestData()
    {
        using var activity = new Activity("request").Start();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/mcp/plans/private-id";
        context.Request.QueryString = new("?token=private-value");
        context.Request.Headers.Authorization = "Bearer private-value";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/mcp/plans/{id}"), 0, EndpointMetadataCollection.Empty, "plan"));
        var middleware = new PortalTelemetry(c => { c.Response.StatusCode = 503; return Task.CompletedTask; }, NullLoggerFactory.Instance);
        await middleware.Invoke(context);
        Assert.Equal(activity.TraceId.ToHexString(), context.Response.Headers["X-Trace-Id"]);
        Assert.Equal("/api/mcp/plans/{id}", activity.GetTagItem("http.route"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.DoesNotContain("private", string.Join(";", activity.TagObjects));
    }

    [Fact]
    public void CalculationProcessReceivesOnlyCorrelationAndOperatingEnvironment()
    {
        using var activity = new Activity("process").Start();
        activity.AddBaggage("secret", "private-value");
        var start = new ProcessStartInfo("node");
        start.Environment["AZURE_CLIENT_SECRET"] = "private-value";
        start.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=private-value";
        start.Environment["TRACEPARENT"] = "stale";
        start.Environment["TRACESTATE"] = "secret=private-value";
        ChildProcessTelemetry.Configure(start);
        Assert.Equal(activity.Id, start.Environment["TRACEPARENT"]);
        Assert.False(start.Environment.ContainsKey("AZURE_CLIENT_SECRET"));
        Assert.False(start.Environment.ContainsKey("OTEL_EXPORTER_OTLP_HEADERS"));
        Assert.False(start.Environment.ContainsKey("TRACESTATE"));
        Assert.DoesNotContain("private-value", start.Environment.Values);
    }

    [Theory]
    [InlineData("http://127.0.0.1:18888", "http://127.0.0.1:18888")]
    [InlineData("http://127.0.0.1:18888/login?t=private-value", null)]
    [InlineData("https://attacker.example", null)]
    [InlineData("http://localhost.attacker.example:18888", null)]
    [InlineData("http://user:private-value@127.0.0.1:18888", null)]
    public void BrowserReceivesOnlyPublicLiteralDashboardOrigin(string dashboard, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "portal-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config/observability.json"), System.Text.Json.JsonSerializer.Serialize(new { dashboardUrl = dashboard }));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318" }).Build();
            Assert.Equal(expected, ObservabilityStatus.Dashboard(root, config));
            Assert.Null(ObservabilityStatus.Dashboard(root, new ConfigurationBuilder().Build()));
        }
        finally { Directory.Delete(root, true); }
    }
}
