using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class CodexIsolationTests
{
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    static object Server(string? state = "disabled", object? tools = null, object[]? resources = null, object[]? templates = null) => new
    {
        name = "synthetic-registration", runtimeStatus = state, authStatus = "unknown",
        tools = tools ?? new { }, resources = resources ?? [], resourceTemplates = templates ?? []
    };
    [Fact] public void DisabledRegistrationWithNoCapabilitiesIsNotAnExposedTool()
    {
        AgentPolicy.ValidateMcpIsolation(Json(new { data = new[] { Server() }, nextCursor = (string?)null }));
        AgentPolicy.ValidateMcpIsolation(Json(new { data = Array.Empty<object>() }));
    }
    [Theory]
    [InlineData("connected")][InlineData("starting")][InlineData("notStarted")][InlineData("authenticationRequired")][InlineData("failed")][InlineData("cancelled")][InlineData(null)]
    public void RuntimeMustProveRegistrationIsDisabledEvenWhenCatalogIsEmpty(string? state)
        => Assert.Throws<PortalException>(() => AgentPolicy.ValidateMcpIsolation(Json(new { data = new[] { Server(state) } })));
    [Theory][InlineData("tool")][InlineData("resource")][InlineData("template")]
    public void DisabledLabelCannotHideAnExposedCapability(string capability)
    {
        var server = Server(tools: capability == "tool" ? new { unauthorized_tool = new { name = "unauthorized_tool", inputSchema = new { type = "object" } } } : null,
            resources: capability == "resource" ? [new { uri = "synthetic://resource" }] : null,
            templates: capability == "template" ? [new { uriTemplate = "synthetic://resource/{id}" }] : null);
        Assert.Throws<PortalException>(() => AgentPolicy.ValidateMcpIsolation(Json(new { data = new[] { server } })));
    }
    [Theory][InlineData("{}")][InlineData("{\"data\":{}}")][InlineData("{\"data\":[{\"runtimeStatus\":\"disabled\"}]}")]
    public void MissingCapabilityEvidenceCannotPassIsolation(string response)
    {
        using var document = JsonDocument.Parse(response);
        Assert.Throws<PortalException>(() => AgentPolicy.ValidateMcpIsolation(document.RootElement));
    }
    [Fact] public void ThreadOverridesDisableOnlyActualConfiguredNamesIncludingLiteralDots()
    {
        string[] names = ["existing-provider", "provider.with.literal.dots"];
        var isolated = Json(AgentPolicy.IsolatedThreadConfiguration(names));
        var servers = isolated.GetProperty("mcp_servers");
        Assert.Equal(names.Order(), servers.EnumerateObject().Select(property => property.Name).Order());
        Assert.All(names, name => Assert.False(servers.GetProperty(name).GetProperty("enabled").GetBoolean()));
        Assert.False(servers.TryGetProperty("codex_apps", out _));
        var empty = Json(AgentPolicy.IsolatedThreadConfiguration([]));
        Assert.False(empty.TryGetProperty("mcp_servers", out var emptyServers) && emptyServers.EnumerateObject().Any());
    }
    [Fact] public void StartupAndThreadPoliciesDisableAmbientRuntimeCapabilities()
    {
        string[] features = ["apps", "plugins", "remote_plugin", "code_mode_host", "computer_use", "browser_use", "in_app_browser", "image_generation", "workspace_dependencies", "shell_tool"];
        var thread = Json(AgentPolicy.IsolatedThreadConfiguration([]));
        Assert.All(features, name => Assert.False(thread.GetProperty("features").GetProperty(name).GetBoolean()));
        Assert.False(thread.GetProperty("apps").GetProperty("_default").GetProperty("enabled").GetBoolean());
        Assert.Contains("[features]", AgentPolicy.BootstrapConfiguration);
        Assert.All(features, name => Assert.Matches("(?m)^" + name + "\\s*=\\s*false\\s*$", AgentPolicy.BootstrapConfiguration));
        Assert.Contains("[apps._default]", AgentPolicy.BootstrapConfiguration);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"config\":null}")]
    [InlineData("{\"config\":{}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":null}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":\"16384\"}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":0}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":10000}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":32768}}")]
    [InlineData("{\"config\":{\"tool_output_token_limit\":16384,\"tool_output_token_limit\":16384}}")]
    public void MissingAmbiguousOrAlteredNativeToolBudgetsCannotPassPreflight(string response)
    {
        using var configuration = JsonDocument.Parse(response);
        Assert.Throws<PortalException>(() => AgentPolicy.ValidateToolOutputBudget(configuration.RootElement));
    }
    [Fact] public void ExplicitBoundedNativeToolBudgetIsRequiredInBothPolicies()
    {
        Assert.Equal(16384, AgentPolicy.ToolOutputTokenLimit);
        AgentPolicy.ValidateToolOutputBudget(Json(new { config = new { tool_output_token_limit = AgentPolicy.ToolOutputTokenLimit } }));
        Assert.Matches("(?m)^tool_output_token_limit\\s*=\\s*16384\\s*$", AgentPolicy.BootstrapConfiguration);
        Assert.Equal(AgentPolicy.ToolOutputTokenLimit, Json(AgentPolicy.IsolatedThreadConfiguration([])).GetProperty("tool_output_token_limit").GetInt32());
    }
    [Fact] public void BrokerNamespaceRemainsDirectlyVisibleForCodeModeOnlyModelsWithoutEnablingCodeHost()
    {
        var thread = Json(AgentPolicy.IsolatedThreadConfiguration([]));
        var features = thread.GetProperty("features");
        Assert.False(features.GetProperty("code_mode_host").GetBoolean());
        var codeMode = features.GetProperty("code_mode");
        Assert.False(codeMode.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { AgentPolicy.ToolNamespace }, codeMode.GetProperty("direct_only_tool_namespaces").EnumerateArray().Select(value => value.GetString()));
        Assert.False(thread.TryGetProperty("code_mode", out _));
        Assert.Contains("[features.code_mode]", AgentPolicy.BootstrapConfiguration);
        Assert.Contains("direct_only_tool_namespaces = [\"" + AgentPolicy.ToolNamespace + "\"]", AgentPolicy.BootstrapConfiguration);
    }
    [Fact] public async Task InventoryVerificationUsesThreadScopeFullCapabilitiesAndEveryPage()
    {
        var cursors = new List<string?>();
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct)
        {
            Assert.Equal("mcpServerStatus/list", method);
            var request = Json(parameters); Assert.Equal("thread-fixture", request.GetProperty("threadId").GetString()); Assert.Equal("full", request.GetProperty("detail").GetString());
            cursors.Add(request.GetProperty("cursor").GetString());
            return Task.FromResult(Json(new { data = new[] { Server() }, nextCursor = cursors.Count == 1 ? "page-two" : null }));
        }
        await CodexAgentRuntime.VerifyMcpIsolation(Protocol, "thread-fixture", CancellationToken.None);
        Assert.Equal(new string?[] { null, "page-two" }, cursors);
    }
    [Fact] public async Task LaterPageCannotHideAnActiveInheritedTool()
    {
        var calls = 0;
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct) => Task.FromResult(++calls == 1
            ? Json(new { data = new[] { Server() }, nextCursor = "page-two" })
            : Json(new { data = new[] { Server("connected", new { hidden_tool = new { } }) }, nextCursor = (string?)null }));
        await Assert.ThrowsAsync<PortalException>(() => CodexAgentRuntime.VerifyMcpIsolation(Protocol, "thread-fixture", CancellationToken.None));
        Assert.Equal(2, calls);
    }
    [Theory][InlineData("repeat")][InlineData("")][InlineData("unique-forever")]
    public async Task RepeatingEmptyAndUnboundedPaginationCannotPassIsolation(string behavior)
    {
        var calls = 0;
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(Json(new { data = new[] { Server() }, nextCursor = behavior == "unique-forever" ? "page-" + calls : behavior }));
        }
        await Assert.ThrowsAsync<PortalException>(() => CodexAgentRuntime.VerifyMcpIsolation(Protocol, "thread-fixture", CancellationToken.None));
        Assert.InRange(calls, 1, 20);
    }
    [NativeCodexFact] public async Task NativeUnauthenticatedThreadAcceptsDisabledRegistrationWithoutInference()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/workloads.json"))) directory = directory.Parent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var runtime = await CodexAgentRuntime.Start(new() { RepositoryRoot = directory!.FullName }, timeout.Token);
        var account = await runtime.Call("account/read", new { refreshToken = false }, timeout.Token);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("account").ValueKind);
        await runtime.Call("config/value/write", new
        {
            keyPath = "mcp_servers", value = new Dictionary<string, object> { ["disabled_fixture"] = new { command = "nonexistent-disabled-fixture.exe", enabled = false } },
            mergeStrategy = "upsert", filePath = Path.Combine(runtime.Home, "config.toml")
        }, timeout.Token);
        await using var bridge = await AgentMcpBridge.Create("synthetic evidence", "synthetic skill", () => true, timeout.Token);
        var thread = await runtime.Call("thread/start", new
        {
            model = AgentPolicy.Model, serviceTier = AgentPolicy.Tier, cwd = runtime.Home, runtimeWorkspaceRoots = Array.Empty<string>(),
            approvalPolicy = "never", approvalsReviewer = "user", sandbox = "read-only", ephemeral = true, allowProviderModelFallback = false,
            personality = "none", environments = Array.Empty<object>(), selectedCapabilityRoots = Array.Empty<object>(),
            config = AgentPolicy.IsolatedThreadConfiguration(["disabled_fixture"]),
            dynamicTools = new[] { new { type = "namespace", name = AgentPolicy.ToolNamespace, description = "Synthetic broker namespace", tools = bridge.Tools } }
        }, timeout.Token);
        AgentPolicy.ValidateThread(thread);
        var threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
        await CodexAgentRuntime.VerifyMcpIsolation(runtime.Call, threadId, timeout.Token);
        var status = await runtime.Call("mcpServerStatus/list", new { threadId, limit = 100, detail = "full" }, timeout.Token);
        Assert.Contains(status.GetProperty("data").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "disabled_fixture" && entry.GetProperty("runtimeStatus").GetString() == "disabled");
        var configuration = await runtime.Call("config/read", new { cwd = runtime.Home, includeLayers = false }, timeout.Token);
        AgentPolicy.ValidateToolOutputBudget(configuration);
        Assert.Equal(new[] { AgentPolicy.ToolNamespace }, configuration.GetProperty("config").GetProperty("features").GetProperty("code_mode").GetProperty("direct_only_tool_namespaces").EnumerateArray().Select(value => value.GetString()));
        await runtime.Call("config/value/write", new { keyPath = "tool_output_token_limit", value = 8192, mergeStrategy = "upsert", filePath = Path.Combine(runtime.Home, "config.toml") }, timeout.Token);
        var altered = await runtime.Call("config/read", new { cwd = runtime.Home, includeLayers = false }, timeout.Token);
        Assert.Throws<PortalException>(() => AgentPolicy.ValidateToolOutputBudget(altered));
        Assert.False(runtime.InferenceAttempted);
    }
}
