using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SelfService.Telemetry;
using Xunit;

namespace SelfService.Telemetry.Tests;

public sealed class TelemetryCommandTests
{
    [Theory]
    [InlineData(0, "success")]
    [InlineData(7, "error")]
    public async Task CommandExportsCompletedRootBeforeReturningAndPreservesExitCode(int exitCode, string outcome)
    {
        await using var collector = new OtlpCollector();
        var variables = new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_BSP_SCHEDULE_DELAY", "TRACEPARENT", "TRACESTATE" };
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var operation = "test.command." + Guid.NewGuid().ToString("N");
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.OperationName == operation) stopped = activity; }
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", collector.Origin);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "x-otlp-api-key=synthetic-test-key");
            // Prevent the periodic batch timer from concealing a shutdown-order regression.
            Environment.SetEnvironmentVariable("OTEL_BSP_SCHEDULE_DELAY", "60000");
            Environment.SetEnvironmentVariable("TRACEPARENT", null);
            Environment.SetEnvironmentVariable("TRACESTATE", null);
            var returned = await TelemetryCommand.Run("Test.Command", operation, () => Task.FromResult(exitCode));
            Assert.Equal(exitCode, returned);
            Assert.NotNull(stopped);
            Assert.Equal(exitCode == 0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error, stopped.Status);
            Assert.Equal(outcome, stopped.GetTagItem("platform.outcome"));
            Assert.Null(stopped.GetTagItem("error.type"));
            // OTLP protobuf stores span names as UTF-8 strings. The exact unique
            // root name must reach the actual HTTP exporter before Run returns.
            Assert.Contains(collector.Traces, payload => Encoding.UTF8.GetString(payload).Contains(operation, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var variable in previous) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
    }

    // A minimal local HTTP sink avoids ASP.NET tracing the collector itself.
    // It accepts the real OTLP HTTP protobuf export without any network service.
    sealed class OtlpCollector : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource lifetime = new();
        readonly Task server;
        public ConcurrentBag<byte[]> Traces { get; } = [];
        public string Origin { get; }
        public OtlpCollector()
        {
            listener.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            server = Accept();
        }
        async Task Accept()
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                await using var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new List<byte>(); var next = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(next, timeout.Token) != 1) throw new IOException("Incomplete test collector request.");
                    header.Add(next[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var length = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1]);
                if (length is < 0 or > 2 * 1024 * 1024) throw new IOException("Excessive test collector request.");
                var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
                if (lines[0].StartsWith("POST /v1/traces ", StringComparison.Ordinal)) Traces.Add(body);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); listener.Stop();
            try { await server; }
            catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
            lifetime.Dispose();
        }
    }
}
