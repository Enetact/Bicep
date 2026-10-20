using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;
using SelfService.Telemetry;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SelfService.Telemetry.Tests;

public sealed class TelemetryTests
{
    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void HandoffPreservesAmbientAndExplicitW3cParent()
    {
        using var listener = Listen();
        using var parent = PlatformTelemetry.Start("workflow.parent");
        using (var child = PlatformTelemetry.Start("workflow.child"))
        {
            Assert.Equal(parent.Activity!.TraceId, child.Activity!.TraceId);
            Assert.Equal(parent.Activity.SpanId, child.Activity.ParentSpanId);
        }
        var context = PlatformTelemetry.ReadParent(PlatformTelemetry.TraceParent);
        using var remote = PlatformTelemetry.Start("process.child", ActivityKind.Consumer, parentContext: context);
        Assert.Equal(parent.Activity!.TraceId, remote.Activity!.TraceId);
        Assert.Equal(parent.Activity.SpanId, remote.Activity.ParentSpanId);
        Assert.Equal(default, PlatformTelemetry.ReadParent("invalid"));
    }

    [Fact]
    public void DefaultHttpPropagationDoesNotForwardBaggage()
    {
        var services = new ServiceCollection();
        services.AddPlatformTelemetry(new ConfigurationBuilder().Build(), "Test.Service");
        var context = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var headers = new Dictionary<string, string>();
        Propagators.DefaultTextMapPropagator.Inject(new PropagationContext(context,
            Baggage.Create(new Dictionary<string, string> { ["private-user"] = "secret" })),
            headers, (carrier, key, value) => carrier[key] = value);
        Assert.Contains("traceparent", headers.Keys);
        Assert.DoesNotContain("baggage", headers.Keys);
        Assert.DoesNotContain("secret", string.Join(",", headers.Values));
    }

    [Fact]
    public void ScopeRejectsContentAndIdentityAndRecordsActualFailureType()
    {
        using var listener = Listen();
        using var operation = PlatformTelemetry.Start("mcp.call", ActivityKind.Client);
        operation.Tag("mcp.tool.name", "list_resources")
            .Tag("tool.arguments", "secret")
            .Tag("gen_ai.input.messages", "private prompt")
            .Tag("mcp.session.id", "private provider identity")
            .Tag("platform.parent.id", "browser identity");
        operation.Fail(new InvalidOperationException("private credential"));
        Assert.Equal("list_resources", operation.Activity!.GetTagItem("mcp.tool.name"));
        Assert.Null(operation.Activity.GetTagItem("tool.arguments"));
        Assert.Null(operation.Activity.GetTagItem("gen_ai.input.messages"));
        Assert.Null(operation.Activity.GetTagItem("mcp.session.id"));
        Assert.Null(operation.Activity.GetTagItem("platform.parent.id"));
        Assert.Equal(typeof(InvalidOperationException).FullName, operation.Activity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, operation.Activity.Status);
        Assert.Null(operation.Activity.StatusDescription);
        Assert.Empty(operation.Activity.Events);
    }

    [Fact]
    public void ReturnedFailureAndCancellationHaveDistinctOutcomes()
    {
        using var listener = Listen();
        var rejected = PlatformTelemetry.Start("mcp.rejected");
        rejected.Reject();
        rejected.Dispose();
        Assert.Equal("error", rejected.Activity!.GetTagItem("platform.outcome"));
        Assert.Null(rejected.Activity.GetTagItem("error.type"));
        var canceled = PlatformTelemetry.Start("agent.canceled");
        canceled.Fail(new OperationCanceledException("private"));
        canceled.Dispose();
        Assert.Equal("canceled", canceled.Activity!.GetTagItem("platform.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, canceled.Activity.Status);
    }

    [Fact]
    public void ExportProcessorRemovesRawHttpLocationsAndErrorDescriptions()
    {
        var exporter = new TraceCapture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformTelemetry(new ConfigurationBuilder().Build(), "Test.Service");
        services.AddOpenTelemetry().WithTracing(builder => builder.AddSource("System.Net.Http")
            .AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var serviceProvider = services.BuildServiceProvider();
        _ = serviceProvider.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource("System.Net.Http");
        using (var activity = source.StartActivity("GET /private-capability"))
        {
            activity!.SetTag("url.full", "https://user:password@example.test/private?token=secret#fragment");
            activity.SetTag("url.path", "/private-capability");
            activity.SetTag("http.request.method", "GET");
            activity.SetTag("http.response.status_code", 500);
            activity.SetStatus(ActivityStatusCode.Error, "secret exception text");
        }
        var exported = Assert.Single(exporter.Activities);
        Assert.Null(exported.GetTagItem("url.full"));
        Assert.Null(exported.GetTagItem("url.path"));
        Assert.Null(exported.StatusDescription);
        Assert.Equal("GET request", exported.DisplayName);
        Assert.Equal(500, exported.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public void ImmutableExceptionEventsAndLinkAttributesAreNotExported()
    {
        var exporter = new TraceCapture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformTelemetry(new ConfigurationBuilder().Build(), "Test.Service");
        services.AddOpenTelemetry().WithTracing(builder => builder.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var serviceProvider = services.BuildServiceProvider();
        _ = serviceProvider.GetRequiredService<TracerProvider>();
        using (var operation = PlatformTelemetry.Start("unsafe.exception"))
            operation.Activity!.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            { { "exception.message", "private payload" } }));
        using (var operation = PlatformTelemetry.Start("safe.operation")) operation.Success();
        Assert.Equal("safe.operation", Assert.Single(exporter.Activities).OperationName);
    }

    [Fact]
    public void LogProcessorExportsOnlyOwnedContentFreeMetadata()
    {
        var exporter = new LogCapture();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddPlatformTelemetry("Test.Service");
        builder.Logging.AddOpenTelemetry(options => options.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("PlatformStudio.Tests").LogError(new InvalidOperationException("secret exception"),
            "private body {credential}", "secret value");
        factory.CreateLogger("Unowned.Provider").LogInformation("private SDK payload");
        var log = Assert.Single(exporter.Logs);
        Assert.Equal("Platform operation", log.Body);
        Assert.Null(log.Exception);
        Assert.Empty(log.Attributes);
    }

    [Theory]
    [InlineData("http://example.test:4318", "x-otlp-api-key=key")]
    [InlineData("http://127.0.0.1:4318?token=private", "x-otlp-api-key=key")]
    [InlineData("http://127.0.0.1:4318", null)]
    [InlineData("http://127.0.0.1:4318", "unsafe-header=key")]
    public void UnsafeOrUnauthenticatedExportFailsClosed(string endpoint, string? headers)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint, ["OTEL_EXPORTER_OTLP_HEADERS"] = headers }).Build();
        Assert.Throws<InvalidOperationException>(() => TelemetryExportSettings.Read(configuration));
    }

    [Fact]
    public void ExportIsOptInAndAuthenticatedLoopbackOrTlsAreAccepted()
    {
        Assert.Null(TelemetryExportSettings.Read(new ConfigurationBuilder().Build()));
        foreach (var endpoint in new[] { "http://127.0.0.1:4318", "https://collector.example.test" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint, ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=local-key" }).Build();
            var settings = TelemetryExportSettings.Read(config);
            Assert.NotNull(settings);
            Assert.DoesNotContain("local-key", settings.ToString());
        }
    }

    private sealed class TraceCapture : BaseExporter<Activity>
    {
        public List<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Activities.Add(activity);
            return ExportResult.Success;
        }
    }

    private sealed class LogCapture : BaseExporter<LogRecord>
    {
        public List<(string? Body, Exception? Exception, KeyValuePair<string, object?>[] Attributes)> Logs { get; } = [];
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch) Logs.Add((record.Body, record.Exception, record.Attributes?.ToArray() ?? []));
            return ExportResult.Success;
        }
    }
}
