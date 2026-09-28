using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BicepDraftTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static string Root { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "config/workloads.json"))) d = d.Parent; return d!.FullName; } }
    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Json);
    static BicepDraftContext Context()
    {
        var modules = BicepDrafts.Catalog(Root).Modules.Where(m => m.DraftSelectable).ToArray();
        return new("platform.bicep-context/v1", "evidence-fixture", BicepDrafts.Hash(JsonSerializer.Serialize(modules, Json)),
            "11111111-1111-1111-1111-111111111111", "storage", "dev", "eastus2", "Private storage", "Partial",
            new() { ["location"] = J("eastus2"), ["owner"] = J("O'Brien ${fake.expression}") },
            [new("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/shared/providers/Microsoft.OperationalInsights/workspaces/logs", "Microsoft.OperationalInsights/workspaces")], modules);
    }
    static BicepNode Node(string id = "workspace", string module = "monitoring-log-analytics", Dictionary<string, BicepBinding>? bindings = null) => new(id, module, "Synthetic source draft test", bindings ?? new() { ["location"] = new("Setting", "location") });
    static string Proposal(params BicepNode[] nodes) => JsonSerializer.Serialize(new BicepProposal("platform.bicep-proposal/v1", "evidence-fixture", "Synthetic source proposal", nodes, ["Manual qualification required"]), Json);
    [Fact] public void EverySelectableModuleEmitsSourceForCompilation()
    {
        var context = Context(); Assert.Equal(12, context.Modules.Length);
        foreach (var module in context.Modules)
        {
            var result = BicepDrafts.Generate(Root, context, Proposal(Node("component", module.Id, new())));
            Assert.Equal("UnqualifiedSourceDraft", result.Status);
            Assert.Contains("Required", result.Files["draft-receipt.json"]);
            Assert.DoesNotContain("deploymentAuthorized\": true", result.Files["draft-receipt.json"]);
            var directory = Path.Combine(Root, "artifacts/bicep-draft-tests", module.Id);
            foreach (var (path, content) in result.Files) { var file = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content); }
        }
    }
    [Fact] public void ObservedAndOutputBindingsRetainProvenance()
    {
        var context = Context();
        var result = BicepDrafts.Generate(Root, context, Proposal(Node(), Node("store", "storage-storage-account", new() { ["workspaceId"] = new("Output", Module: "workspace", Output: "id") }),
            Node("vault", "security-key-vault", new() { ["workspaceId"] = new("Resource", context.Resources[0].Id) })));
        Assert.Contains("workspace.outputs.id", result.Files["main.bicep"]);
        Assert.Contains("Observed ID", result.Files["parameter-provenance.json"]);
        Assert.Contains("p_store_name", result.RequiredInputs);
    }
    [Theory][InlineData("../../escape")][InlineData("a'b")][InlineData("x${evil}")][InlineData("_bad")]
    public void NodeCodeAndPathInjectionBlocked(string id) => Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(id))));
    [Theory][InlineData("module-path")][InlineData("network-ipam-reservation")]
    public void UnapprovedModuleBlocked(string module) => Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(module: module))));
    [Theory][InlineData("Expression", "location")][InlineData("Setting", "invented")][InlineData("Resource", "/subscriptions/invented/resource")]
    public void UntrustedBindingRejected(string kind, string reference) => Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(bindings: new() { ["name"] = new(kind, reference) }))));
    [Fact] public void ValueRemainsDataOutsideBicepSource()
    {
        var context = Context(); context.Settings["tags"] = J(new { owner = "fixture", environment = "dev" });
        var result = BicepDrafts.Generate(Root, context, Proposal(Node(bindings: new() { ["name"] = new("Setting", "owner"), ["location"] = new("Setting", "location"), ["tags"] = new("Setting", "tags") })));
        Assert.DoesNotContain("fake.expression", result.Files["main.bicep"]);
        Assert.Contains("fake.expression", result.Files["resolved-values.json"]);
        Assert.Empty(result.RequiredInputs);
        var directory = Path.Combine(Root, "artifacts/bicep-draft-tests/resolved-data");
        foreach (var (path, content) in result.Files) { var file = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content); }
    }
    [Fact] public void DuplicateNodeAndCycleRejected()
    {
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(), Node("Workspace"))));
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(
            Node("a", bindings: new() { ["name"] = new("Output", Module: "b", Output: "id") }),
            Node("b", bindings: new() { ["name"] = new("Output", Module: "a", Output: "id") }))));
    }
    [Fact] public void WrongOutputTypeAndUnknownParameterRejected()
    {
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(), Node("other", bindings: new() { ["tags"] = new("Output", Module: "workspace", Output: "id") }))));
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node(bindings: new() { ["arbitrary"] = new("Input") }))));
    }
    [Fact] public void WrongEvidenceOrChangedCatalogRejected()
    {
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context() with { EvidenceDigest = "other" }, Proposal(Node())));
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, Context() with { CatalogDigest = "changed" }, Proposal(Node())));
    }
    [Fact] public void UnknownJsonAndLiteralValuesRejected()
    {
        Assert.ThrowsAny<Exception>(() => BicepDrafts.Generate(Root, Context(), Proposal(Node()).Replace("\"kind\": \"Setting\"", "\"kind\": \"Setting\", \"value\": \"injected\"")));
    }
    [Fact] public void ContextRequiresBrowserOwnedFreshSubscriptionEvidence()
    {
        var catalog = new Catalog(new() { RepositoryRoot = Root }); using var session = new BrowserSession(new());
        var request = new AgentRequest("bicep-draft", null, null, "storage", "dev", "eastus2", null, "snapshot", DraftGoal: "A private workspace");
        Assert.Throws<PortalException>(() => BicepDrafts.Context(Root, catalog, session, request));
        var subscription = catalog.Products.Single(p => p.Id == "storage").Targets.First().SubscriptionId;
        object Snapshot(DateTimeOffset time, string sub) => new { id = "snapshot", generatedUtc = time, reports = new[] { new { subscriptionId = sub, status = "Partial", collections = Array.Empty<object>() } } };
        session.NetworkReports["snapshot"] = J(Snapshot(DateTimeOffset.UtcNow.AddHours(-1), subscription));
        Assert.Throws<PortalException>(() => BicepDrafts.Context(Root, catalog, session, request));
        session.NetworkReports["snapshot"] = J(Snapshot(DateTimeOffset.UtcNow, "other"));
        Assert.Throws<PortalException>(() => BicepDrafts.Context(Root, catalog, session, request));
        session.NetworkReports["snapshot"] = J(Snapshot(DateTimeOffset.UtcNow, subscription));
        var context = BicepDrafts.Context(Root, catalog, session, request);
        Assert.Equal("Partial", context.DiscoveryStatus); Assert.Empty(context.Resources);
        Assert.Equal("eastus2", context.Settings["location"].GetString());
    }
}
