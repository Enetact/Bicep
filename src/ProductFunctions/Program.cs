using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using SelfService.Telemetry;

// Host and worker use the same W3C invocation context without exporting message bodies.
using var host = new HostBuilder().ConfigureFunctionsWorkerDefaults()
    .ConfigureLogging((context, logging) => logging.AddPlatformTelemetry(context.Configuration, "platform-product-functions"))
    .ConfigureServices((context, services) =>
    {
        services.AddPlatformTelemetry(context.Configuration, "platform-product-functions");
        services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
    }).Build();
await host.RunAsync();
