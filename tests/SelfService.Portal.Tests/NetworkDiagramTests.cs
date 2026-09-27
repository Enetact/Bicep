using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class NetworkDiagramTests
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, Options);
    const string Root = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Network/";
    static EvidenceGraph Graph() => EvidenceDiagram.Build(Json(new { collections = new[] { new { name = "Virtual networks", resources = new[] {
        new { id = Root + "virtualNetworks/v", name = "Actual VNet", subnets = new[] {new { id = Root + "virtualNetworks/v/subnets/s", name = "Actual subnet", nsgId = Root + "networkSecurityGroups/n" }} }
    } } } }));
    static string Source(EvidenceGraph g, string? extra = null)
    {
        var declarations = string.Join("\n", g.Nodes.Select(n => $"{n.Id}[\"agent label\"]"));
        var edges = string.Join("\n", g.Edges.Select(e => $"{e.From} -->|{e.Label}| {e.To}"));
        return $"Review\n```mermaid\ngraph TB\n{declarations}\n{edges}\n{extra}\n```";
    }
    [Fact] public void ValidDiagramUsesCanonicalNamesAndKeepsReferenceUncertainty()
    {
        var graph = Graph(); var result = EvidenceDiagram.Validate(Source(graph), graph);
        Assert.Equal("Validated", result.Status); Assert.Equal(graph.Nodes.Length, result.Nodes.Length);
        Assert.Contains(result.Nodes, n => n.Label == "Actual VNet"); Assert.DoesNotContain(result.Nodes, n => n.Label == "agent label");
        Assert.Contains(result.Nodes, n => n.Status == "Referenced only"); Assert.NotNull(result.Source);
    }
    [Theory]
    [InlineData("click R001 href \"https://evil.test\"")]
    [InlineData("%%{init: {securityLevel: 'loose'}}%%")]
    [InlineData("style R001 fill:url(https://evil.test)")]
    [InlineData("R999[\"invented\"]")]
    [InlineData("R004[\"<script>alert(1)</script>\"]")]
    [InlineData("R001 -->|verified connectivity| R002")]
    [InlineData("R001 -->|configured subnet| R001")]
    public void UnsafeOrInventedDiagramIsNeverRendered(string attack)
    {
        var g = Graph(); var r = EvidenceDiagram.Validate(Source(g, attack), g);
        Assert.Equal("Rejected", r.Status); Assert.Empty(r.Nodes); Assert.Null(r.Source);
    }
    [Fact] public void MissingAndMultipleBlocksRemainUntrustedText()
    {
        var g = Graph(); Assert.Equal("Rejected", EvidenceDiagram.Validate("No diagram", g).Status);
        Assert.Equal("Rejected", EvidenceDiagram.Validate(Source(g) + Source(g), g).Status);
    }
    [Fact] public void OversizedGraphFailsBeforeInference()
    {
        var e = Json(new { collections = new[] { new { resources = Enumerable.Range(0,201).Select(i => new {id = Root + "virtualNetworks/v" + i}) } } });
        Assert.Throws<PortalException>(() => EvidenceDiagram.Build(e));
    }
    [Theory][InlineData("10.0.0.0/26", true, 64)][InlineData("10.0.0.1/26", false, 0)][InlineData("2001:db8::/64", false, 0)][InlineData("10.0.0.0/33", false, 0)][InlineData("0.0.0.0/0", true, 4294967296L)]
    public void PrefixArithmeticIsBoundedAndCanonical(string cidr, bool valid, long size)
    { Assert.Equal(valid, Ipv4Range.TryParse(cidr, out var range)); if(valid) Assert.Equal((ulong)size, range.Size); }
    [Fact] public void AssessmentDetectsOverlapAndNeverCallsCapacityFree()
    {
        var input = Json(new { collections = new[] {new {name="Virtual networks",status="Succeeded",resources=new[] {new {id=Root+"virtualNetworks/v",addressPrefixes=new[]{"10.0.0.0/24"},subnets=new[]{
            new{id=Root+"virtualNetworks/v/subnets/a",configured=new{addressPrefix="10.0.0.0/26"}},
            new{id=Root+"virtualNetworks/v/subnets/b",configured=new{addressPrefix="10.0.0.32/27"}}
        }}}}} });
        var result = Json(NetworkAssessment.Evaluate(input));
        Assert.False(result.GetProperty("allocationAuthorized").GetBoolean());
        Assert.Contains(result.GetProperty("findings").EnumerateArray(), f => f.GetProperty("rule").GetString()=="subnet-overlap" && f.GetProperty("state").GetString()=="Conflict");
        Assert.All(result.GetProperty("findings").EnumerateArray().Where(f=>f.GetProperty("rule").GetString()=="capacity"), f=>Assert.Equal("Unknown", f.GetProperty("state").GetString()));
    }
    [Fact] public void AllocationMenuCannotEnableWritesOrSelectArbitraryOperation()
    {
        var disabled=Json(new{poolId="/configured-pool",enabled=false,exclusiveLockReviewed=false,externalPrefixesReconciled=false});
        NetworkPipeline.ValidateProfile(disabled,new("httpapi","dev","Plan only"));
        Assert.Throws<PortalException>(()=>NetworkPipeline.ValidateProfile(disabled,new("httpapi","dev","Reserve only")));
        Assert.Throws<PortalException>(()=>NetworkPipeline.ValidateProfile(disabled,new("httpapi","dev","Delete")));
        var body=Json(NetworkPipeline.Payload(new("httpapi","dev","Plan only")));
        Assert.Equal("refs/heads/main",body.GetProperty("resources").GetProperty("repositories").GetProperty("self").GetProperty("refName").GetString());
    }
}
