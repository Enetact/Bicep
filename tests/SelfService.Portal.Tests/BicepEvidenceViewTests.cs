using System.Text;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BicepEvidenceViewTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Json);
    static JsonElement Parse(string value) => JsonDocument.Parse(value).RootElement.Clone();
    static string Root
    {
        get { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/workloads.json"))) directory = directory.Parent; return directory!.FullName; }
    }
    static BicepDraftContext Context()
    {
        var modules = BicepDrafts.Catalog(Root).Modules.Where(m => m.DraftSelectable).ToArray();
        return new("platform.bicep-context/v1", "synthetic-evidence", "synthetic-catalog", "11111111-1111-1111-1111-111111111111",
            "storage", "dev", "eastus2", "Compose all six AVM modules and a workspace with explicit child parameters", "Partial",
            new() { ["workload"] = J("storage"), ["environmentName"] = J("dev"), ["location"] = J("eastus2"), ["owner"] = J("synthetic-owner") },
            Enumerable.Range(0, 7).Select(i => new BicepObservedResource($"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/synthetic/providers/Microsoft.OperationalInsights/workspaces/logs{i}", "Microsoft.OperationalInsights/workspaces")).ToArray(),
            modules, J(new { status = "Partial", syntheticReport = new string('x', 68_000), escaped = "quote\" slash\\ unicode 🧪", resources = new[] { "synthetic" } }));
    }
    static string Proposal(params BicepNode[] nodes) => JsonSerializer.Serialize(new BicepProposal("platform.bicep-proposal/v1", "synthetic-evidence", "Synthetic source proposal", nodes, ["No deployment qualification"]), Json);
    static BicepNode Node(string module = "monitoring-log-analytics", Dictionary<string, BicepBinding>? bindings = null) => new("component", module, "Synthetic evidence test", bindings ?? new());
    static JsonElement Args(string? moduleId = null, string? setting = null, string? section = null, int? page = null)
    {
        var values = new Dictionary<string, object>(); if (moduleId is not null) values["moduleId"] = moduleId; if (setting is not null) values["setting"] = setting;
        if (section is not null) values["section"] = section; if (page is not null) values["page"] = page.Value; return J(values);
    }
    static string Deliver(BicepEvidenceView view, string? moduleId = null, string? setting = null, string? section = null, int? page = null)
    {
        var args = Args(moduleId, setting, section, page); view.ValidateRequest(args);
        var text = view.Read(moduleId, setting, section, page); view.MarkRead(args); return text;
    }

    [Fact] public void LargeSnapshotDeliversEveryFullAvmContractAndWorkspaceWithoutTruncation()
    {
        var context = Context(); Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(context, Json)) > 64_000);
        var view = new BicepEvidenceView(context); var indexText = Deliver(view); var index = Parse(indexText);
        Assert.InRange(Encoding.UTF8.GetByteCount(indexText), 1, 12_000);
        Assert.Equal(context.Modules.Length, index.GetProperty("modules").GetArrayLength());
        Assert.DoesNotContain("syntheticReport", indexText); Assert.DoesNotContain("synthetic-owner", indexText);
        var selected = context.Modules.Where(m => m.Id.StartsWith("avm-", StringComparison.Ordinal) || m.Id == "monitoring-log-analytics").ToArray();
        Assert.Equal(7, selected.Length);
        foreach (var module in selected)
        {
            var text = Deliver(view, moduleId: module.Id); var response = Parse(text); var actual = response.GetProperty("module");
            Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 12_000);
            Assert.Equal(module.Id, actual.GetProperty("id").GetString());
            Assert.Equal(module.Parameters.Count, actual.GetProperty("parameters").EnumerateObject().Count());
            Assert.Equal(JsonSerializer.Serialize(module, Json), actual.GetRawText());
            Assert.Equal(BicepDrafts.Hash(actual.GetRawText()), response.GetProperty("contractSha256").GetString());
        }
        var nodes = selected.Select((m, i) => new BicepNode("component" + i, m.Id, "Synthetic all-module coverage", new() { ["location"] = new("Setting", "location") })).ToArray();
        view.ValidateProposal(Proposal(nodes));
        Assert.Equal(7, J(view.Coverage).GetProperty("modules").GetArrayLength());
    }

    [Fact] public void CallerMutationsCannotChangeFrozenResponsesOrCoverage()
    {
        var context = Context(); var first = context.Modules[0]; var view = new BicepEvidenceView(context);
        var before = new[] { view.Read(), view.Read(moduleId: first.Id), view.Read(setting: "owner"), view.Read(section: "resources"), view.Read(section: "network") };
        context.Settings["owner"] = J("changed-owner"); context.Settings["newSetting"] = J("changed");
        first.Parameters["name"] = J(new { type = "array" }); context.Modules[0] = first with { Id = "changed-module" };
        context.Resources[0] = new("changed-resource", "changed-type");
        var after = new[] { view.Read(), view.Read(moduleId: first.Id), view.Read(setting: "owner"), view.Read(section: "resources"), view.Read(section: "network") };
        Assert.Equal(before, after);
        view.MarkRead(Args()); view.MarkRead(Args(moduleId: first.Id));
        view.ValidateProposal(Proposal(Node(first.Id)));
        Assert.DoesNotContain("changed", JsonSerializer.Serialize(view.Coverage, Json));
    }

    [Theory]
    [InlineData("[]")][InlineData("null")][InlineData("{\"unknown\":true}")]
    [InlineData("{\"moduleId\":null}")][InlineData("{\"moduleId\":2}")][InlineData("{\"moduleId\":\"\"}")]
    [InlineData("{\"moduleId\":\"invented\"}")][InlineData("{\"setting\":\"invented\"}")]
    [InlineData("{\"moduleId\":\"monitoring-log-analytics\",\"setting\":\"owner\"}")]
    [InlineData("{\"moduleId\":\"monitoring-log-analytics\",\"page\":0}")]
    [InlineData("{\"page\":0}")][InlineData("{\"section\":\"invented\"}")]
    [InlineData("{\"section\":\"resources\",\"page\":-1}")][InlineData("{\"section\":\"resources\",\"page\":1.5}")]
    [InlineData("{\"section\":\"resources\",\"page\":\"0\"}")][InlineData("{\"section\":\"resources\",\"page\":3}")]
    [InlineData("{\"section\":\"resources\",\"page\":0,\"page\":1}")]
    [InlineData("{\"setting\":\"owner\",\"setting\":\"location\"}")]
    public void InvalidOrAmbiguousLookupsFailWithoutChangingCoverage(string json)
    {
        var view = new BicepEvidenceView(Context()); var before = JsonSerializer.Serialize(view.Coverage, Json);
        var exception = Assert.Throws<PortalException>(() => view.ValidateRequest(Parse(json)));
        Assert.Equal("Invalid Bicep evidence lookup.", exception.Message);
        Assert.Equal(before, JsonSerializer.Serialize(view.Coverage, Json));
    }

    [Fact] public void CoverageRequiresDeliveredIndexAndEachSelectedContract()
    {
        var view = new BicepEvidenceView(Context()); var proposal = Proposal(Node());
        Assert.Throws<PortalException>(() => view.MarkRead(Args()));
        view.Read(); Assert.False(view.BaseRead); Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        view.MarkRead(Args()); Assert.True(view.BaseRead); Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        view.Read(moduleId: "monitoring-log-analytics"); Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        view.MarkRead(Args(moduleId: "monitoring-log-analytics")); view.ValidateProposal(proposal);
        Assert.Throws<PortalException>(() => view.ValidateProposal(Proposal(Node("avm-network-route-table"))));
    }

    [Fact] public void NonInlineSettingNeedsItsOwnSuccessfulLookup()
    {
        var view = new BicepEvidenceView(Context()); Deliver(view); Deliver(view, moduleId: "monitoring-log-analytics");
        var proposal = Proposal(Node(bindings: new() { ["name"] = new("Setting", "owner"), ["location"] = new("Setting", "location") }));
        Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        var value = Parse(Deliver(view, setting: "owner")); Assert.Equal("synthetic-owner", value.GetProperty("value").GetString());
        view.ValidateProposal(proposal);
    }

    [Fact] public void ResourceCoverageIncludesOnlyIdsFromSuccessfullyDeliveredPages()
    {
        var context = Context(); var view = new BicepEvidenceView(context); Deliver(view); Deliver(view, moduleId: "avm-monitoring-application-insights");
        var proposal = Proposal(Node("avm-monitoring-application-insights", new() { ["workspaceId"] = new("Resource", context.Resources[4].Id) }));
        var first = Parse(Deliver(view, section: "resources")); Assert.Equal(3, first.GetProperty("resources").GetArrayLength());
        Deliver(view, section: "network"); Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        view.Read(section: "resources", page: 1); Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
        view.MarkRead(Args(section: "resources", page: 1)); view.ValidateProposal(proposal);
        var coverage = JsonSerializer.Serialize(view.Coverage, Json); Assert.DoesNotContain("/subscriptions/", coverage);
        Assert.Equal(6, Parse(coverage).GetProperty("resources").GetArrayLength());
        Assert.Single(Parse(Deliver(view, section: "resources", page: 2)).GetProperty("resources").EnumerateArray());
    }

    [Fact] public void NetworkPagesReconstructExactFrozenJsonAndDoNotCountAsIndexOrModules()
    {
        var context = Context(); var view = new BicepEvidenceView(context); var chunks = new StringBuilder(); var page = 0; string? digest = null;
        while (true)
        {
            var text = Deliver(view, section: "network", page: page); Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 12_000);
            var result = Parse(text); chunks.Append(result.GetProperty("chunk").GetString());
            digest ??= result.GetProperty("contentSha256").GetString(); Assert.Equal(digest, result.GetProperty("contentSha256").GetString());
            if (result.GetProperty("nextPage").ValueKind == JsonValueKind.Null) break;
            page = result.GetProperty("nextPage").GetInt32();
        }
        Assert.True(page > 1); Assert.Equal(JsonSerializer.Serialize(context.NestedEvidence!.Value, Json), chunks.ToString());
        Assert.Equal(BicepDrafts.Hash(chunks.ToString()), digest); Assert.False(view.BaseRead);
        Assert.Empty(J(view.Coverage).GetProperty("modules").EnumerateArray());
        Assert.Throws<PortalException>(() => view.ValidateProposal(Proposal(Node())));
    }

    [Fact] public void OversizedItemsFailExplicitlyWithoutTruncationOrReadCoverage()
    {
        var context = Context(); context.Settings["owner"] = J(new string('x', 8_200));
        var module = context.Modules.Single(m => m.Id == "monitoring-log-analytics"); module.Parameters["oversizedFixture"] = J(new { type = "string", metadata = new { description = new string('x', 13_000) } });
        var view = new BicepEvidenceView(context);
        Assert.Throws<PortalException>(() => view.Read(setting: "owner")); Assert.Throws<PortalException>(() => view.Read(moduleId: module.Id));
        Assert.Throws<PortalException>(() => view.MarkRead(Args(setting: "owner"))); Assert.Throws<PortalException>(() => view.MarkRead(Args(moduleId: module.Id)));
        Assert.Empty(J(view.Coverage).GetProperty("modules").EnumerateArray()); Assert.Empty(J(view.Coverage).GetProperty("settings").EnumerateArray());
    }

    [Theory][InlineData("null")][InlineData("{}")][InlineData("{\"modules\":[]}")][InlineData("not-json")]
    public void MalformedProposalCannotBypassCoverage(string proposal)
    {
        var view = new BicepEvidenceView(Context()); Deliver(view);
        Assert.Throws<PortalException>(() => view.ValidateProposal(proposal));
    }
}
