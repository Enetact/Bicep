using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationWorkflowIntegrationTests
{
    [Fact]
    public async Task Existing_MCP_bridge_creates_plan_and_requires_separate_HTTP_approval_before_durable_dispatch()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var tools = await Json(await f.Send("requester", HttpMethod.Get, "/agent/tools"));
        Assert.Equal(9, tools.GetProperty("tools").GetArrayLength());
        Assert.False(tools.GetProperty("approvalTool").GetBoolean());
        Assert.DoesNotContain("platform_foundation_approve", tools.GetRawText());
        var created = await Tool(f, "requester", "platform_foundation_plan", new PlanCommand("customer-ci", "main",
            FoundationApiTests.ApiCell.Revision, new() { ["region"] = "eastus2" }));
        var plan = created.Deserialize<FoundationPlan>(FoundationApplication.Json)!;
        var beforeApproval = await f.Send("operator", HttpMethod.Post, "/agent/tool", new FoundationToolCommand("platform_foundation_enqueue",
            JsonSerializer.SerializeToElement(new FoundationPlanReference(plan.Id, plan.Digest, "mcp-once"), FoundationApplication.Json)));
        Assert.False((await Json(beforeApproval)).GetProperty("success").GetBoolean());
        await f.Approve(plan);
        var queued = await Tool(f, "operator", "platform_foundation_enqueue", new FoundationPlanReference(plan.Id, plan.Digest, "mcp-once"));
        var job = queued.Deserialize<FoundationJob>(FoundationApplication.Json)!;
        await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("mcp-worker");
        var observed = await Tool(f, "viewer", "platform_foundation_job", new FoundationObjectReference(job.Id));
        Assert.Equal("Succeeded", observed.GetProperty("state").GetString());
        Assert.Contains("deploymentVerified", observed.GetRawText());
        Assert.Single(f.Transport.Requests);
    }

    [Fact]
    public async Task Customer_scoped_Bicep_workflow_reuses_existing_AVM_emitter_and_stores_parameterized_source_archive()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var command = new FoundationBicepCommand("11111111-1111-1111-1111-111111111111", "eastus2", "Compose an unqualified source draft",
            [new BicepNode("storage", "storage-storage-account", "Customer source example", new()),
             new BicepNode("identity", "avm-identity-user-assigned-identity", "Scoped managed identity source", new())], new());
        var result = await Tool(f, "requester", "platform_foundation_bicep", command);
        Assert.False(result.GetProperty("deploymentAuthorized").GetBoolean());
        Assert.Equal("NotCollected", result.GetProperty("discoveryStatus").GetString());
        Assert.True(result.GetProperty("fileCount").GetInt32() > 5);
        Assert.NotEmpty(result.GetProperty("requiredInputs").EnumerateArray());
        var id = result.GetProperty("id").GetString();
        var response = await f.Send("viewer", HttpMethod.Get, $"/artifacts/{id}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Contains(archive.Entries, e => e.FullName.EndsWith("main.bicepparam", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, e => e.FullName.Contains("user-assigned-identity", StringComparison.Ordinal));
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task MCP_arguments_cannot_override_customer_identity_or_approve_plans()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var forged = await f.Send("requester", HttpMethod.Post, "/agent/tool", new
        {
            name = "platform_foundation_plan", arguments = new { connectionId = "customer-ci", @ref = "main", sourceRevision = FoundationApiTests.ApiCell.Revision,
                inputs = new { region = "eastus2" }, customerId = FoundationApiTests.ApiCell.OtherCustomer, actorKey = FoundationApiTests.ApiCell.Actor("admin") }
        });
        Assert.False((await Json(forged)).GetProperty("success").GetBoolean());
        var approve = await f.Send("approver", HttpMethod.Post, "/agent/tool", new FoundationToolCommand("platform_foundation_approve", JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task AHP_protocol_uses_real_MCP_bridge_and_rechecks_revoked_membership_per_action()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var client = f.App.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
        client.ConfigureRequest = r => r.Headers.Authorization = f.Request("requester", HttpMethod.Get, "/agent/ahp").Headers.Authorization!.ToString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + f.Root + "/agent/ahp"), timeout.Token);
        var initialized = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root, protocolVersions = new[] { AgentHostChannel.Version }, clientId = "synthetic-test" }, timeout.Token);
        Assert.True(initialized.TryGetProperty("result", out _));
        var channel = "ahp-session:/" + Guid.NewGuid().ToString("D");
        _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, timeout.Token);
        var subscribed = await Rpc(socket, 3, "subscribe", new { channel }, timeout.Token);
        Assert.Equal(9, subscribed.GetProperty("result").GetProperty("snapshot").GetProperty("state").GetProperty("tools").GetArrayLength());
        var proposed = await Rpc(socket, 4, "dispatchAction", new { channel, action = new { type = "platform/foundationPlan", payload =
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new() { ["region"] = "eastus2" }) } }, timeout.Token);
        Assert.True(proposed.GetProperty("result").GetProperty("success").GetBoolean());
        await f.Member("requester", [FoundationRoles.Requester], false);
        var denied = await Rpc(socket, 5, "dispatchAction", new { channel, action = new { type = "platform/foundationPlan", payload =
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new()) } }, timeout.Token);
        Assert.True(denied.TryGetProperty("error", out _));
        Assert.Empty(f.Transport.Requests);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Fact]
    public async Task AHP_model_review_uses_bounded_mock_runtime_and_preserves_advice_only_authority()
    {
        var model = new AdviceClient();
        await using var f = await FoundationApiTests.ApiCell.Start(modelClient: model);
        var client = f.App.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
        client.ConfigureRequest = r => r.Headers.Authorization = f.Request("requester", HttpMethod.Get, "/agent/ahp").Headers.Authorization!.ToString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + f.Root + "/agent/ahp"), timeout.Token);
        _ = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root, protocolVersions = new[] { AgentHostChannel.Version }, clientId = "model-fixture" }, timeout.Token);
        var channel = "ahp-session:/" + Guid.NewGuid().ToString("D");
        _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, timeout.Token);
        var response = await Rpc(socket, 3, "dispatchAction", new { channel, action = new { type = "platform/foundationReview",
            payload = new FoundationModelCommand("Review the synthetic local module catalog") } }, timeout.Token);
        var review = response.GetProperty("result");
        Assert.True(review.GetProperty("adviceOnly").GetBoolean()); Assert.True(review.GetProperty("mock").GetBoolean());
        Assert.False(review.GetProperty("executionAuthorized").GetBoolean()); Assert.False(review.GetProperty("deploymentVerified").GetBoolean());
        Assert.Equal(3, review.GetProperty("toolAudit").GetArrayLength()); Assert.Equal(1, model.Calls);
        Assert.Empty(f.Transport.Requests);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    sealed class AdviceClient : IFoundationModelClient
    {
        public bool IsMock => true;
        public int Calls { get; private set; }
        public async Task<string> ReviewAsync(FoundationModelInvocation invocation, AgentMcpBridge bridge, CancellationToken ct)
        {
            Calls++;
            foreach (var tool in new[] { "platform_skill", "platform_evidence", "platform_foundation_catalog" })
                await bridge.Call(JsonSerializer.SerializeToElement(new { tool, arguments = new { } }), ct);
            return "This is unqualified advice about the synthetic local source catalog.";
        }
    }

    [Fact]
    public async Task Exact_reviewed_customer_lifecycle_can_suspend_and_resume_without_restoring_pipeline_approval()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var pipeline = await f.Plan(); await f.Approve(pipeline); _ = await f.Enqueue(pipeline);
        var plan = (await Json(await f.Send("admin", HttpMethod.Post, "/administration/lifecycle/plan",
            new CustomerLifecycleCommand("Suspend", "Suspend this synthetic customer")))).Deserialize<FoundationAdministrativePlan>(FoundationApplication.Json)!;
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        _ = await Json(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest }));
        var suspended = await Json(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/execute", new { plan.Digest }));
        Assert.False(suspended.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, "/jobs")).StatusCode);
        var resume = (await Json(await f.Send("admin", HttpMethod.Post, "/administration/lifecycle/plan",
            new CustomerLifecycleCommand("Resume", "Resume this synthetic customer")))).Deserialize<FoundationAdministrativePlan>(FoundationApplication.Json)!;
        _ = await Json(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{resume.Id}/approve", new { resume.Digest }));
        _ = await Json(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{resume.Id}/execute", new { resume.Digest }));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("operator", HttpMethod.Post, $"/plans/{pipeline.Id}/enqueue", new { pipeline.Digest, idempotencyKey = "not-restored" })).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Scope_onboarding_requires_review_and_does_not_grant_cloud_or_operation_roles()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var plan = (await Json(await f.Send("admin", HttpMethod.Post, "/administration/scopes/plan",
            new ScopeOnboardingCommand("product-b", "qa", FoundationApiTests.ApiCell.Actor("operator"), "Synthetic project isolation"))))
            .Deserialize<FoundationAdministrativePlan>(FoundationApplication.Json)!;
        _ = await Json(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest }));
        _ = await Json(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/execute", new { plan.Digest }));
        var other = f.Root.Replace("/projects/default/environments/dev", "/projects/product-b/environments/qa");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/jobs", root: other)).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    static async Task<JsonElement> Tool(FoundationApiTests.ApiCell f, string actor, string tool, object arguments)
    {
        var response = await Json(await f.Send(actor, HttpMethod.Post, "/agent/tool", new FoundationToolCommand(tool,
            JsonSerializer.SerializeToElement(arguments, FoundationApplication.Json))));
        Assert.True(response.GetProperty("success").GetBoolean(), response.GetRawText());
        using var doc = JsonDocument.Parse(response.GetProperty("contentItems")[0].GetProperty("text").GetString()!); return doc.RootElement.Clone();
    }
    static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
    }
    static async Task<JsonElement> Rpc(WebSocket socket, int id, string method, object parameters, CancellationToken ct)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, FoundationApplication.Json)), WebSocketMessageType.Text, true, ct);
        using var output = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult part;
        do { part = await socket.ReceiveAsync(buffer, ct); output.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
