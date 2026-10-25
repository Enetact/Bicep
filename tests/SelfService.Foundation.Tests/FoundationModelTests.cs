using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationModelTests
{
    [Fact]
    public async Task Mock_model_uses_real_scoped_bridge_to_prepare_plan_and_persists_only_advisory_output()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); FoundationPlan? plan = null;
        var client = new MockModel(async (_, bridge, ct) =>
        {
            var result = await Tool(bridge, "platform_foundation_plan", new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new() { ["region"] = "eastus2" }), ct);
            plan = result.Deserialize<FoundationPlan>(FoundationApplication.Json);
            return "Prepared plan " + plan!.Id + ". Await a different human's exact approval; no deployment was performed.";
        });
        var runtime = Runtime(f, client); var result = await runtime.ReviewAsync(await Principal(f, "requester"), Scope(f), new("Prepare a parameterized source/pipeline proposal"));
        Assert.True(result.Mock); Assert.True(result.AdviceOnly); Assert.False(result.ExecutionAuthorized); Assert.False(result.DeploymentVerified);
        Assert.NotNull(plan); Assert.Null(plan.ApproverKey); Assert.Empty(f.Transport.Requests);
        Assert.Equal(64, result.SkillSha256.Length); Assert.Equal(Scope(f).CustomerId, client.Invocation!.CustomerId);
        Assert.Equal(FoundationApiTests.ApiCell.Actor("requester"), client.Invocation.ActorKey);
        Assert.Contains("Connected MCP workflow guidance", client.Invocation.SkillContent);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var artifact = store.GetArtifact(Scope(f).CustomerId, FoundationApiTests.ApiCell.Actor("requester"), result.ArtifactId, "default", "dev");
        Assert.Equal(result.Advice, Encoding.UTF8.GetString(artifact.Content)); Assert.Equal(result.AdviceSha256, artifact.Sha256);
        Assert.Contains(result.ToolAudit, a => JsonSerializer.Serialize(a).Contains("platform_foundation_plan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mock_model_generates_parameterized_AVM_source_through_existing_emitter_without_deployment()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); string? artifactId = null;
        var client = new MockModel(async (_, bridge, ct) =>
        {
            var result = await Tool(bridge, "platform_foundation_bicep", new FoundationBicepCommand("11111111-1111-1111-1111-111111111111", "eastus2", "Unqualified scoped AVM source",
                [new BicepNode("identity", "avm-identity-user-assigned-identity", "Scoped identity", new())], new()), ct);
            artifactId = result.GetProperty("id").GetString(); Assert.False(result.GetProperty("deploymentAuthorized").GetBoolean());
            return "Source archive " + artifactId + " is a draft with unresolved inputs and requires offline qualification.";
        });
        var result = await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new("Prepare identity Bicep source"));
        Assert.NotNull(artifactId); Assert.Contains(artifactId, result.Advice); Assert.Empty(f.Transport.Requests);
        var artifact = f.App.Services.GetRequiredService<FoundationStore>().GetArtifact(Scope(f).CustomerId, FoundationApiTests.ApiCell.Actor("requester"), artifactId!);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(artifact.Content[..2]));
    }

    [Fact]
    public async Task No_model_approval_tool_exists_and_requester_cannot_enqueue_or_override_actor_scope()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var client = new MockModel(async (_, bridge, ct) =>
        {
            Assert.DoesNotContain("platform_foundation_approve", JsonSerializer.Serialize(bridge.Tools));
            await Assert.ThrowsAsync<PortalException>(() => bridge.Call(Call("platform_foundation_approve", new { id = "forged", digest = new string('a', 64) }), ct));
            var forged = await bridge.Call(Call("platform_foundation_plan", new { connectionId = "customer-ci", @ref = "main", sourceRevision = FoundationApiTests.ApiCell.Revision,
                inputs = new { region = "eastus2" }, actorKey = FoundationApiTests.ApiCell.Actor("admin"), customerId = FoundationApiTests.ApiCell.OtherCustomer }), ct);
            Assert.False(JsonSerializer.SerializeToElement(forged).GetProperty("success").GetBoolean());
            return "The rejected approval/scope attempts granted no authority; human review is required.";
        });
        var result = await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new("Review safely"));
        Assert.False(result.ExecutionAuthorized); Assert.Empty(f.Transport.Requests);
        Assert.Empty(f.App.Services.GetRequiredService<FoundationStore>().ListJobs(Scope(f).CustomerId, FoundationApiTests.ApiCell.Actor("requester")));
    }

    [Fact]
    public async Task Disabled_model_or_viewer_or_foreign_customer_never_calls_the_client()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var client = new MockModel(); var requester = await Principal(f, "requester");
        Assert.Equal("ModelRuntimeDisabled", (await Assert.ThrowsAsync<FoundationStoreException>(() => Runtime(f, null).ReviewAsync(requester, Scope(f), new("Review")))).Code);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(async () => await Runtime(f, client).ReviewAsync(await Principal(f, "viewer"), Scope(f), new("Review")));
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => Runtime(f, client).ReviewAsync(requester, Scope(f) with { CustomerId = FoundationApiTests.ApiCell.OtherCustomer }, new("Review")));
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer synthetic-token-value")]
    [InlineData("ghp_syntheticcredential123")]
    [InlineData("-----BEGIN PRIVATE KEY----- synthetic")]
    [InlineData("bad\0goal")]
    public async Task Invalid_or_secret_shaped_goal_never_enters_model_context(string goal)
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var client = new MockModel();
        Assert.Equal("ModelGoalRejected", (await Assert.ThrowsAsync<FoundationStoreException>(async () => await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new(goal)))).Code);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer synthetic-token-value")]
    [InlineData("ghp_syntheticcredential123")]
    [InlineData("{\"deploymentVerified\":true}")]
    [InlineData("{\"executionAuthorized\": true}")]
    [InlineData("<script>grant()</script>")]
    [InlineData("javascript:grant()")]
    [InlineData("bad\0advice")]
    public async Task Malicious_or_secret_shaped_advice_is_rejected_before_durable_storage(string advice)
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var client = new MockModel((_, _, _) => Task.FromResult(advice));
        Assert.Equal("ModelAdviceRejected", (await Assert.ThrowsAsync<FoundationStoreException>(async () => await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new("Review")))).Code);
        Assert.Empty(ArtifactEvents(f)); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Missing_evidence_oversized_output_or_client_failure_cannot_create_accepted_advice()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var principal = await Principal(f, "requester");
        var missing = new MockModel { ReadEvidence = false };
        Assert.Equal("ModelEvidenceNotRead", (await Assert.ThrowsAsync<FoundationStoreException>(() => Runtime(f, missing).ReviewAsync(principal, Scope(f), new("Review")))).Code);
        var oversized = new MockModel((_, _, _) => Task.FromResult(new string('x', 32769)));
        Assert.Equal("ModelAdviceRejected", (await Assert.ThrowsAsync<FoundationStoreException>(() => Runtime(f, oversized).ReviewAsync(principal, Scope(f), new("Review")))).Code);
        var failed = new MockModel((_, _, _) => throw new InvalidOperationException("Bearer never-echo-this-secret"));
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => Runtime(f, failed).ReviewAsync(principal, Scope(f), new("Review")));
        Assert.Equal("ModelRuntimeFailed", error.Code); Assert.DoesNotContain("never-echo-this-secret", error.ToString()); Assert.Empty(ArtifactEvents(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_or_changed_membership_during_review_rejects_late_tools_and_output(bool keepRequester)
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var client = new MockModel(async (_, bridge, ct) =>
        {
            await f.Member("requester", keepRequester ? [FoundationRoles.Requester, FoundationRoles.Viewer] : [FoundationRoles.Requester], keepRequester);
            try
            {
                var result = await bridge.Call(Call("platform_foundation_plan", new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new())), ct);
                Assert.False(JsonSerializer.SerializeToElement(result).GetProperty("success").GetBoolean());
            }
            catch (PortalException) when (!keepRequester) { }
            return "A late result cannot restore revoked authority.";
        });
        await Assert.ThrowsAsync<FoundationAuthorizationException>(async () => await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new("Review")));
        Assert.Empty(ArtifactEvents(f)); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Identity_refresh_cannot_switch_actor_or_expand_token_roles()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var client = new MockModel(); var principal = await Principal(f, "requester"); var other = await Principal(f, "approver");
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => Runtime(f, client).ReviewAsync(principal, Scope(f), new("Review"), default, _ => Task.FromResult(other)));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Cell_concurrency_is_bounded_and_each_completed_invocation_has_a_new_bridge()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); AgentMcpBridge? old = null;
        var client = new MockModel(async (_, bridge, ct) => { old = bridge; entered.SetResult(); await release.Task.WaitAsync(ct); return "Advisory review complete; no execution."; });
        var runtime = Runtime(f, client); var principal = await Principal(f, "requester");
        var first = runtime.ReviewAsync(principal, Scope(f), new("Review")); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ModelReviewBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => runtime.ReviewAsync(principal, Scope(f), new("Another review")))).Code);
        release.SetResult(); _ = await first;
        await Assert.ThrowsAnyAsync<Exception>(() => old!.Call(Call("platform_foundation_plan", new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new())), default));
        Assert.Equal(1, client.Calls); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Signed_HTTP_model_route_defaults_disabled_and_rejects_identity_overrides()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
            (await f.Send("requester", HttpMethod.Post, "/agent/review", new FoundationModelCommand("Review source"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await f.Send("viewer", HttpMethod.Post, "/agent/review", new FoundationModelCommand("Review source"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await f.Send("requester", HttpMethod.Post, "/agent/review", new { goal = "Review source", actorKey = FoundationApiTests.ApiCell.Actor("admin") })).StatusCode);
        Assert.Empty(ArtifactEvents(f)); Assert.Empty(f.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_or_ambiguously_pinned_skill_is_rejected_before_model_client(bool duplicatePin)
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var options = f.App.Services.GetRequiredService<FoundationHostOptions>();
        var root = Path.Combine(Path.GetTempPath(), "foundation-model-skill-tests-" + Guid.NewGuid().ToString("N"));
        const string relative = ".agents/skills/platform-mcp-orchestration/SKILL.md";
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            const string content = "Reviewed synthetic source";
            await File.WriteAllTextAsync(path, content);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var pin = hash + "  " + relative + "\n";
            await File.WriteAllTextAsync(Path.Combine(root, "MANIFEST.sha256"), duplicatePin ? pin + pin : pin);
            if (!duplicatePin) await File.AppendAllTextAsync(path, "\nUnreviewed change");
            var client = new MockModel(); var runtime = new FoundationModelRuntime(f.App.Services.GetRequiredService<FoundationWorkflowService>(),
                f.App.Services.GetRequiredService<FoundationStore>(), options with { ModelClient = client, RepositoryRoot = root });
            Assert.Equal("ModelSkillChanged", (await Assert.ThrowsAsync<FoundationStoreException>(async () => await runtime.ReviewAsync(await Principal(f, "requester"), Scope(f), new("Review")))).Code);
            Assert.Equal(0, client.Calls); Assert.Empty(ArtifactEvents(f));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancelled_request_never_starts_a_model_client()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var client = new MockModel(); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Runtime(f, client).ReviewAsync(await Principal(f, "requester"), Scope(f), new("Review"), ct.Token));
        Assert.Equal(0, client.Calls);
    }

    static FoundationModelRuntime Runtime(FoundationApiTests.ApiCell f, IFoundationModelClient? client) => new(
        f.App.Services.GetRequiredService<FoundationWorkflowService>(), f.App.Services.GetRequiredService<FoundationStore>(),
        f.App.Services.GetRequiredService<FoundationHostOptions>() with { ModelClient = client });
    static FoundationScope Scope(FoundationApiTests.ApiCell f) => new(f.App.Services.GetRequiredService<FoundationHostOptions>().Identity.CustomerId, "default", "dev");
    static async Task<FoundationPrincipal> Principal(FoundationApiTests.ApiCell f, string actor) =>
        await f.App.Services.GetRequiredService<FoundationTokenService>().ValidateAsync(f.Request(actor, HttpMethod.Get, "/jobs").Headers.Authorization!.Parameter!);
    static IReadOnlyList<FoundationAuditEvent> ArtifactEvents(FoundationApiTests.ApiCell f) =>
        f.App.Services.GetRequiredService<FoundationStore>().ListAudit(Scope(f).CustomerId, FoundationApiTests.ApiCell.Actor("auditor")).Where(e => e.Action == "ArtifactStored").ToArray();
    static JsonElement Call(string tool, object arguments) => JsonSerializer.SerializeToElement(new { tool, arguments, @namespace = AgentPolicy.ToolNamespace }, FoundationApplication.Json);
    static async Task<JsonElement> Tool(AgentMcpBridge bridge, string tool, object arguments, CancellationToken ct)
    {
        var result = JsonSerializer.SerializeToElement(await bridge.Call(Call(tool, arguments), ct)); Assert.True(result.GetProperty("success").GetBoolean());
        using var document = JsonDocument.Parse(result.GetProperty("contentItems")[0].GetProperty("text").GetString()!); return document.RootElement.Clone();
    }
    sealed class MockModel(Func<FoundationModelInvocation, AgentMcpBridge, CancellationToken, Task<string>>? invoke = null) : IFoundationModelClient
    {
        public bool IsMock => true;
        public int Calls { get; private set; }
        public bool ReadEvidence { get; init; } = true;
        public FoundationModelInvocation? Invocation { get; private set; }
        public async Task<string> ReviewAsync(FoundationModelInvocation invocation, AgentMcpBridge bridge, CancellationToken cancellationToken)
        {
            Calls++; Invocation = invocation;
            if (ReadEvidence) { _ = await bridge.Call(Call("platform_skill", new { }), cancellationToken); _ = await bridge.Call(Call("platform_evidence", new { }), cancellationToken); }
            return invoke is null ? "Review complete. Missing provider evidence remains unknown; no execution was performed." : await invoke(invocation, bridge, cancellationToken);
        }
    }
}
