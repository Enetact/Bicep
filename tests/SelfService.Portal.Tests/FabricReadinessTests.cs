using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class FabricReadinessTests
{
    static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/workloads.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
    static PortalOptions Options(string? root = null) => new() { RepositoryRoot = root ?? Root() };
    static readonly string[] SourceIds = ["source-onboarding", "source-identity", "source-operation", "source-pool-binding", "source-pool-profile", "source-region", "source-private-workspace"];
    static readonly string[] RuntimeIds = ["runtime-managed-pool", "runtime-agent-subnet", "runtime-agent-job", "runtime-private-endpoint", "runtime-private-reachability", "runtime-public-policy", "runtime-plan-verification"];
    static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static Dictionary<string, object?> Valid(string environment = "dev") => new()
    {
        ["schemaVersion"] = 1, ["environment"] = environment, ["region"] = "eastus2", ["checkedUtc"] = DateTimeOffset.UtcNow,
        ["scope"] = "ReviewedSourceOnly", ["status"] = "NeedsConfiguration", ["canDeploy"] = false, ["liveVerified"] = false,
        ["poolName"] = $"fabric-{environment}-agents", ["workspaceMode"] = "Bootstrap", ["privateProfile"] = "Same VNet / Azure DNS / IPv4",
        ["pipeline"] = "azure-pipelines-fabric-workspace.yml", ["sources"] = new[] { new { path = $"self-service/fabric/workspace.{environment}.json", sha256 = new string('a', 64) } },
        ["checks"] = SourceIds.Select(id => new FabricReadinessCheck(id, "Reviewed source requirement", "NeedsConfiguration", "Complete reviewed source configuration."))
            .Concat(RuntimeIds.Select(id => new FabricReadinessCheck(id, "Pipeline requirement", "NotVerified", "The protected pipeline must verify this check."))).ToArray(),
        ["limitations"] = new[] { "Deny-public workspace native deployment pipelines remain unsupported." }
    };
    sealed class Runner : IFabricReadinessRunner
    {
        public byte[] Output = Bytes(Valid());
        public int ExitCode, Calls;
        public ProcessStartInfo? Start;
        public Func<CancellationToken, Task>? BeforeReturn;
        public Exception? Failure;
        public async Task<FabricReadinessProcessResult> Run(ProcessStartInfo start, CancellationToken cancellation)
        {
            Calls++; Start = start;
            if (BeforeReturn is not null) await BeforeReturn(cancellation);
            if (Failure is not null) throw Failure;
            return new(ExitCode, Output);
        }
    }
    static FabricReadiness Service(Runner runner, PortalOptions? options = null)
    {
        var value = options ?? Options();
        return new(value, new(value), runner);
    }
    [Fact] public async Task SourceAssessmentHasNoDeploymentAuthorityAndNeverTurnsPipelineChecksGreen()
    {
        var runner = new Runner();
        var result = await Service(runner).Read("dev", "eastus2", CancellationToken.None);
        Assert.Equal("ReviewedSourceOnly", result.Scope);
        Assert.False(result.CanDeploy); Assert.False(result.LiveVerified);
        Assert.Equal(7, result.Checks.Count(c => c.Id.StartsWith("runtime-")));
        Assert.All(result.Checks.Where(c => c.Id.StartsWith("runtime-")), c => Assert.Equal("NotVerified", c.Status));
        Assert.Equal("fabric-dev-agents", result.PoolName);
        Assert.Equal(1, runner.Calls);
    }
    [Fact] public async Task CompleteSourceConfigurationStillRequiresAllPipelineProofs()
    {
        var value = Valid(); value["status"] = "AwaitingPipelineVerification"; value["workspaceMode"] = "Private";
        value["checks"] = ((FabricReadinessCheck[])value["checks"]!).Select(c => c.Id.StartsWith("source-") ? c with { Status = "Configured" } : c).ToArray();
        var result = await Service(new() { Output = Bytes(value) }).Read("dev", "eastus2", CancellationToken.None);
        Assert.Equal("AwaitingPipelineVerification", result.Status);
        Assert.False(result.CanDeploy); Assert.False(result.LiveVerified);
        Assert.All(result.Checks.Where(c => c.Id.StartsWith("runtime-")), c => Assert.Equal("NotVerified", c.Status));
    }
    [Fact] public async Task IncompleteSourceCannotClaimConfigurationIsComplete()
    {
        var value = Valid(); value["status"] = "AwaitingPipelineVerification";
        await Assert.ThrowsAsync<PortalException>(() => Service(new() { Output = Bytes(value) }).Read("dev", "eastus2", CancellationToken.None));
    }
    [Fact] public async Task MissingSourceProjectionRemainsUnavailableAndNonExecutable()
    {
        var value = Valid(); value["status"] = "Unavailable"; value["workspaceMode"] = "Unconfigured"; value["sources"] = System.Array.Empty<object>();
        value["checks"] = new[] { new FabricReadinessCheck("source-unavailable", "Reviewed source configuration", "NeedsConfiguration", "Review missing source configuration locally.") }
            .Concat(((FabricReadinessCheck[])value["checks"]!).Where(c => c.Id.StartsWith("runtime-"))).ToArray();
        var result = await Service(new() { Output = Bytes(value) }).Read("dev", "eastus2", CancellationToken.None);
        Assert.Equal("Unavailable", result.Status); Assert.False(result.LiveVerified); Assert.False(result.CanDeploy);
        Assert.Equal(8, result.Checks.Length);
    }
    [Fact] public async Task ChildUsesFixedArgumentListAndNoServiceCredentials()
    {
        var runner = new Runner();
        await Service(runner).Read("dev", "eastus2", CancellationToken.None);
        var start = Assert.IsType<ProcessStartInfo>(runner.Start);
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(Root(), start.WorkingDirectory);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(Root(), "scripts/Get-FabricReadiness.ps1"), "-EnvironmentName", "dev", "-Region", "eastus2" }, start.ArgumentList);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ARM_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SYSTEM_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("STUDIO_", StringComparison.OrdinalIgnoreCase) || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k.Contains("SECRET", StringComparison.OrdinalIgnoreCase));
    }
    [Theory][InlineData("../qa", "eastus2")][InlineData("DEV", "eastus2")][InlineData("missing", "eastus2")][InlineData("dev", "eastus2;write")][InlineData("dev", "missingregion")]
    public async Task InvalidScopeRejectedBeforeRunner(string environment, string region)
    {
        var runner = new Runner();
        await Assert.ThrowsAsync<PortalException>(() => Service(runner).Read(environment, region, CancellationToken.None));
        Assert.Equal(0, runner.Calls);
    }
    [Theory][InlineData("schemaVersion")][InlineData("environment")][InlineData("region")][InlineData("scope")][InlineData("canDeploy")][InlineData("liveVerified")][InlineData("status")][InlineData("workspaceMode")][InlineData("pipeline")][InlineData("privateProfile")][InlineData("checkedUtc")]
    public async Task WrongScopeOrExecutionClaimsAreWithheld(string field)
    {
        var value = Valid(); value[field] = field switch { "schemaVersion" => 2, "canDeploy" or "liveVerified" => true, "checkedUtc" => DateTimeOffset.UtcNow.AddHours(-1), _ => "SYNTHETIC_SENSITIVE_UNTRUSTED_VALUE" };
        var runner = new Runner { Output = Bytes(value) };
        var error = await Assert.ThrowsAsync<PortalException>(() => Service(runner).Read("dev", "eastus2", CancellationToken.None));
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE", error.Message);
        Assert.Contains("unavailable", error.Message);
    }
    [Theory][InlineData("malformed")][InlineData("singleton-array")][InlineData("null")][InlineData("extra-field")][InlineData("missing-field")][InlineData("oversize")][InlineData("duplicate-field")]
    public async Task MalformedOrOversizedResponsesAreWithheld(string fault)
    {
        var value = Valid();
        var bytes = fault switch
        {
            "malformed" => Encoding.UTF8.GetBytes("{SYNTHETIC_SENSITIVE_JSON"),
            "singleton-array" => Bytes(new[] { value }), "null" => Encoding.UTF8.GetBytes("null"),
            "oversize" => new byte[FabricReadiness.MaximumOutputBytes + 1],
            "duplicate-field" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bytes(value)).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")),
            _ => []
        };
        if (fault == "extra-field") { value["accessToken"] = "SYNTHETIC_SENSITIVE_TOKEN"; bytes = Bytes(value); }
        if (fault == "missing-field") { value.Remove("checks"); bytes = Bytes(value); }
        var runner = new Runner { Output = bytes };
        var error = await Assert.ThrowsAsync<PortalException>(() => Service(runner).Read("dev", "eastus2", CancellationToken.None));
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE", error.Message);
    }
    [Theory][InlineData("runtime-green")][InlineData("missing-runtime")][InlineData("duplicate-check")][InlineData("wrong-source")][InlineData("secret-detail")][InlineData("source-path")][InlineData("source-hash")]
    public async Task CheckAndProvenanceCorruptionCannotBeDisplayedAsReadiness(string fault)
    {
        var value = Valid();
        var checks = (FabricReadinessCheck[])value["checks"]!;
        if (fault == "runtime-green") checks[^1] = checks[^1] with { Status = "Configured" };
        if (fault == "missing-runtime") value["checks"] = checks[..^1];
        if (fault == "duplicate-check") checks[^1] = checks[^2];
        if (fault == "wrong-source") checks[0] = checks[0] with { Id = "source-invented" };
        if (fault == "secret-detail") checks[0] = checks[0] with { Detail = "Bearer SYNTHETIC_SENSITIVE_TOKEN" };
        if (fault == "source-path") value["sources"] = new[] { new { path = "../credentials.json", sha256 = new string('a', 64) } };
        if (fault == "source-hash") value["sources"] = new[] { new { path = "self-service/fabric/workspace.dev.json", sha256 = "incorrect" } };
        var runner = new Runner { Output = Bytes(value) };
        var error = await Assert.ThrowsAsync<PortalException>(() => Service(runner).Read("dev", "eastus2", CancellationToken.None));
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE", error.Message);
    }
    [Fact] public async Task ProcessFailureDoesNotExposeOutputOrExceptionDetails()
    {
        foreach (var runner in new[] { new Runner { ExitCode = 1, Output = Encoding.UTF8.GetBytes("SYNTHETIC_SENSITIVE_STDOUT") }, new Runner { Failure = new PortalException("SYNTHETIC_SENSITIVE_RUNNER_ERROR") } })
        {
            var error = await Assert.ThrowsAsync<PortalException>(() => Service(runner).Read("dev", "eastus2", CancellationToken.None));
            Assert.DoesNotContain("SYNTHETIC_SENSITIVE", error.Message);
        }
    }
    [Theory][InlineData("missing")][InlineData("oversized")][InlineData("linked")]
    public async Task UntrustedReadinessCodeIsRejectedBeforeAnyProcess(string fault)
    {
        var root = Path.GetFullPath(Path.Combine(Root(), "artifacts/fabric-readiness-backend-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, "config")); Directory.CreateDirectory(Path.Combine(root, "self-service/targets"));
        foreach (var relative in new[] { "config/workloads.json", "config/platform.json", "self-service/pipeline-settings.json", "self-service/targets/fabric.dev.json" }) File.Copy(Path.Combine(Root(), relative), Path.Combine(root, relative));
        foreach (var yaml in Directory.GetFiles(Root(), "azure-pipelines-*-deploy.yml")) File.Copy(yaml, Path.Combine(root, Path.GetFileName(yaml)));
        if (fault == "oversized")
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts/Get-FabricReadiness.ps1"), new string('x', 262145));
        }
        if (fault == "linked")
        {
            var target = Path.Combine(root, "linked-code"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "Get-FabricReadiness.ps1"), "throw 'must not execute'");
            var link = Path.Combine(root, "scripts");
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
                Assert.True(process.ExitCode == 0, "Local junction fixture creation failed: " + output + error);
            }
            else Directory.CreateSymbolicLink(link, target);
        }
        var runner = new Runner();
        var failure = await Assert.ThrowsAsync<PortalException>(() => Service(runner, Options(root)).Read("dev", "eastus2", CancellationToken.None));
        Assert.Contains("unavailable", failure.Message); Assert.Equal(0, runner.Calls);
    }
    [Fact] public async Task ConcurrentAssessmentFailsClosedAndCancellationReleasesGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner { BeforeReturn = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var service = Service(runner);
        using var cancel = new CancellationTokenSource();
        var first = service.Read("dev", "eastus2", cancel.Token); await entered.Task;
        var busy = await Assert.ThrowsAsync<PortalException>(() => service.Read("dev", "eastus2", CancellationToken.None));
        Assert.Equal(409, busy.Status); Assert.Equal(1, runner.Calls);
        cancel.Cancel(); var cancelled = await Assert.ThrowsAsync<PortalException>(() => first); Assert.Equal(408, cancelled.Status);
        runner.BeforeReturn = null;
        Assert.False((await service.Read("dev", "eastus2", CancellationToken.None)).LiveVerified);
    }
    [Theory][InlineData("dev")][InlineData("qa")][InlineData("uat")][InlineData("prod")]
    public async Task RealOfflinePowerShellAssessmentKeepsAllRuntimeChecksUnverified(string environment)
    {
        var options = Options();
        var result = await new FabricReadiness(options, new(options), new FabricReadinessRunner()).Read(environment, "eastus2", CancellationToken.None);
        Assert.Equal(environment, result.Environment); Assert.Equal("NeedsConfiguration", result.Status);
        Assert.False(result.CanDeploy); Assert.False(result.LiveVerified);
        Assert.Equal($"fabric-{environment}-agents", result.PoolName);
        Assert.Equal("Bootstrap", result.WorkspaceMode);
        Assert.Equal(8, result.Sources.Length); Assert.Equal(14, result.Checks.Length);
        Assert.All(result.Checks.Where(c => c.Id.StartsWith("runtime-")), c => Assert.Equal("NotVerified", c.Status));
        Assert.Contains(result.Checks, c => c.Id == "source-onboarding" && c.Status == "NeedsConfiguration");
    }
    [Fact] public void PortalPublisherIncludesTheReadinessSourcesWithoutCopyingLocalCredentials()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "scripts/Publish-Portal.ps1"));
        foreach (var path in new[] { "Get-FabricReadiness.ps1", "fabric-readiness-common.ps1", "fabric-workspace-common.ps1", "fabric-execution-common.ps1", "managed-devops-pool-common.ps1", "pipelines/fabric-execution-bindings.yml", "workloads/managed-devops-pool/environments/main.$environmentName.bicepparam" }) Assert.Contains(path, source);
        Assert.DoesNotContain("Copy-Item -LiteralPath (Join-Path $root '.local'", source);
        Assert.DoesNotContain("Copy-Item -LiteralPath (Join-Path $root '.azure'", source);
    }
}
