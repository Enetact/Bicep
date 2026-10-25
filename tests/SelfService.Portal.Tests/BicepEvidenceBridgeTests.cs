using System.Text;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BicepEvidenceBridgeTests
{
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static BicepDraftContext Context()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "config/workloads.json"))) root = root.Parent;
        var modules = BicepDrafts.Catalog(root!.FullName).Modules.Where(m => m.DraftSelectable).ToArray();
        return new("platform.bicep-context/v1", "synthetic-frozen-context", "synthetic-frozen-catalog", "11111111-1111-1111-1111-111111111111", "storage", "dev", "eastus2", "Read all six AVM contracts", "Partial",
            new() { ["location"] = J("eastus2") }, [], modules);
    }
    static JsonElement Call(string tool, object? args = null) => J(new { tool, arguments = args ?? new { }, @namespace = AgentPolicy.ToolNamespace });
    static string Text(JsonElement result) => result.GetProperty("contentItems")[0].GetProperty("text").GetString()!;

    [Fact] public async Task RealMcpRoundTripsDeliverAllSelectedContractsAndRetainTwoScopedTools()
    {
        var context = Context(); var view = new BicepEvidenceView(context);
        await using var bridge = await AgentMcpBridge.Create("Full frozen evidence retained separately", "Complete composition skill", () => true, CancellationToken.None, view);
        Assert.Equal(2, bridge.Tools.Length);
        var evidenceTool = bridge.Tools.Select(J).Single(t => t.GetProperty("name").GetString() == "platform_evidence");
        Assert.True(evidenceTool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("moduleId", out _));
        var index = J(await bridge.Call(Call("platform_evidence"), CancellationToken.None));
        Assert.True(index.GetProperty("success").GetBoolean()); Assert.True(view.BaseRead); Assert.False(bridge.EvidenceRead);
        Assert.True(Encoding.UTF8.GetByteCount(Text(index)) <= 12000);
        var selected = context.Modules.Where(m => m.Id.StartsWith("avm-", StringComparison.Ordinal) || m.Id == "monitoring-log-analytics").ToArray();
        Assert.Equal(7, selected.Length);
        var proposal = JsonSerializer.Serialize(new BicepProposal("platform.bicep-proposal/v1", context.EvidenceDigest, "Synthetic seven-node proposal", selected.Select((m, i) => new BicepNode("node" + i, m.Id, "Synthetic node", [])).ToArray(), []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        foreach (var module in selected)
        {
            var result = J(await bridge.Call(Call("platform_evidence", new { moduleId = module.Id }), CancellationToken.None));
            Assert.True(result.GetProperty("success").GetBoolean());
            var text = Text(result); Assert.True(Encoding.UTF8.GetByteCount(text) <= 12000);
            var delivered = JsonDocument.Parse(text).RootElement;
            Assert.Equal(module.Id, delivered.GetProperty("module").GetProperty("id").GetString());
            Assert.True(delivered.GetProperty("module").GetProperty("parameters").TryGetProperty("name", out _));
        }
        view.ValidateProposal(proposal);
        await bridge.Call(Call("platform_skill"), CancellationToken.None);
        Assert.True(bridge.EvidenceRead); Assert.NotNull(bridge.CompositionCoverage);
    }
    [Fact] public async Task PagingCannotOverrideScopeOrSurviveRevocationAndDoesNotChangeOtherWorkflows()
    {
        var authorized = true; var view = new BicepEvidenceView(Context());
        await using var composition = await AgentMcpBridge.Create("frozen", "skill", () => authorized, CancellationToken.None, view);
        await Assert.ThrowsAsync<PortalException>(() => composition.Call(Call("platform_evidence", new { subscription = "another-scope" }), CancellationToken.None));
        Assert.False(view.BaseRead);
        authorized = false;
        await Assert.ThrowsAsync<PortalException>(() => composition.Call(Call("platform_evidence"), CancellationToken.None));
        Assert.False(view.BaseRead);
        await using var standard = await AgentMcpBridge.Create("other frozen evidence", "other skill", () => true, CancellationToken.None);
        await Assert.ThrowsAsync<PortalException>(() => standard.Call(Call("platform_evidence", new { moduleId = "avm-network-route-table" }), CancellationToken.None));
        Assert.Null(standard.CompositionCoverage);
    }
}
