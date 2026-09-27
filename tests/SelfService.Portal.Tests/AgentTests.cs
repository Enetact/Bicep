using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AgentTests
{
    static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);
    static JsonElement Model(string name = "gpt-6-astra", string effort = "high", string tier = "default") => Json(new { model = name, supportedReasoningEfforts = new[] { new { reasoningEffort = effort } }, serviceTiers = new[] { new { id = tier } } });
    static JsonElement Thread(string tier = "default", bool network = false) => Json(new { model = AgentPolicy.Model, reasoningEffort = AgentPolicy.Effort, serviceTier = tier, approvalPolicy = "never", sandbox = new { type = "readOnly", networkAccess = network } });
    [Fact] public void ExactRequestedSettingsAreAccepted() { AgentPolicy.ValidateModel(Model()); AgentPolicy.ValidateThread(Thread()); }
    [Theory][InlineData("gpt-6-sol", "high", "default")][InlineData("gpt-6-astra", "max", "default")][InlineData("gpt-6-astra", "high", "priority")]
    public void ModelFallbackAndDifferentReasoningOrSpeedAreBlocked(string model, string effort, string tier) => Assert.Throws<PortalException>(() => AgentPolicy.ValidateModel(Model(model, effort, tier)));
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
