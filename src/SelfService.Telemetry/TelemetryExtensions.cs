using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SelfService.Telemetry;

public static class TelemetryExtensions
{
    public static TBuilder AddPlatformTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddPlatformTelemetry(builder.Configuration, serviceName);
        builder.Logging.AddPlatformTelemetry(builder.Configuration, serviceName);
        return builder;
    }

    public static IServiceCollection AddPlatformTelemetry(this IServiceCollection services,
        IConfiguration configuration, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var export = TelemetryExportSettings.Read(configuration);
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        // HTTP propagation carries W3C trace context only; SDK defaults must not forward caller baggage.
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddService(serviceName, serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing.AddSource(PlatformTelemetry.SourceName, "PlatformStudio.*")
                    .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
                    .AddAspNetCoreInstrumentation(options => options.RecordException = false)
                    .AddHttpClientInstrumentation(options => options.RecordException = false)
                    .AddProcessor(new PrivacyActivityProcessor());
                if (export is not null) tracing.AddOtlpExporter(options => export.Apply(options, "traces"));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(PlatformTelemetry.MeterName)
                    .AddRuntimeInstrumentation()
                    .AddView("platform.operation.duration", new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.01, 0.05, 0.1, 0.5, 1, 5, 15, 30, 60, 120, 300, 600]
                    });
                // HTTP trace route metadata is useful; arbitrary raw HTTP metric labels can expose URLs.
                if (export is not null) metrics.AddOtlpExporter(options => export.Apply(options, "metrics"));
            });
        return services;
    }

    public static ILoggingBuilder AddPlatformTelemetry(this ILoggingBuilder logging,
        IConfiguration configuration, string serviceName = "PlatformStudio")
    {
        var export = TelemetryExportSettings.Read(configuration);
        logging.AddFilter<OpenTelemetryLoggerProvider>((category, level) =>
            category?.StartsWith("PlatformStudio", StringComparison.Ordinal) == true && level >= LogLevel.Information);
        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService(serviceName, serviceVersion: "1.0.0"));
            options.IncludeFormattedMessage = false;
            options.IncludeScopes = false;
            options.ParseStateValues = true;
            options.AddProcessor(new PrivacyLogProcessor());
            if (export is not null) options.AddOtlpExporter(otlp => export.Apply(otlp, "logs"));
        });
        return logging;
    }
}

internal sealed class PrivacyLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        // Log messages are intentionally replaced by a stable event name; only approved metadata is exported.
        record.Body = "Platform operation";
        record.EventId = new EventId(1, "platform.operation");
        record.FormattedMessage = null;
        record.Exception = null;
        record.Attributes = record.Attributes?.Where(tag => TelemetryPrivacy.IsAllowedTag(tag.Key, tag.Value)).ToArray();
    }
}

/// <summary>Export is opt-in and authenticated. A bad export target fails before an application starts.</summary>
public sealed record TelemetryExportSettings(Uri Endpoint, string Headers)
{
    public override string ToString() => $"OTLP collector {Endpoint.GetLeftPart(UriPartial.Authority)} (authenticated)";

    public static TelemetryExportSettings? Read(IConfiguration configuration)
    {
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new InvalidOperationException("OTLP requires a plain collector origin without user information, a path or a query.");
        // Local HTTP is appropriate only for an authenticated loopback dashboard. Remote collectors require TLS.
        if (uri.Scheme != "https" && !uri.IsLoopback)
            throw new InvalidOperationException("A remote OTLP collector requires HTTPS.");
        var headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
        if (string.IsNullOrWhiteSpace(headers) || headers.Contains('\r') || headers.Contains('\n') ||
            !headers.Split(',').All(header => header.Split('=', 2) is [var name, var value] &&
                name.Trim() is "x-otlp-api-key" or "Authorization" && value.Length is > 0 and <= 4096))
            throw new InvalidOperationException("OTLP requires an explicit API key or authorization header.");
        return new TelemetryExportSettings(uri, headers);
    }

    internal void Apply(OtlpExporterOptions options, string signal)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri(Endpoint, $"v1/{signal}");
        options.Headers = Headers;
        options.TimeoutMilliseconds = 5000;
    }
}
