using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class McpProviderBrokerTests
{
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    static ProviderTool Tool(string name, bool readOnly = false, object? schema = null, bool sensitiveHint = false) => new(name, "Synthetic provider tool", Json(schema ?? new { type = "object", properties = new { } }), readOnly, false, false, sensitiveHint);
    static McpStep Step(string tool, object? arguments = null, string provider = "ado") => new(provider, tool, Json(arguments ?? new { }));

    sealed class Resolver : IProviderConnectionResolver
    {
        public Task<ProviderConnectionSpec> Resolve(BrowserSession session, string provider, CancellationToken ct) => Task.FromResult(new ProviderConnectionSpec(provider, "fixture-only", [], null, [], [], session.Mcp.Id));
    }
    sealed class FakeClient(ProviderTool[] tools) : IProviderMcpClient
    {
        public ProviderTool[] Tools { get; set; } = tools;
        public List<string> Calls { get; } = [];
        public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Handler { get; set; }
        public bool Disposed { get; private set; }
        public Task<ProviderTool[]> ListTools(CancellationToken ct) => Task.FromResult(Tools);
        public Task<JsonElement> Call(string tool, JsonElement arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(tool);
            return Handler is null ? Task.FromResult(Json(new { content = new[] { new { type = "text", text = "Synthetic result" } }, isError = false })) : Handler(tool, arguments, ct);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    sealed class Factory : IProviderMcpFactory
    {
        public ProviderTool[] Tools { get; set; } = [Tool("list_projects", true), Tool("create_project"), Tool("get_access_token", true)];
        public List<FakeClient> Clients { get; } = [];
        public Task<IProviderMcpClient> Connect(ProviderConnectionSpec spec, CancellationToken ct)
        {
            var client = new FakeClient(Tools); Clients.Add(client); return Task.FromResult<IProviderMcpClient>(client);
        }
    }
    sealed class Fixture : IDisposable
    {
        readonly List<BrowserSession> sessions = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcp-broker-tests-" + Guid.NewGuid().ToString("N"));
        public Factory Factory { get; } = new();
        public McpProviderBroker Broker { get; }
        public Fixture() { Directory.CreateDirectory(Root); Broker = new(new() { RepositoryRoot = Root }, new Resolver(), Factory); }
        public BrowserSession Session()
        {
            var session = new BrowserSession(new()); session.Connected["ado"] = true; session.AzureTenantId = "11111111-1111-1111-1111-111111111111";
            sessions.Add(session); return session;
        }
        public void Dispose()
        {
            foreach (var session in sessions) session.Dispose();
            Directory.Delete(Root, true);
        }
    }

    [Fact] public async Task DiscoveryRetainsReadMutationAndSensitiveTools()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var catalog = Json(fixture.Broker.Catalog(session));
        Assert.Equal(3, catalog.GetProperty("total").GetInt32());
        var effects = catalog.GetProperty("tools").EnumerateArray().ToDictionary(x => x.GetProperty("definition").GetProperty("name").GetString()!, x => x.GetProperty("definition").GetProperty("effect").GetString());
        Assert.Equal("Read", effects["list_projects"]);
        Assert.Equal("ChangeOrUnknown", effects["create_project"]);
        Assert.Equal("Sensitive", effects["get_access_token"]);
    }
    [Fact] public async Task MutationNeedsApprovalForExactDigestAndCannotReplay()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("create_project", new { name = "Synthetic" })]));
        Assert.True(plan.RequiresApproval); Assert.Equal("AwaitingApproval", plan.State);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Empty(fixture.Factory.Clients.Single().Calls);
        Assert.Throws<PortalException>(() => fixture.Broker.Approve(session, plan.Id, new string('0', 64)));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        Assert.Equal("Approved", plan.State);
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        Assert.Equal("Completed", plan.State); Assert.Single(fixture.Factory.Clients.Single().Calls);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Throws<PortalException>(() => fixture.Broker.Approve(session, plan.Id, plan.Digest));
        Assert.Single(fixture.Factory.Clients.Single().Calls);
    }
    [Fact] public async Task ReadToolExecutesWithoutApprovalAndSavesReceipt()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("list_projects")]));
        Assert.False(plan.RequiresApproval); Assert.Null(plan.ApprovedUtc);
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        Assert.Equal("Completed", plan.State); Assert.Single(plan.Receipts);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "artifacts", "portal-mcp", session.Mcp.Id, plan.Id + ".json")));
    }
    [Fact] public async Task LearnCatalogCanRunDocumentReadsWithoutCloudIdentity()
    {
        using var fixture = new Fixture(); var session = fixture.Session(); session.Connected.Clear();
        fixture.Factory.Tools = [Tool("microsoft_docs_search"), Tool("microsoft_docs_fetch"), Tool("microsoft_code_sample_search")];
        await fixture.Broker.Connect(session, "learn", CancellationToken.None);
        var result = Json(await fixture.Broker.AgentCall(session, "learn", "microsoft_docs_search", Json(new { query = "Synthetic query" }), CancellationToken.None));
        Assert.Equal("Completed", result.GetProperty("State").GetString());
        Assert.Single(fixture.Factory.Clients.Single().Calls);
        Assert.Equal(3, Json(fixture.Broker.Catalog(session, "learn")).GetProperty("total").GetInt32());
    }
    [Fact] public async Task AspireUsesRealTelemetryCatalogAndExactPrivateReadApproval()
    {
        using var fixture = new Fixture(); var session = fixture.Session(); session.Connected.Clear();
        fixture.Factory.Tools = [Tool("list_structured_logs", true), Tool("list_traces", true), Tool("list_trace_structured_logs", true)];
        await fixture.Broker.Connect(session, "aspire", CancellationToken.None);
        Assert.Equal("ready", session.Mcp.Connections["aspire"].State);
        Assert.Equal(3, Json(fixture.Broker.Catalog(session, "aspire")).GetProperty("total").GetInt32());
        var pending = Json(await fixture.Broker.AgentCall(session, "aspire", "list_traces", Json(new { resourceName = "synthetic-service" }), CancellationToken.None));
        Assert.Equal("AwaitingApproval", pending.GetProperty("State").GetString());
        var plan = fixture.Broker.ReadPlan(session, pending.GetProperty("Id").GetString()!);
        Assert.True(plan.RequiresApproval); Assert.Empty(fixture.Factory.Clients.Single().Calls);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        Assert.Equal("Completed", plan.State); Assert.Equal(new[] { "list_traces" }, fixture.Factory.Clients.Single().Calls);
        Assert.Contains("Synthetic result", Json(plan.Receipts).GetRawText());
        Assert.Equal("PrivateRead", McpProviderBroker.Effect("aspire", fixture.Factory.Tools[1]));
    }
    [Fact] public async Task AspireRejectsApplicationControlAndChangedTelemetryInventories()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        var telemetry = new[] { Tool("list_structured_logs", true), Tool("list_traces", true), Tool("list_trace_structured_logs", true) };
        fixture.Factory.Tools = [.. telemetry, Tool("execute_resource_command")];
        await fixture.Broker.Connect(session, "aspire", CancellationToken.None);
        Assert.Equal("error", session.Mcp.Connections["aspire"].State);
        Assert.True(fixture.Factory.Clients.Single().Disposed);
        fixture.Factory.Tools = telemetry;
        await fixture.Broker.Connect(session, "aspire", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("list_traces", provider: "aspire")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var client = fixture.Factory.Clients.Last(); client.Tools = [.. telemetry, Tool("execute_resource_command")];
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Empty(client.Calls);
    }
    [Fact] public async Task FailedAspireReadNeverClaimsUnknownExternalMutation()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("list_structured_logs", true), Tool("list_traces", true), Tool("list_trace_structured_logs", true)];
        await fixture.Broker.Connect(session, "aspire", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("list_traces", provider: "aspire")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        fixture.Factory.Clients.Single().Handler = (_, _, _) => throw new InvalidOperationException("synthetic-private-error-text");
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        Assert.Equal("Failed", plan.State); Assert.DoesNotContain("synthetic-private-error-text", Json(plan.Receipts).GetRawText());
    }
    [Fact] public async Task McpSpansRemainChildrenOfCallerAndExcludePayloadsAndExceptionText()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "PlatformStudio.Mcp" or "Synthetic.Mcp.Caller",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var callerSource = new ActivitySource("Synthetic.Mcp.Caller");
        using var caller = callerSource.StartActivity("request")!;
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("list_projects", new { query = "synthetic-private-query" })]));
        fixture.Factory.Clients.Single().Handler = (_, _, _) => throw new InvalidOperationException("synthetic-private-error-text");
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        var captured = spans.Where(s => s.Source.Name == "PlatformStudio.Mcp" && s.TraceId == caller.TraceId).ToArray();
        Assert.Contains(captured, s => s.OperationName == "mcp.connect" && s.ParentSpanId == caller.SpanId);
        var tool = Assert.Single(captured, s => s.OperationName == "mcp.tools.call");
        var execution = Assert.Single(captured, s => s.OperationName == "mcp.plan.execute");
        Assert.Equal(execution.SpanId, tool.ParentSpanId); Assert.Equal(ActivityStatusCode.Error, tool.Status);
        var telemetry = Json(captured.Select(s => new { tags = s.TagObjects.ToArray(), events = s.Events.ToArray(), s.StatusDescription }));
        Assert.DoesNotContain("synthetic-private-query", telemetry.GetRawText());
        Assert.DoesNotContain("synthetic-private-error-text", telemetry.GetRawText());
        Assert.DoesNotContain("Synthetic result", telemetry.GetRawText());
    }
    [Fact] public async Task FabricRequiresSeparateIdentityAndRetainsExactApprovalAndRevocation()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("list_workspaces", true), Tool("create_workspace")];
        await fixture.Broker.Connect(session, "fabric", CancellationToken.None);
        Assert.Empty(fixture.Factory.Clients);
        Assert.Equal("authRequired", session.Mcp.Connections["fabric"].State);
        session.Connected["fabric"] = true;
        await fixture.Broker.Connect(session, "fabric", CancellationToken.None);
        var read = fixture.Broker.Plan(session, new([Step("list_workspaces", provider: "fabric")]));
        Assert.False(read.RequiresApproval);
        await fixture.Broker.Run(session, read.Id, read.Digest, CancellationToken.None);
        Assert.Equal("Completed", read.State);
        var change = fixture.Broker.Plan(session, new([Step("create_workspace", new { displayName = "Synthetic" }, "fabric")]));
        Assert.True(change.RequiresApproval);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, change.Id, change.Digest, CancellationToken.None));
        fixture.Broker.Approve(session, change.Id, change.Digest);
        var connection = session.Mcp.Connections["fabric"];
        session.ClearMicrosoftEvidence(); session.Connected["fabric"] = true;
        Assert.True(connection.Lifetime.IsCancellationRequested);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, change.Id, change.Digest, CancellationToken.None));
        Assert.Equal(new[] { "list_workspaces" }, fixture.Factory.Clients.Single().Calls);
    }
    [Fact] public async Task CatalogAndPlansCannotCrossBrowserSessionsAndMissingPlanReleasesGate()
    {
        using var fixture = new Fixture(); var first = fixture.Session(); var second = fixture.Session();
        await fixture.Broker.Connect(first, "ado", CancellationToken.None);
        var firstPlan = fixture.Broker.Plan(first, new([Step("list_projects")]));
        Assert.Equal(0, Json(fixture.Broker.Catalog(second)).GetProperty("total").GetInt32());
        Assert.Throws<PortalException>(() => fixture.Broker.ReadPlan(second, firstPlan.Id));
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(second, firstPlan.Id, firstPlan.Digest, CancellationToken.None));
        Assert.Equal(1, second.Mcp.Gate.CurrentCount);
        await fixture.Broker.Connect(second, "ado", CancellationToken.None);
        var secondPlan = fixture.Broker.Plan(second, new([Step("list_projects")]));
        Assert.NotEqual(firstPlan.Digest, secondPlan.Digest);
        await fixture.Broker.Run(second, secondPlan.Id, secondPlan.Digest, CancellationToken.None);
        Assert.Empty(fixture.Factory.Clients[0].Calls); Assert.Single(fixture.Factory.Clients[1].Calls);
    }
    [Fact] public async Task ProviderReconnectAndSchemaChangeInvalidateApproval()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("create_project")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var oldCatalog = session.Mcp.Connections["ado"].CatalogDigest;
        fixture.Factory.Tools = [Tool("create_project", schema: new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" } })];
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        Assert.NotEqual(oldCatalog, session.Mcp.Connections["ado"].CatalogDigest);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.All(fixture.Factory.Clients, client => Assert.Empty(client.Calls));
    }
    [Fact] public async Task ReconnectingSameMicrosoftAudienceCannotReviveApprovedPlan()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("create_project")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        session.ClearMicrosoftEvidence(); session.Connected["ado"] = true;
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.All(fixture.Factory.Clients, client => Assert.Empty(client.Calls));
    }
    [Fact] public async Task SchemaDriftOnConnectedTransportIsRejectedBeforeApprovalIsConsumed()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("create_project")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var client = fixture.Factory.Clients.Single();
        client.Tools = [Tool("create_project", schema: new { type = "object", required = new[] { "name" } })];
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Empty(client.Calls);
        Assert.NotEqual("Completed", plan.State);
        Assert.Equal(1, session.Mcp.Gate.CurrentCount);
    }
    [Fact] public async Task MicrosoftDisconnectCancelsInflightMutationAndRecordsUnknownOutcome()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.Clients.Single().Handler = async (_, _, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json(new { isError = false }); };
        var plan = fixture.Broker.Plan(session, new([Step("create_project")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = fixture.Broker.Run(session, plan.Id, plan.Digest, timeout.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            session.ClearMicrosoftEvidence();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("UnknownOutcome", plan.State);
            Assert.Single(fixture.Factory.Clients.Single().Calls);
        }
        finally { timeout.Cancel(); await running; }
    }
    [Fact] public async Task FailedSecondMutationStopsSequenceAndCannotRetryUnknownOutcome()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("create_project"), Tool("update_project"), Tool("delete_project")];
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var client = fixture.Factory.Clients.Single();
        client.Handler = (tool, _, _) => tool == "update_project" ? throw new IOException("Synthetic response lost after submission") : Task.FromResult(Json(new { isError = false, result = "Synthetic success" }));
        var plan = fixture.Broker.Plan(session, new([Step("create_project"), Step("update_project"), Step("delete_project")]));
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None);
        Assert.Equal("UnknownOutcome", plan.State);
        Assert.Equal(new[] { "create_project", "update_project" }, client.Calls);
        Assert.Equal(2, plan.Receipts.Count);
        Assert.Equal("Succeeded", Json(plan.Receipts[0]).GetProperty("status").GetString());
        Assert.Equal("UnknownOutcome", Json(plan.Receipts[1]).GetProperty("status").GetString());
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Equal(2, client.Calls.Count);
    }
    [Fact] public async Task SensitiveToolRetainsCapabilityButWithholdsOutputFromModelAndReceipt()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        fixture.Factory.Clients.Single().Handler = (_, _, _) => Task.FromResult(Json(new { content = new[] { new { text = "synthetic-sensitive-result-never-expose" } }, accessToken = "synthetic-sensitive-token-never-expose" }));
        var proposal = Json(await fixture.Broker.AgentCall(session, "ado", "get_access_token", Json(new { }), CancellationToken.None));
        Assert.Equal("AwaitingApproval", proposal.GetProperty("State").GetString());
        Assert.Empty(fixture.Factory.Clients.Single().Calls);
        var plan = session.Mcp.Plans.Values.Single(); fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var result = Json(await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.True(result.GetProperty("receipts")[0].GetProperty("output").GetProperty("withheld").GetBoolean());
        Assert.DoesNotContain("synthetic-sensitive-result-never-expose", result.GetRawText());
        Assert.DoesNotContain("synthetic-sensitive-token-never-expose", File.ReadAllText(Path.Combine(fixture.Root, "artifacts", "portal-mcp", session.Mcp.Id, plan.Id + ".json")));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SensitiveMetadataRequiresApprovalAndWithholdsInnocuouslyNamedOutput(bool mixedPlan)
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("list_projects", true), Tool("get_settings", true, sensitiveHint: true), Tool("create_project")];
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var client = fixture.Factory.Clients.Single();
        const string sentinel = "synthetic-private-setting-must-never-enter-model-or-receipt";
        client.Handler = (tool, _, _) => Task.FromResult(tool == "get_settings"
            ? Json(new { content = new[] { new { type = "text", text = sentinel } }, isError = false })
            : Json(new { content = new[] { new { type = "text", text = "Synthetic public result" } }, isError = false }));
        McpExecutionPlan plan;
        if (mixedPlan) plan = fixture.Broker.Plan(session, new([Step("list_projects"), Step("get_settings"), Step("create_project")]));
        else
        {
            var proposed = Json(await fixture.Broker.AgentCall(session, "ado", "get_settings", Json(new { }), CancellationToken.None));
            Assert.Equal("AwaitingApproval", proposed.GetProperty("State").GetString());
            plan = session.Mcp.Plans.Values.Single();
        }
        Assert.True(plan.RequiresApproval); Assert.Equal("AwaitingApproval", plan.State);
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Empty(client.Calls);
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var executed = Json(await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.Equal("Completed", plan.State);
        var sensitiveReceipt = executed.GetProperty("receipts").EnumerateArray().Single(receipt => receipt.GetProperty("tool").GetString() == "get_settings");
        Assert.True(sensitiveReceipt.GetProperty("output").GetProperty("withheld").GetBoolean());
        Assert.DoesNotContain(sentinel, executed.GetRawText());
        Assert.DoesNotContain(sentinel, File.ReadAllText(Path.Combine(fixture.Root, "artifacts", "portal-mcp", session.Mcp.Id, plan.Id + ".json")));
        if (mixedPlan)
        {
            Assert.Equal(new[] { "list_projects", "get_settings", "create_project" }, client.Calls);
            Assert.Contains("Synthetic public result", executed.GetRawText());
        }
        else Assert.Equal(new[] { "get_settings" }, client.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SensitiveMetadataChangeInvalidatesExistingPlanAndCatalogDigest(bool initiallySensitive)
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("get_settings", true, sensitiveHint: initiallySensitive)];
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var plan = fixture.Broker.Plan(session, new([Step("get_settings")]));
        Assert.Equal(initiallySensitive, plan.RequiresApproval);
        if (initiallySensitive) fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var previousDigest = session.Mcp.Connections["ado"].CatalogDigest;
        var client = fixture.Factory.Clients.Single();
        client.Tools = [Tool("get_settings", true, sensitiveHint: !initiallySensitive)];
        await Assert.ThrowsAsync<PortalException>(() => fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        Assert.NotEqual(previousDigest, session.Mcp.Connections["ado"].CatalogDigest);
        Assert.Empty(client.Calls);
        var replacement = fixture.Broker.Plan(session, new([Step("get_settings")]));
        Assert.Equal(!initiallySensitive, replacement.RequiresApproval);
        Assert.NotEqual(plan.Digest, replacement.Digest);
    }
    [Fact] public async Task JsonEncodedTextResultsRedactCredentialFieldsBeforeReturningOrSaving()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        const string accessToken = "synthetic-access-value-without-recognizable-token-prefix";
        const string password = "synthetic-password-value-without-recognizable-token-prefix";
        const string accountKey = "synthetic-storage-key-without-recognizable-token-prefix";
        var payload = JsonSerializer.Serialize(new { data = new { name = "Visible synthetic name", accessToken, nested = new[] { new { password, AccountKey = accountKey } } } });
        fixture.Factory.Clients.Single().Handler = (_, _, _) => Task.FromResult(Json(new { content = new[] { new { type = "text", text = payload } }, isError = false }));
        var plan = fixture.Broker.Plan(session, new([Step("list_projects")]));
        var executed = Json(await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        var text = executed.GetProperty("receipts")[0].GetProperty("output").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var redacted = JsonDocument.Parse(text);
        Assert.Equal("Visible synthetic name", redacted.RootElement.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal("[redacted]", redacted.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        Assert.Equal("[redacted]", redacted.RootElement.GetProperty("data").GetProperty("nested")[0].GetProperty("password").GetString());
        Assert.Equal("[redacted]", redacted.RootElement.GetProperty("data").GetProperty("nested")[0].GetProperty("AccountKey").GetString());
        Assert.DoesNotContain(accessToken, executed.GetRawText()); Assert.DoesNotContain(password, executed.GetRawText()); Assert.DoesNotContain(accountKey, executed.GetRawText());
        var saved = File.ReadAllText(Path.Combine(fixture.Root, "artifacts", "portal-mcp", session.Mcp.Id, plan.Id + ".json"));
        Assert.DoesNotContain(accessToken, saved); Assert.DoesNotContain(password, saved); Assert.DoesNotContain(accountKey, saved);
    }
    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=synthetic;AccountKey=synthetic-private-value;EndpointSuffix=core.windows.net")]
    [InlineData("AccountName=synthetic;SharedAccessSignature=sv=synthetic&sig=synthetic-private-value")]
    [InlineData("Server=tcp:synthetic.database.windows.net,1433;Database=synthetic;User ID=fixture;Password=synthetic-private-value;Encrypt=True")]
    [InlineData("Server=synthetic;Database=synthetic;Uid=fixture;Pwd='synthetic-private-value';Encrypt=True")]
    [InlineData("https://synthetic.blob.core.windows.net/container/blob?sig=synthetic-private-value")]
    [InlineData("https://synthetic.blob.core.windows.net/container/blob?sv=synthetic&sig=synthetic-private-value")]
    public async Task PlaintextConnectionCredentialsAndSignedUrlsAreWithheldFromModelAndDisk(string providerText)
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        fixture.Factory.Clients.Single().Handler = (_, _, _) => Task.FromResult(Json(new { content = new[] { new { type = "text", text = providerText } }, isError = false }));
        var plan = fixture.Broker.Plan(session, new([Step("list_projects")]));
        var executed = Json(await fixture.Broker.Run(session, plan.Id, plan.Digest, CancellationToken.None));
        var outputText = executed.GetProperty("receipts")[0].GetProperty("output").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("[withheld: credential-shaped provider text]", outputText);
        Assert.DoesNotContain("synthetic-private-value", executed.GetRawText());
        var saved = File.ReadAllText(Path.Combine(fixture.Root, "artifacts", "portal-mcp", session.Mcp.Id, plan.Id + ".json"));
        Assert.DoesNotContain("synthetic-private-value", saved);
    }
    [Theory]
    [InlineData("Server=tcp:synthetic.database.windows.net,1433;Database=synthetic;Authentication=Active Directory Managed Identity")]
    [InlineData("https://learn.microsoft.com/azure/storage/?view=synthetic")]
    public void NonCredentialConnectionMetadataAndOrdinaryUrlsRemainReadable(string providerText)
    {
        var redacted = McpProviderBroker.Redact(Json(new { text = providerText }));
        Assert.Equal(providerText, redacted.GetProperty("text").GetString());
    }
    [Theory]
    [InlineData("accessToken")][InlineData("refresh_token")][InlineData("clientSecret")][InlineData("authorization")][InlineData("password")][InlineData("connectionString")][InlineData("privateKey")][InlineData("personalAccessToken")]
    [InlineData("GH_TOKEN")][InlineData("GITHUB_TOKEN")][InlineData("AZURE_DEVOPS_EXT_PAT")][InlineData("SYSTEM_ACCESSTOKEN")][InlineData("apiKey")][InlineData("token")][InlineData("secret")][InlineData("apiToken")]
    public async Task NestedCredentialArgumentsAreRejectedBeforeAnyProviderCall(string field)
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        var arguments = Json(new { nested = new[] { new Dictionary<string, string> { [field] = "synthetic-value" } } });
        Assert.Throws<PortalException>(() => fixture.Broker.Plan(session, new([new("ado", "create_project", arguments)])));
        Assert.Empty(fixture.Factory.Clients.Single().Calls); Assert.Empty(session.Mcp.Plans);
    }
    [Fact] public async Task BicepFileCapabilitiesRejectPathsOutsideSourceBoundary()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        fixture.Factory.Tools = [Tool("format_bicep_file")];
        await fixture.Broker.Connect(session, "bicep", CancellationToken.None);
        foreach (var path in new[] { "../outside.bicep", ".local/credential-cache.bicep", ".git/config", "artifacts/output.bicep" })
            Assert.Throws<PortalException>(() => fixture.Broker.Plan(session, new([Step("format_bicep_file", new { filePath = path }, "bicep")])));
        var valid = fixture.Broker.Plan(session, new([Step("format_bicep_file", new { filePath = "src/main.bicep" }, "bicep")]));
        Assert.True(valid.RequiresApproval); Assert.Empty(fixture.Factory.Clients.Single().Calls);
    }
    static JsonElement BridgeCall(string name, object? arguments = null) => Json(new { tool = name, arguments = arguments ?? new { } });
    static JsonElement ProviderOutput(JsonElement response) => JsonDocument.Parse(response.GetProperty("contentItems")[0].GetProperty("text").GetString()!).RootElement.Clone();
    [Fact] public async Task ProviderBridgeIsOptInAndPreservesRequiredEvidenceReads()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        await using var bridge = await AgentMcpBridge.Create("frozen workflow evidence", "pinned workflow skill", () => true, CancellationToken.None);
        Assert.Equal(2, bridge.Tools.Length);
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(BridgeCall("platform_mcp_catalog"), CancellationToken.None));
        bridge.EnableProviders(fixture.Broker, session);
        var names = bridge.Tools.Select(tool => Json(tool).GetProperty("name").GetString()).ToArray();
        Assert.Equal(6, names.Length); Assert.Contains("platform_mcp_catalog", names); Assert.DoesNotContain("platform_mcp_approve", names);
        var catalog = Json(await bridge.Call(BridgeCall("platform_mcp_catalog", new { provider = "ado" }), CancellationToken.None));
        Assert.Equal(3, ProviderOutput(catalog).GetProperty("total").GetInt32());
        var read = Json(await bridge.Call(BridgeCall("platform_mcp_call", new { provider = "ado", tool = "list_projects", arguments = new { } }), CancellationToken.None));
        Assert.True(read.GetProperty("success").GetBoolean());
        Assert.Equal("Completed", ProviderOutput(read).GetProperty("State").GetString());
        Assert.Equal(new[] { "list_projects" }, fixture.Factory.Clients.Single().Calls);
        Assert.False(bridge.EvidenceRead);
        await bridge.Call(BridgeCall("platform_evidence"), CancellationToken.None); Assert.False(bridge.EvidenceRead);
        await bridge.Call(BridgeCall("platform_skill"), CancellationToken.None); Assert.True(bridge.EvidenceRead);
    }
    [Fact] public async Task ModelBridgeCanProposeMutationButCannotApproveIt()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await fixture.Broker.Connect(session, "ado", CancellationToken.None);
        await using var bridge = await AgentMcpBridge.Create("evidence", "skill", () => true, CancellationToken.None); bridge.EnableProviders(fixture.Broker, session);
        var response = Json(await bridge.Call(BridgeCall("platform_mcp_call", new { provider = "ado", tool = "create_project", arguments = new { name = "Synthetic project" } }), CancellationToken.None));
        Assert.Equal("AwaitingApproval", ProviderOutput(response).GetProperty("State").GetString());
        var plan = session.Mcp.Plans.Values.Single();
        await Assert.ThrowsAsync<PortalException>(() => bridge.Call(BridgeCall("platform_mcp_approve", new { id = plan.Id, digest = plan.Digest }), CancellationToken.None));
        var unapproved = Json(await bridge.Call(BridgeCall("platform_mcp_run", new { id = plan.Id, digest = plan.Digest }), CancellationToken.None));
        Assert.False(unapproved.GetProperty("success").GetBoolean()); Assert.Equal("AwaitingApproval", plan.State);
        Assert.Empty(fixture.Factory.Clients.Single().Calls);
        fixture.Broker.Approve(session, plan.Id, plan.Digest);
        var approved = Json(await bridge.Call(BridgeCall("platform_mcp_run", new { id = plan.Id, digest = plan.Digest }), CancellationToken.None));
        Assert.True(approved.GetProperty("success").GetBoolean()); Assert.Equal("Completed", plan.State);
        Assert.Single(fixture.Factory.Clients.Single().Calls);
    }
    [Fact] public async Task MalformedProviderWrapperReturnsBoundedFailure()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        await using var bridge = await AgentMcpBridge.Create("evidence", "skill", () => true, CancellationToken.None); bridge.EnableProviders(fixture.Broker, session);
        var malformed = Json(await bridge.Call(BridgeCall("platform_mcp_call", new { tool = "list_projects", arguments = new { } }), CancellationToken.None));
        Assert.False(malformed.GetProperty("success").GetBoolean());
        Assert.Empty(fixture.Factory.Clients);
    }
}
