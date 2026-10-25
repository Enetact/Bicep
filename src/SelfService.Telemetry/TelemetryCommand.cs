using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SelfService.Telemetry;

/// <summary>Starts and flushes telemetry for short lived commands without changing their console contract.</summary>
public static class TelemetryCommand
{
    public static async Task<T> Run<T>(string serviceName, string operation, Func<Task<T>> action)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.AddPlatformTelemetry(serviceName);
        using var host = builder.Build();
        await host.StartAsync();
        // Only an explicit W3C parent crosses the command boundary. Command arguments and baggage do not.
        using var span = PlatformTelemetry.Start(operation, parentContext: PlatformTelemetry.ReadParent(
            Environment.GetEnvironmentVariable("TRACEPARENT"), Environment.GetEnvironmentVariable("TRACESTATE")));
        try
        {
            var result = await action();
            if (result is int exitCode && exitCode != 0) span.Reject();
            else span.Success();
            return result;
        }
        catch (Exception e) { span.Fail(e); throw; }
        finally
        {
            // End the command before the hosted exporters stop so its root span is included in the final batch.
            span.Dispose();
            await host.StopAsync();
        }
    }
}
