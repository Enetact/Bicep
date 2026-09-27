using System.Diagnostics;
using System.Text.Json;

namespace SelfService.Portal;

public record AnalysisUpload(string Manifest, string Inventory);
public sealed class AnalysisRunner(PortalOptions options)
{
    readonly SemaphoreSlim gate = new(1, 1);
    public async Task<object> Run(AnalysisUpload upload, CancellationToken cancellation)
    {
        byte[] manifest, inventory;
        try { manifest = Convert.FromBase64String(upload.Manifest); inventory = Convert.FromBase64String(upload.Inventory); }
        catch { throw new PortalException("Select manifest.json and inventory.json."); }
        if (manifest.Length > 16 * 1024 * 1024 || inventory.Length > 16 * 1024 * 1024) throw new PortalException("Each input must be at most 16 MiB.", 413);
        if (!await gate.WaitAsync(0, cancellation)) throw new PortalException("Another analysis is running.", 409);
        var id = Guid.NewGuid().ToString("N"); var root = Path.Combine(options.RepositoryRoot, "artifacts/portal-analysis", id);
        try
        {
            var input = Path.Combine(root, "input"); var output = Path.Combine(root, "report"); Directory.CreateDirectory(input);
            await File.WriteAllBytesAsync(Path.Combine(input, "manifest.json"), manifest, cancellation);
            await File.WriteAllBytesAsync(Path.Combine(input, "inventory.json"), inventory, cancellation);
            var start = new ProcessStartInfo(options.NodePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Path.Combine(options.RepositoryRoot, "scripts/analysis/cli.mjs"), "--input", input, "--output", output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new PortalException("Install Node.js 22+ to run analysis.", 409);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation); var stderr = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw new PortalException("Analysis was cancelled or exceeded its time limit."); }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new PortalException("Analysis rejected the input. Check schema, selection and inventory hash using the analysis guide.");
            var diagrams = Directory.GetFiles(output, "*.svg").Order().ToArray();
            return new { id, report = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(output, "analysis.json"), cancellation)),
                markdown = await File.ReadAllTextAsync(Path.Combine(output, "README.md"), cancellation),
                diagramCount = diagrams.Length, diagrams = diagrams.Take(10).Select(File.ReadAllText).ToArray() };
        }
        catch (System.ComponentModel.Win32Exception) { throw new PortalException("Node.js was not found. Install Node.js 22+ and restart the portal.", 409); }
        finally { gate.Release(); }
    }
}
