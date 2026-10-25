using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AvmChildValueTests
{
    static string Root { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "config/workloads.json"))) d = d.Parent; return d!.FullName; } }
    static readonly (string Module, string Parameter, string Value)[] Examples =
    [
        ("avm-identity-user-assigned-identity", "federatedIdentityCredentials", """[{"name":"synthetic-federation","issuer":"https://issuer.example.test/","subject":"repo:example/repo:environment:reviewed","audiences":["api://AzureADTokenExchange"]}]"""),
        ("avm-containers-container-registry", "replications", """[{"name":"westus3","location":"westus3","zoneRedundancy":"Disabled"}]"""),
        ("avm-configuration-app-configuration", "keyValues", """[{"name":"synthetic-setting","value":"O'Brien ${untrusted.expression}","contentType":"text/plain"}]"""),
        ("avm-configuration-app-configuration", "replicaLocations", """[{"name":"syntheticreplica","replicaLocation":"westus3"}]"""),
        ("avm-network-network-security-group", "securityRules", """[{"name":"deny-inbound","properties":{"priority":100,"access":"Deny","direction":"Inbound","protocol":"*","sourceAddressPrefix":"*","sourcePortRange":"*","destinationAddressPrefix":"*","destinationPortRange":"*"}}]"""),
        ("avm-network-route-table", "routes", """[{"name":"drop-review-range","properties":{"addressPrefix":"10.250.0.0/16","nextHopType":"None"}}]"""),
        ("avm-network-route-table", "applianceRoutes", """[{"name":"reviewed-appliance","properties":{"addressPrefix":"10.251.0.0/16","nextHopIpAddress":"10.250.0.4"}}]""")
    ];

    static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
    static JsonElement Schema(string module, string parameter)
    {
        var contract = BicepDrafts.Catalog(Root).Modules.Single(m => m.Id == module);
        return BicepParameterSchema.Expand(contract.Parameters[parameter], contract.Definitions);
    }
    public static IEnumerable<object[]> Collections() => Examples.Select(x => new object[] { x.Module, x.Parameter, x.Value });

    [Theory]
    [MemberData(nameof(Collections))]
    public void EveryActualChildCollectionAcceptsItsEmptyDefaultAndReviewedExample(string module, string parameter, string example)
    {
        var schema = Schema(module, parameter);
        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal(0, schema.GetProperty("defaultValue").GetArrayLength());
        Assert.False(schema.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        BicepParameterSchema.Validate(Parse("[]"), schema);
        BicepParameterSchema.Validate(Parse(example), schema);
    }

    public static IEnumerable<object[]> InvalidCollections()
    {
        foreach (var example in Examples)
        foreach (var mutation in new[] { "unknown-property", "missing-name", "wrong-name-type", "empty-name", "wrong-collection-type", "too-many-children" })
            yield return [example.Module, example.Parameter, example.Value, mutation];
    }

    [Theory]
    [MemberData(nameof(InvalidCollections))]
    public void EveryActualChildCollectionRejectsUnknownMissingMistypedAndUnboundedData(string module, string parameter, string example, string mutation)
    {
        var schema = Schema(module, parameter);
        JsonNode value = JsonNode.Parse(example)!;
        var child = value.AsArray()[0]!.AsObject();
        switch (mutation)
        {
            case "unknown-property": child["unreviewed"] = true; break;
            case "missing-name": child.Remove("name"); break;
            case "wrong-name-type": child["name"] = 123; break;
            case "empty-name": child["name"] = ""; break;
            case "wrong-collection-type": value = new JsonObject(); break;
            case "too-many-children":
                var array = new JsonArray();
                for (var i = 0; i <= schema.GetProperty("maxLength").GetInt32(); i++) array.Add(child.DeepClone());
                value = array; break;
            default: throw new InvalidOperationException("Unknown test mutation.");
        }
        Assert.Throws<PortalException>(() => BicepParameterSchema.Validate(Parse(value.ToJsonString()), schema));
    }

    public static IEnumerable<object[]> SpecificInvalidValues()
    {
        yield return [Examples[0].Module, Examples[0].Parameter, """[{"name":"synthetic-federation","issuer":"https://issuer.example.test/","subject":"reviewed","audiences":["invented-audience"]}]"""];
        yield return [Examples[1].Module, Examples[1].Parameter, """[{"name":"westus3","location":"westus3","zoneRedundancy":"Bypass"}]"""];
        yield return [Examples[2].Module, Examples[2].Parameter, """[{"name":"synthetic-setting","value":"test","contentType":"text/html"}]"""];
        yield return [Examples[3].Module, Examples[3].Parameter, """[{"name":"syntheticreplica","replicaLocation":""}]"""];
        yield return [Examples[4].Module, Examples[4].Parameter, """[{"name":"deny-inbound","properties":{"priority":99,"access":"Deny","direction":"Inbound","protocol":"*","sourceAddressPrefix":"*","sourcePortRange":"*","destinationAddressPrefix":"*","destinationPortRange":"*"}}]"""];
        yield return [Examples[5].Module, Examples[5].Parameter, """[{"name":"unreviewed-egress","properties":{"addressPrefix":"0.0.0.0/0","nextHopType":"Internet"}}]"""];
        yield return [Examples[6].Module, Examples[6].Parameter, """[{"name":"missing-appliance","properties":{"addressPrefix":"10.251.0.0/16"}}]"""];
    }

    [Theory]
    [MemberData(nameof(SpecificInvalidValues))]
    public void ActualProviderChildConstraintsRejectInvalidEnumRangeAndRequiredNextHop(string module, string parameter, string value) =>
        Assert.Throws<PortalException>(() => BicepParameterSchema.Validate(Parse(value), Schema(module, parameter)));
}
