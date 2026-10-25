using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SelfService.Telemetry;

/// <summary>Creates content-free spans shared by the portal, workflows and child processes.</summary>
public static class PlatformTelemetry
{
    public const string SourceName = "PlatformStudio";
    public const string MeterName = "PlatformStudio";
    public static readonly ActivitySource Activities = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Completed = Meter.CreateCounter<long>(
        "platform.operation.completed", "{operation}", "Operations completed by outcome; never includes user content.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "platform.operation.duration", "s", "Elapsed time for a bounded platform operation.");

    public static TelemetryOperation Start(string operation, ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityContext parentContext = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        // Operation names are developer-defined constants, not prompts, URLs or tool arguments.
        var activity = Activities.StartActivity(operation, kind, parentContext);
        var scope = new TelemetryOperation(operation, activity);
        if (tags is not null)
            foreach (var tag in tags) scope.Tag(tag.Key, tag.Value);
        return scope;
    }

    /// <summary>Returns W3C context only. Baggage is deliberately never propagated to subprocesses.</summary>
    public static string? TraceParent => Activity.Current?.Id;

    public static ActivityContext ReadParent(string? traceParent, string? traceState = null) =>
        ActivityContext.TryParse(traceParent, traceState, true, out var context) ? context : default;

    internal static void Record(string operation, string outcome, double seconds)
    {
        var tags = new TagList { { "platform.operation", operation }, { "platform.outcome", outcome } };
        Completed.Add(1, tags);
        Duration.Record(seconds, tags);
    }
}

/// <summary>A scope records outcome and duration even when no trace listener is attached.</summary>
public sealed class TelemetryOperation : IDisposable
{
    private readonly string operation;
    private readonly long started = Stopwatch.GetTimestamp();
    private string outcome = "success";
    private bool disposed;
    public Activity? Activity { get; }

    internal TelemetryOperation(string operation, Activity? activity)
    {
        this.operation = operation;
        Activity = activity;
    }

    public TelemetryOperation Tag(string key, object? value)
    {
        if (TelemetryPrivacy.IsAllowedTag(key, value)) Activity?.SetTag(key, value);
        return this;
    }

    public void Success()
    {
        outcome = "success";
        Activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public void Fail(Exception exception, bool canceled = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        outcome = canceled || exception is OperationCanceledException ? "canceled" : "error";
        // Exception messages and stack traces can contain credentials, request bodies and local paths.
        Activity?.SetTag("error.type", exception.GetType().FullName);
        Activity?.SetStatus(outcome == "canceled" ? ActivityStatusCode.Unset : ActivityStatusCode.Error);
    }

    public void Reject()
    {
        outcome = "error";
        Activity?.SetStatus(ActivityStatusCode.Error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Activity?.Status == ActivityStatusCode.Error) outcome = "error";
        Activity?.SetTag("platform.outcome", outcome);
        PlatformTelemetry.Record(operation, outcome, Stopwatch.GetElapsedTime(started).TotalSeconds);
        Activity?.Dispose();
    }
}
