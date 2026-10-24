using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationFoundationOperationsApiTests
{
    [Fact]
    public async Task Trusted_host_can_require_signed_mode_without_stranding_recovery_or_allowing_a_body_override()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(requireSignedEntitlement: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Post, "/administration/configuration/plan",
            new FeatureConfigurationCommand(new("LocalPreview", [FoundationFeatures.PipelinePlan], 100, "0.0.0", "99.0.0"), 0,
                "Attempt preview downgrade under signed-only host policy"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send("admin", HttpMethod.Post, "/administration/configuration/plan",
            raw: "{\"requireSignedEntitlement\":false}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Send("admin", HttpMethod.Get, "/administration/configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Send("auditor", HttpMethod.Get, "/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.Send("auditor", HttpMethod.Post, "/administration/export-snapshots", new PagedScopeExportCommand())).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Reviewed_feature_configuration_gates_HTTP_and_model_tools_and_can_be_rolled_forward_to_restore()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var change = await Plan(f, new("LocalPreview", [], 100, "0.0.0", "99.0.0"), 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Post,
            $"/administration/plans/{change.Id}/approve", new ExactPlanCommand(change.Digest))).StatusCode);
        await Apply(f, change);
        var snapshot = await Read<FoundationConfigurationSnapshot>(await f.Send("auditor", HttpMethod.Get, "/administration/configuration"));
        Assert.Equal(1, snapshot.Generation);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, "/agent/review", new FoundationModelCommand("Review safely"))).StatusCode);
        foreach (var name in new[] { "platform_foundation_catalog", "platform_foundation_bicep", "platform_foundation_plan" })
        {
            var rejected = await Read<JsonElement>(await f.Send("requester", HttpMethod.Post, "/agent/tool",
                new { name, arguments = name == "platform_foundation_catalog" ? new { } : (object)(name == "platform_foundation_bicep"
                    ? new { subscriptionId = "00000000-0000-0000-0000-000000000005", region = "eastus2", goal = "Source only", modules = Array.Empty<object>(), settings = new { } }
                    : new { connectionId = "customer-ci", @ref = "main", sourceRevision = FoundationApiTests.ApiCell.Revision, inputs = new { }, lifetimeMinutes = 30 }) }));
            Assert.False(rejected.GetProperty("success").GetBoolean());
        }
        Assert.Equal(HttpStatusCode.OK, (await f.Send("auditor", HttpMethod.Get, "/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.Send("auditor", HttpMethod.Post, "/administration/export-snapshots", new PagedScopeExportCommand())).StatusCode);
        var rollback = await Read<FoundationAdministrativePlan>(await f.Send("admin", HttpMethod.Post,
            "/administration/configuration/rollback/plan", new FeatureRollbackCommand(0, 1, "Restore reviewed preview policy")));
        await Apply(f, rollback);
        snapshot = await Read<FoundationConfigurationSnapshot>(await f.Send("admin", HttpMethod.Get, "/administration/configuration"));
        Assert.Equal(2, snapshot.Generation);
        Assert.Equal(HttpStatusCode.Created, (await f.Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new()))).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Paged_export_HTTP_is_frozen_owner_bound_and_rejects_cross_scope_or_digest_rebinding()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        await f.Member("viewer", [FoundationRoles.Auditor]);
        var result = await Read<FoundationPagedExportStart>(await f.Send("auditor", HttpMethod.Post,
            "/administration/export-snapshots", new PagedScopeExportCommand(2, 15)));
        var body = new PagedScopeExportReadCommand(result.FirstCursor, result.Snapshot.Sha256);
        var path = $"/administration/export-snapshots/{result.Snapshot.Id}/page";
        var page = await Read<FoundationPagedExportPage>(await f.Send("auditor", HttpMethod.Post, path, body));
        Assert.Equal(result.FirstPage.Sha256, page.Sha256); Assert.True(page.MetadataOnly);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send("viewer", HttpMethod.Post, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send("auditor", HttpMethod.Get, $"/administration/exports/{result.Snapshot.Id}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await f.Send("auditor", HttpMethod.Post, path, body with { SnapshotSha256 = new string('0', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("auditor", HttpMethod.Post, path, body,
            root: $"/api/foundation/customers/{FoundationApiTests.ApiCell.OtherCustomer}/projects/default/environments/dev")).StatusCode);
        var count = page.Items.Length;
        while (page.NextCursor is { } next)
        {
            page = await Read<FoundationPagedExportPage>(await f.Send("auditor", HttpMethod.Post, path, body with { Cursor = next }));
            count += page.Items.Length;
        }
        Assert.True(page.Complete); Assert.Equal(result.Snapshot.ItemCount, count);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Worker_health_is_an_aggregate_for_explicit_platform_operators_only()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Get, "/operations/workers")).StatusCode);
        await f.Member("operator", [FoundationRoles.PlatformOperator]);
        using var response = await f.Send("operator", HttpMethod.Get, "/operations/workers", tokenRoles: [FoundationRoles.PlatformOperator]);
        var raw = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = JsonSerializer.Deserialize<FoundationWorkerHealth>(raw, FoundationApplication.Json)!;
        Assert.Equal(0, health.Registered); Assert.DoesNotContain("actor", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("process", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<FoundationAdministrativePlan> Plan(FoundationApiTests.ApiCell f, FoundationFeatureConfiguration config, long generation) =>
        await Read<FoundationAdministrativePlan>(await f.Send("admin", HttpMethod.Post, "/administration/configuration/plan",
            new FeatureConfigurationCommand(config, generation, "Reviewed local qualification feature policy")));
    static async Task Apply(FoundationApiTests.ApiCell f, FoundationAdministrativePlan plan)
    {
        _ = await Read<FoundationAdministrativePlan>(await f.Send("approver", HttpMethod.Post,
            $"/administration/plans/{plan.Id}/approve", new ExactPlanCommand(plan.Digest)));
        _ = await Read<FoundationConfigurationSnapshot>(await f.Send("admin", HttpMethod.Post,
            $"/administration/plans/{plan.Id}/execute", new ExactPlanCommand(plan.Digest)));
    }
    static async Task<T> Read<T>(HttpResponseMessage response)
    {
        using (response) { var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, body); return JsonSerializer.Deserialize<T>(body, FoundationApplication.Json)!; }
    }
}
