using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfService.Telemetry;

namespace SelfService.Portal;

public sealed record FabricReadinessCheck(string Id, string Label, string Status, string Detail);
public sealed record FabricReadinessSource(string Path, string Sha256);
public sealed record FabricReadinessReport(int SchemaVersion, string Environment, string Region, DateTimeOffset CheckedUtc,
    string Scope, string Status, bool CanDeploy, bool LiveVerified, string PoolName, string WorkspaceMode, string PrivateProfile,
    string Pipeline, FabricReadinessSource[] Sources, FabricReadinessCheck[] Checks, string[] Limitations);
public sealed record FabricReadinessProcessResult(int ExitCode, byte[] Output);
public interface IFabricReadinessRunner
{
    Task<FabricReadinessProcessResult> Run(ProcessStartInfo start, CancellationToken cancellation);
}

// The child has no token provider, pipeline client, interactive input or inherited service credentials.
public sealed class FabricReadinessRunner : IFabricReadinessRunner
{
    public async Task<FabricReadinessProcessResult> Run(ProcessStartInfo start, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException();
            process.StandardInput.Close();
            using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var output = PreviewDiagram.Bounded(process.StandardOutput.BaseStream, FabricReadiness.MaximumOutputBytes, cancellation);
            var errors = PreviewDiagram.Bounded(process.StandardError.BaseStream, 8192, cancellation);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(cancellation));
            return new(process.ExitCode, output.Result);
        }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } } catch { }
            }
        }
    }
}

// Source assessment only. Live pool, current-job and private-network proofs remain in the protected pipeline.
public sealed class FabricReadiness(PortalOptions options, Catalog catalog, IFabricReadinessRunner runner)
{
    public const int MaximumOutputBytes = 128 * 1024;
    const string Unavailable = "Fabric source readiness is unavailable. Check the reviewed source files and PowerShell 7.4 or later.";
    readonly SemaphoreSlim gate = new(1, 1);
    static readonly string[] Environments = ["dev", "qa", "uat", "prod"];
    static readonly string[] Scripts = ["Get-FabricReadiness.ps1", "fabric-readiness-common.ps1", "fabric-workspace-common.ps1", "fabric-execution-common.ps1", "managed-devops-pool-common.ps1"];
    static readonly string[] SourceChecks = ["source-onboarding", "source-identity", "source-operation", "source-pool-binding", "source-pool-profile", "source-region", "source-private-workspace"];
    static readonly string[] RuntimeChecks = ["runtime-managed-pool", "runtime-agent-subnet", "runtime-agent-job", "runtime-private-endpoint", "runtime-private-reachability", "runtime-public-policy", "runtime-plan-verification"];
    static readonly string[] SafeEnvironment = ["PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LANG", "LC_ALL", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"];

    public async Task<FabricReadinessReport> Read(string environment, string region, CancellationToken cancellation)
    {
        if (!Environments.Contains(environment) || !Regex.IsMatch(region, "^[a-z][a-z0-9]{2,39}$") || !catalog.Regions.Contains(region) ||
            !catalog.Products.Any(p => p.Id == "fabric" && p.Targets.Any(t => t.Environment == environment)))
            throw new PortalException("Select a registered Fabric environment and region.");
        var entered = false;
        using var span = PlatformTelemetry.Start("fabric.readiness", ActivityKind.Client).Tag("platform.process.kind", "powershell-readiness");
        try
        {
            entered = await gate.WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            if (!entered) throw new PortalException("Another Fabric source assessment is running. Retry when it completes.", 409);
            var root = Path.GetFullPath(options.RepositoryRoot);
            foreach (var name in Scripts) GuardFile(root, Path.Combine(root, "scripts", name));
            var start = new ProcessStartInfo(PowerShell())
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            var retained = start.Environment.Where(v => SafeEnvironment.Contains(v.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
            start.Environment.Clear();
            foreach (var entry in retained) start.Environment[entry.Key] = entry.Value;
            start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
            start.Environment["POWERSHELL_UPDATECHECK"] = "Off";
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "scripts/Get-FabricReadiness.ps1"), "-EnvironmentName", environment, "-Region", region }) start.ArgumentList.Add(arg);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await runner.Run(start, timeout.Token);
            span.Tag("platform.process.exit_code", result.ExitCode);
            if (result.ExitCode != 0 || result.Output.Length > MaximumOutputBytes) throw new InvalidOperationException();
            return Validate(result.Output, environment, region);
        }
        catch (PortalException e) when (!entered) { span.Fail(e); throw; }
        catch (OperationCanceledException e) { span.Fail(e, true); throw new PortalException("Fabric source assessment was cancelled or timed out.", 408); }
        catch (Exception e) { span.Fail(e); throw new PortalException(Unavailable, 409); }
        finally { if (entered) gate.Release(); }
    }
    static void GuardFile(string root, string file)
    {
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        var info = new FileInfo(full);
        if (!info.Exists || info.Length > 256 * 1024) throw new InvalidOperationException();
        for (FileSystemInfo? node = info; node is not null; node = node is FileInfo f ? f.Directory : ((DirectoryInfo)node).Parent)
            if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
    }
    static string PowerShell()
    {
        var file = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var installed = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", file);
        return new[] { installed }.Concat((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p, file))).Where(Path.IsPathFullyQualified).FirstOrDefault(File.Exists) ?? throw new InvalidOperationException();
    }
    static FabricReadinessReport Validate(byte[] output, string environment, string region)
    {
        using var document = JsonDocument.Parse(output, new() { MaxDepth = 12 });
        var r = document.RootElement;
        Keys(r, ["schemaVersion", "environment", "region", "checkedUtc", "scope", "status", "canDeploy", "liveVerified", "poolName", "workspaceMode", "privateProfile", "pipeline", "sources", "checks", "limitations"]);
        if (r.GetProperty("schemaVersion").GetInt32() != 1 || r.GetProperty("environment").GetString() != environment || r.GetProperty("region").GetString() != region ||
            r.GetProperty("scope").GetString() != "ReviewedSourceOnly" || r.GetProperty("canDeploy").GetBoolean() || r.GetProperty("liveVerified").GetBoolean() ||
            r.GetProperty("pipeline").GetString() != "azure-pipelines-fabric-workspace.yml" || r.GetProperty("privateProfile").GetString() != "Same VNet / Azure DNS / IPv4") throw new InvalidOperationException();
        var status = Text(r.GetProperty("status"), 50);
        var mode = Text(r.GetProperty("workspaceMode"), 30);
        if (status is not ("NeedsConfiguration" or "AwaitingPipelineVerification" or "Unavailable") || mode is not ("Bootstrap" or "Private" or "Unconfigured")) throw new InvalidOperationException();
        var date = r.GetProperty("checkedUtc").GetDateTimeOffset();
        if (date < DateTimeOffset.UtcNow.AddMinutes(-5) || date > DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidOperationException();
        var sources = Array(r.GetProperty("sources"), 16).Select(s =>
        {
            Keys(s, ["path", "sha256"]);
            var path = Text(s.GetProperty("path"), 200); var hash = Text(s.GetProperty("sha256"), 64);
            var allowed = new[] { "config/platform.json", "config/workloads.json", $"self-service/fabric/workspace.{environment}.json", $"self-service/targets/fabric.{environment}.json", $"self-service/targets/devopspool.{environment}.json", $"workloads/managed-devops-pool/environments/main.{environment}.bicepparam", "pipelines/fabric-execution-bindings.yml" }
                .Concat(Scripts.Select(s => "scripts/" + s));
            if (!allowed.Contains(path) || !Regex.IsMatch(hash, "^[0-9a-f]{64}$")) throw new InvalidOperationException();
            return new FabricReadinessSource(path, hash);
        }).ToArray();
        if (sources.DistinctBy(s => s.Path).Count() != sources.Length) throw new InvalidOperationException();
        var checks = Array(r.GetProperty("checks"), 24).Select(c =>
        {
            Keys(c, ["id", "label", "status", "detail"]);
            var id = Text(c.GetProperty("id"), 80); var state = Text(c.GetProperty("status"), 40);
            if (!Regex.IsMatch(id, "^(source|runtime)-[a-z0-9-]{1,65}$") || state is not ("Configured" or "NeedsConfiguration" or "NotVerified" or "Unsupported") ||
                (id.StartsWith("runtime-", StringComparison.Ordinal) && state != "NotVerified")) throw new InvalidOperationException();
            return new FabricReadinessCheck(id, Text(c.GetProperty("label"), 150), state, Text(c.GetProperty("detail"), 1200));
        }).ToArray();
        if (checks.Length == 0 || checks.DistinctBy(c => c.Id).Count() != checks.Length || !checks.Any(c => c.Id.StartsWith("runtime-", StringComparison.Ordinal))) throw new InvalidOperationException();
        var expectedChecks = RuntimeChecks.Concat(status == "Unavailable" ? ["source-unavailable"] : SourceChecks).Order().ToArray();
        if (!checks.Select(c => c.Id).Order().SequenceEqual(expectedChecks) ||
            (status == "AwaitingPipelineVerification" && checks.Any(c => c.Id.StartsWith("source-", StringComparison.Ordinal) && c.Status != "Configured"))) throw new InvalidOperationException();
        var limitations = Array(r.GetProperty("limitations"), 12).Select(x => Text(x, 1200)).ToArray();
        return new(1, environment, region, date, "ReviewedSourceOnly", status, false, false, Text(r.GetProperty("poolName"), 100), mode,
            "Same VNet / Azure DNS / IPv4", "azure-pipelines-fabric-workspace.yml", sources, checks, limitations);
    }
    static void Keys(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var properties = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Length != expected.Length || !properties.Order().SequenceEqual(expected.Order())) throw new InvalidOperationException();
    }
    static JsonElement[] Array(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw new InvalidOperationException();
        return value.EnumerateArray().ToArray();
    }
    static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > maximum || text.Any(char.IsControl) || Regex.IsMatch(text, @"(?i)Bearer\s+\S+|AccountKey=|SharedAccessSignature=|client_secret=|-----BEGIN .*PRIVATE KEY")) throw new InvalidOperationException();
        return text;
    }
}
