using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AgentTests
{
    static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);
    static JsonElement Model(string name = "gpt-6.1-sol", string effort = "ultra", string tier = "default") => Json(new { model = name, supportedReasoningEfforts = new[] { new { reasoningEffort = effort } }, serviceTiers = new[] { new { id = tier } } });
    static JsonElement Thread(string tier = "default", bool network = false, string model = "gpt-6.1-sol", string effort = "ultra") => Json(new { model, reasoningEffort = effort, serviceTier = tier, approvalPolicy = "never", sandbox = new { type = "readOnly", networkAccess = network } });
    [Fact] public void NativeExecutableWinsOverEarlierNpmVendorFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-resolution-" + Guid.NewGuid().ToString("N"));
        var npmDirectory = Path.Combine(root, "npm"); var nativeDirectory = Path.Combine(root, "native");
        var arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        var vendor = Path.Combine(npmDirectory, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-" + (arm ? "arm64" : "x64"), "vendor", arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc", "bin", "codex.exe");
        var native = Path.Combine(nativeDirectory, "codex.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(vendor)!); Directory.CreateDirectory(nativeDirectory);
            File.WriteAllText(vendor, "fixture"); File.WriteAllText(native, "fixture");
            var path = npmDirectory + Path.PathSeparator + nativeDirectory;
            Assert.Equal(native, CodexAgentRuntime.FindExecutable("", path));
            Assert.Equal(vendor, CodexAgentRuntime.FindExecutable(vendor, path));
            Assert.Null(CodexAgentRuntime.FindExecutable(Path.Combine(root, "missing", "codex.exe"), path));
            File.Delete(native);
            Assert.Equal(vendor, CodexAgentRuntime.FindExecutable("", path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory][InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public async Task AuthAndModelReadinessAreDistinct(bool signedIn, bool available)
    {
        var calls = new List<string>();
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct)
        {
            calls.Add(method);
            return Task.FromResult(method == "account/read"
                ? Json(new { account = signedIn ? new { type = "chatgpt" } : null })
                : Json(new { data = available ? new[] { Model() } : Array.Empty<JsonElement>(), nextCursor = (string?)null }));
        }
        var status = Json(await CodexAgentRuntime.ReadStatus(Protocol, false, "0.160.0", CancellationToken.None));
        Assert.Equal(signedIn, status.GetProperty("authenticated").GetBoolean());
        Assert.Equal(signedIn && available, status.GetProperty("ready").GetBoolean());
        Assert.Equal("0.160.0", status.GetProperty("runtimeVersion").GetString());
        Assert.Equal(signedIn ? 2 : 1, calls.Count);
        if (signedIn && !available) Assert.Contains("runtime's catalog", status.GetProperty("state").GetString());
    }
    [Fact] public async Task CatalogFailurePreservesVerifiedSignInButBlocksReview()
    {
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct) => method == "account/read"
            ? Task.FromResult(Json(new { account = new { type = "chatgpt" } }))
            : throw new PortalException("fixture failure", 502);
        var status = Json(await CodexAgentRuntime.ReadStatus(Protocol, false, "0.160.0", CancellationToken.None));
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.False(status.GetProperty("ready").GetBoolean());
    }
    [Fact] public async Task PaginatedCatalogStillRequiresUltra()
    {
        var page = 0;
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct) => Task.FromResult(method == "account/read"
            ? Json(new { account = new { type = "chatgpt" } })
            : ++page == 1 ? Json(new { data = Array.Empty<JsonElement>(), nextCursor = "next" })
            : Json(new { data = new[] { Model(effort: "high") }, nextCursor = (string?)null }));
        var status = Json(await CodexAgentRuntime.ReadStatus(Protocol, false, null, CancellationToken.None));
        Assert.Equal(2, page); Assert.True(status.GetProperty("authenticated").GetBoolean()); Assert.False(status.GetProperty("ready").GetBoolean());
    }
    [Fact] public void ExactRequestedSettingsAreAccepted() { AgentPolicy.ValidateModel(Model()); AgentPolicy.ValidateThread(Thread()); }
    [Theory][InlineData("unrequested-model", "ultra")][InlineData("gpt-6.1-sol", "high")][InlineData("gpt-6.1-sol", "max")]
    public void ModelFallbackAndDifferentReasoningAreBlocked(string model, string effort) => Assert.Throws<PortalException>(() => AgentPolicy.ValidateModel(Model(model, effort)));
    [Fact] public void StandardDoesNotRequireAnOptionalCatalogTier()
    {
        AgentPolicy.ValidateModel(Model(tier: "priority"));
        AgentPolicy.ValidateModel(Json(new { model = "gpt-6.1-sol", supportedReasoningEfforts = new[] { new { reasoningEffort = "ultra" } }, serviceTiers = Array.Empty<object>() }));
        Assert.Throws<PortalException>(() => AgentPolicy.ValidateThread(Thread(tier: "priority")));
    }
    [Theory][InlineData("unrequested-model", "ultra")][InlineData("gpt-6.1-sol", "high")][InlineData("gpt-6.1-sol", "max")]
    public void EffectiveThreadCannotDowngradeRequestedModelOrReasoning(string model, string effort) => Assert.Throws<PortalException>(() => AgentPolicy.ValidateThread(Thread(model: model, effort: effort)));
    [Fact] public void BootstrapAndPublicPolicyUseRequestedSettings()
    {
        Assert.Contains("model = \"gpt-6.1-sol\"", AgentPolicy.BootstrapConfiguration);
        Assert.Contains("model_reasoning_effort = \"ultra\"", AgentPolicy.BootstrapConfiguration);
        Assert.Contains("service_tier = \"default\"", AgentPolicy.BootstrapConfiguration);
        var policy = Json(AgentPolicy.Descriptor);
        Assert.Equal("GPT-6.1 Sol", policy.GetProperty("displayName").GetString());
        Assert.Equal("Ultra", policy.GetProperty("effortDisplayName").GetString());
        Assert.Equal("Standard", policy.GetProperty("speed").GetString());
        Assert.False(Json(AgentPolicy.Isolation).GetProperty("features").GetProperty("multi_agent").GetBoolean());
    }
    [Theory][InlineData("priority", false)][InlineData("default", true)]
    public void EffectiveThreadMustRespectIsolation(string tier, bool network) => Assert.Throws<PortalException>(() => AgentPolicy.ValidateThread(Thread(tier, network)));
    [Theory][InlineData("http://auth.openai.com/login")][InlineData("https://auth.openai.com.evil.test/")][InlineData("https://user@auth.openai.com/")][InlineData("https://auth.openai.com:444/")][InlineData("file:///C:/secret")]
    public void LoginRejectsUnexpectedOrigin(string url) => Assert.Throws<PortalException>(() => AgentPolicy.LoginUri(url));
    [Fact] public void LoginAllowsExactOfficialHost() => Assert.Equal("auth.openai.com", AgentPolicy.LoginUri("https://auth.openai.com/oauth/authorize?state=example").Host);
    [Fact] public void ReadinessDoesNotEquateAzureAndAdo()
    {
        using var s = new BrowserSession(new());
        Assert.False(AgentWorkflows.Authorized(s, AgentWorkflows.Find("visualize")));
        s.Connected["azure"] = true;
        Assert.True(AgentWorkflows.Authorized(s, AgentWorkflows.Find("visualize")));
        Assert.False(AgentWorkflows.Authorized(s, AgentWorkflows.Find("preview")));
        Assert.True(AgentWorkflows.Authorized(s, AgentWorkflows.Find("workload")));
        s.Connected.Clear(); Assert.False(AgentWorkflows.Authorized(s, AgentWorkflows.Find("network")));
        Assert.Throws<PortalException>(() => AgentWorkflows.Find("deploy"));
    }
    static JsonElement Call(string name, object? args = null) => Json(new { tool = name, arguments = args ?? new { } });
    [Fact] public async Task RealMcpRoundTripReadsOnlyThisRunsSnapshots()
    {
        await using var bridge = await AgentMcpBridge.Create("fixture-evidence-A", "fixture-skill-A", () => true, CancellationToken.None);
        Assert.Equal(2, bridge.Tools.Length);
        var result = Json(await bridge.Call(Call("platform_evidence"), CancellationToken.None));
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("fixture-evidence-A", result.GetProperty("contentItems")[0].GetProperty("text").GetString());
        Assert.False(bridge.EvidenceRead);
        await bridge.Call(Call("platform_skill"), CancellationToken.None);
        Assert.True(bridge.EvidenceRead); Assert.Equal(2, bridge.Audit.Count);
        await using var other = await AgentMcpBridge.Create("fixture-evidence-B", "fixture-skill-B", () => true, CancellationToken.None);
        Assert.DoesNotContain("fixture-evidence-A", JsonSerializer.Serialize(await other.Call(Call("platform_evidence"), CancellationToken.None)));
    }
    [Fact] public async Task McpRejectsScopeOverridesAndUnknownToolsAndRevokedSession()
    {
        var authorized = true;
        await using var bridge = await AgentMcpBridge.Create("evidence", "skill", () => authorized, CancellationToken.None);
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Call("az_deploy"), CancellationToken.None));
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Call("platform_evidence", new { subscription = "another" }), CancellationToken.None));
        authorized = false;
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Call("platform_evidence"), CancellationToken.None));
        Assert.Equal(3, bridge.Audit.Count);
        Assert.All(bridge.Audit, row => Assert.False(Json(row).GetProperty("success").GetBoolean()));
    }
    [Fact] public async Task McpHasBoundedToolBudget()
    {
        await using var bridge = await AgentMcpBridge.Create("evidence", "skill", () => true, CancellationToken.None);
        for (var i = 0; i < 12; i++) await bridge.Call(Call("platform_evidence"), CancellationToken.None);
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Call("platform_evidence"), CancellationToken.None));
    }
    [NativeCodexFact] public async Task NativeHostHasIsolatedUnauthenticatedAccountAndNoInheritedMcp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var runtime = await CodexAgentRuntime.Start(new() { RepositoryRoot = dir!.FullName }, timeout.Token);
        var status = Json(await runtime.Status(timeout.Token));
        Assert.False(status.GetProperty("ready").GetBoolean());
        var servers = await runtime.Call("mcpServerStatus/list", new { limit = 100 }, timeout.Token);
        Assert.Empty(servers.GetProperty("data").EnumerateArray());
        // Exercises the real browser-login protocol without opening a browser, consenting or running inference.
        var url = await runtime.Login(timeout.Token);
        Assert.Equal("https", new Uri(url).Scheme); Assert.NotNull(runtime.LoginId);
        await runtime.CancelLogin(timeout.Token); Assert.Null(runtime.LoginId);
        Assert.False(Json(await runtime.Status(timeout.Token)).GetProperty("ready").GetBoolean());
    }
}
public sealed class NativeCodexFactAttribute : FactAttribute
{
    public NativeCodexFactAttribute() { if (Environment.GetEnvironmentVariable("PLATFORM_TEST_CODEX") != "1") Skip = "Opt-in native Codex handshake; no login or inference. Set PLATFORM_TEST_CODEX=1."; }
}
