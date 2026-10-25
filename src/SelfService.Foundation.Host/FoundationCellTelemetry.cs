namespace SelfService.Foundation.Host;

public sealed record FoundationLatencyBucket(double? UpperBoundSeconds, long Count);
public sealed record FoundationRequestMetrics(long Started, long Completed, long InFlight, long PeakInFlight,
    long Successful, long ClientRejected, long ServerFailed, long Aborted, long Other,
    double DurationSumSeconds, FoundationLatencyBucket[] DurationBuckets, double ProcessUptimeSeconds)
{
    public string Scope => "single-customer-cell-process";
    public bool Durable => false;
}

// Each host owns a separate instance. No static meter, exporter, dynamic labels, traces, paths,
// identities, headers, request bodies, model text or provider results enter this collector.
public sealed class FoundationCellTelemetry(TimeProvider? timeProvider = null)
{
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    readonly object gate = new();
    readonly double[] bounds = [.01, .05, .1, .5, 1, 2, 5, 10, double.PositiveInfinity];
    readonly long[] buckets = new long[9];
    readonly long born = (timeProvider ?? TimeProvider.System).GetTimestamp();
    long started, completed, active, peak, successful, rejected, failed, aborted, other;
    double seconds;

    public Observation Start()
    {
        lock (gate) { started++; active++; peak = Math.Max(peak, active); }
        return new(this, clock.GetTimestamp());
    }

    public FoundationRequestMetrics Snapshot()
    {
        lock (gate)
            return new(started, completed, active, peak, successful, rejected, failed, aborted, other, seconds,
                bounds.Select((upper, index) => new FoundationLatencyBucket(double.IsPositiveInfinity(upper) ? null : upper, buckets[index])).ToArray(),
                Math.Max(0, clock.GetElapsedTime(born).TotalSeconds));
    }

    void End(long timestamp, int status, bool wasAborted, bool unhandledFailure)
    {
        var elapsed = Math.Max(0, clock.GetElapsedTime(timestamp).TotalSeconds);
        lock (gate)
        {
            active--; completed++; seconds += elapsed;
            if (wasAborted) aborted++;
            else if (unhandledFailure || status >= 500) failed++;
            else if (status >= 400) rejected++;
            else if (status is >= 200 and < 300) successful++;
            else other++;
            for (var i = 0; i < bounds.Length; i++) if (elapsed <= bounds[i]) buckets[i]++;
        }
    }

    public sealed class Observation : IDisposable
    {
        readonly FoundationCellTelemetry owner;
        readonly long timestamp;
        int finished;
        internal Observation(FoundationCellTelemetry owner, long timestamp) { this.owner = owner; this.timestamp = timestamp; }
        public void Complete(int status, bool wasAborted = false, bool unhandledFailure = false)
        { if (Interlocked.Exchange(ref finished, 1) == 0) owner.End(timestamp, status, wasAborted, unhandledFailure); }
        public void Dispose() => Complete(500, unhandledFailure: true);
    }
}
