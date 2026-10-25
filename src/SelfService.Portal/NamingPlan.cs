using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SelfService.Telemetry;

namespace SelfService.Portal;

// Local-only adapter. Neither a token provider nor an ADO gateway is available here.
public sealed class NamingPlan(PortalOptions options)
{
    readonly SemaphoreSlim gate = new(1, 1);
    public async Task<JsonElement> Generate(JsonElement request, CancellationToken cancellation)
    {
        var payload = Encoding.UTF8.GetBytes(request.GetRawText());
        if (payload.Length > 65536) throw new PortalException("Naming draft exceeds 64 KiB.", 413);
        if (!await gate.WaitAsync(0, cancellation)) throw new PortalException("Another naming calculation is running.", 409);
        using var span = PlatformTelemetry.Start("naming.process", ActivityKind.Client).Tag("platform.process.kind", "node-naming");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new ProcessStartInfo(options.NodePath) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 } };
        process.StartInfo.ArgumentList.Add(Path.Combine(options.RepositoryRoot, "scripts/naming/workload-cli.mjs"));
        ChildProcessTelemetry.Configure(process.StartInfo);
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new PortalException("Naming runtime could not start.", 409);
            var output = PreviewDiagram.Bounded(process.StandardOutput.BaseStream, 2 * 1024 * 1024, timeout.Token);
            var errors = PreviewDiagram.Bounded(process.StandardError.BaseStream, 8192, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            span.Tag("platform.process.exit_code", process.ExitCode);
            if (process.ExitCode != 0) throw new PortalException("Naming draft rejected. Check workload support, subscription, environment, acronym (2–4 lowercase characters), instance UUID and ordinal (01–99).");
            var result = JsonSerializer.Deserialize<JsonElement>(output.Result);
            if (result.GetProperty("schema").GetString() != "platform.workload-naming-draft/v1" || result.GetProperty("deploymentAuthorized").GetBoolean())
                throw new PortalException("Invalid naming response.", 409);
            return result;
        }
        catch (System.ComponentModel.Win32Exception e) { span.Fail(e); throw new PortalException("Install Node.js 22+ for local naming calculations.", 409); }
        catch (OperationCanceledException e) { span.Fail(e, true); throw new PortalException("Naming calculation cancelled or timed out.", 408); }
        catch (Exception e) { span.Fail(e); throw; }
        finally { if (started && !process.HasExited) process.Kill(true); gate.Release(); }
    }
}
