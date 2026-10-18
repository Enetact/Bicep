using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class McpAuthorizationTests
{
    [Fact] public async Task ReconnectingSameAudienceCannotAuthorizePriorRun()
    {
        using var session = new BrowserSession(new());
        var workflow = AgentWorkflows.Find("visualize");
        session.Connected["azure"] = true;
        var epoch = Interlocked.Read(ref session.MicrosoftIdentityEpoch);
        using var run = new CancellationTokenSource(); session.Agent.Run = run;
        await using var bridge = await AgentMcpBridge.Create("frozen evidence", "pinned skill", () => AgentWorkflows.Authorized(session, workflow, epoch), CancellationToken.None);
        var call = JsonSerializer.SerializeToElement(new { tool = "platform_evidence", arguments = new { } });
        await bridge.Call(call, CancellationToken.None);

        session.ClearMicrosoftEvidence();
        session.Connected["azure"] = true;

        Assert.True(run.IsCancellationRequested);
        Assert.True(AgentWorkflows.Authorized(session, workflow));
        Assert.False(AgentWorkflows.Authorized(session, workflow, epoch));
        Assert.True(AgentWorkflows.Authorized(session, workflow, Interlocked.Read(ref session.MicrosoftIdentityEpoch)));
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(call, CancellationToken.None));
        session.Agent.Run = null;
    }
    [Fact] public void IdentityChangeAlsoInvalidatesWorkflowWithoutRequiredAudience()
    {
        using var session = new BrowserSession(new());
        var workflow = AgentWorkflows.Find("workload");
        var epoch = Interlocked.Read(ref session.MicrosoftIdentityEpoch);
        session.ClearMicrosoftEvidence();
        Assert.True(AgentWorkflows.Authorized(session, workflow));
        Assert.False(AgentWorkflows.Authorized(session, workflow, epoch));
    }
    [Fact] public void InferenceEnvironmentPreservesOnlySystemEssentialsAndOwnedHome()
    {
        string[] essentials = ["PATH", "PATHEXT", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH"];
        string[] credentials = ["GH_TOKEN", "GITHUB_TOKEN", "ARM_CLIENT_SECRET", "SYSTEM_ACCESSTOKEN", "AZURE_DEVOPS_EXT_PAT", "ADO_PAT", "OPENAI_API_KEY", "AZURE_CLIENT_SECRET", "CUSTOM_PASSWORD", "CODEX_HOME", "CODEX_API_KEY"];
        var environment = essentials.Concat(credentials).ToDictionary(key => key, key => (string?)("fixture-" + key), StringComparer.OrdinalIgnoreCase);
        CodexAgentRuntime.ConfigureEnvironment(environment, "owned-runtime-home");
        Assert.All(essentials, key => Assert.Equal("fixture-" + key, environment[key]));
        Assert.All(credentials.Where(key => key != "CODEX_HOME"), key => Assert.False(environment.ContainsKey(key)));
        Assert.Equal("owned-runtime-home", environment["CODEX_HOME"]);
        Assert.Equal(essentials.Length + 1, environment.Count);
    }
    [Fact] public void EnvironmentAllowlistMatchesWindowsVariableCase()
    {
        IDictionary<string, string?> environment = new Dictionary<string, string?> { ["Path"] = "native-path", ["SystemRoot"] = "windows", ["github_token"] = "fixture-token" };
        CodexAgentRuntime.ConfigureEnvironment(environment, "owned-home");
        Assert.Equal("native-path", environment["Path"]);
        Assert.Equal("windows", environment["SystemRoot"]);
        Assert.False(environment.ContainsKey("github_token"));
        Assert.Equal(3, environment.Count);
    }
}
