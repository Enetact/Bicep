using System.Net.Http.Headers;
using System.Text.Json;

namespace SelfService.Portal;

public record SkillDiscoveryRequest(string SkillId, string SubscriptionId, string? ResourceGroup = null);
public record DiscoveryCollection(string Name, string Status, int Pages, int Observed, string? Issue, object[] Resources);

// A deterministic ARM reader, not an interpreter of upstream skill instructions.
public sealed class AzureDiscovery(HttpClient http, ITokenProvider identity, PortalOptions options, Catalog catalog)
{
    const string Arm = "https://management.azure.com";
    const string NetworkVersion = "2025-09-01";
    static readonly Dictionary<string, string[]> Providers = new()
    {
        ["inventory"] = [], ["network"] = [], ["compute"] = ["Microsoft.Compute/", "Microsoft.Web/"],
        ["kubernetes"] = ["Microsoft.ContainerService/", "Microsoft.Kubernetes/", "Microsoft.KubernetesConfiguration/"],
        ["storage"] = ["Microsoft.Storage/"], ["ai"] = ["Microsoft.CognitiveServices/", "Microsoft.MachineLearningServices/", "Microsoft.BotService/", "Microsoft.ApiManagement/"],
        ["messaging"] = ["Microsoft.EventGrid/", "Microsoft.EventHub/", "Microsoft.ServiceBus/"],
        ["monitoring"] = ["Microsoft.Insights/", "Microsoft.OperationalInsights/"], ["data"] = ["Microsoft.Kusto/"]
    };
    public async Task<DiscoveryCollection> ResourceGroups(BrowserSession session, string subscription, CancellationToken ct)
    {
        if (!Guid.TryParse(subscription, out var id) || !catalog.Products.SelectMany(p => p.Targets).Any(t => t.SubscriptionId.Equals(id.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new PortalException("Select a registered subscription.", 403);
        var token = await identity.Token(session, "azure");
        return await Collect(token, "Resource groups", $"/subscriptions/{id}/resourcegroups?api-version=2021-04-01", [], ct);
    }
    public async Task<object> Discover(BrowserSession session, SkillDiscoveryRequest request, CancellationToken cancellation)
    {
        var skill = catalog.Skills.SingleOrDefault(s => s.Id == request.SkillId) ?? throw new PortalException("Unknown skill.");
        if (!Providers.TryGetValue(skill.DiscoveryProfile, out var prefixes)) throw new PortalException("This skill has no Azure discovery adapter.");
        if (!Guid.TryParse(request.SubscriptionId, out var parsed)) throw new PortalException("Select a registered subscription.");
        var subscription = parsed.ToString();
        if (!catalog.Products.SelectMany(p => p.Targets).Any(t => t.SubscriptionId.Equals(subscription, StringComparison.OrdinalIgnoreCase)))
            throw new PortalException("Subscription is outside the registered platform scope.", 403);
        var token = await identity.Token(session, "azure"); // Authentication failure is never an empty inventory.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var scope = $"/subscriptions/{subscription}";
        if (request.ResourceGroup is not null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.ResourceGroup, "^[a-zA-Z0-9_.()-]{1,90}$")) throw new PortalException("Select a valid resource group.");
            scope += $"/resourceGroups/{Uri.EscapeDataString(request.ResourceGroup)}";
        }
        var collections = new List<DiscoveryCollection>
        {
            await Collect(token, "Resources", $"{scope}/resources?api-version=2021-04-01", prefixes, timeout.Token)
        };
        if (skill.DiscoveryProfile == "network")
        {
            // Fixed read routes. No supplied URL, query, CLI, shell, keys or ARM deployment operation.
            foreach (var item in new[] { ("Virtual networks", "virtualNetworks", NetworkVersion), ("Private DNS zones", "privateDnsZones", "2024-06-01"),
                ("Network security groups", "networkSecurityGroups", NetworkVersion), ("Route tables", "routeTables", NetworkVersion), ("Private endpoints", "privateEndpoints", NetworkVersion) })
                collections.Add(await Collect(token, item.Item1, $"{scope}/providers/Microsoft.Network/{item.Item2}?api-version={item.Item3}", [], timeout.Token));
        }
        var id = Guid.NewGuid().ToString("N");
        var report = new
        {
            schemaVersion = 1, kind = "portal-skill-discovery", id, generatedUtc = DateTimeOffset.UtcNow,
            skillId = skill.Id, skillName = skill.Name, profile = skill.DiscoveryProfile, subscriptionId = subscription, resourceGroup = request.ResourceGroup,
            pipelineStatus = "No pipeline associated yet", readOnly = true, deploymentAuthorized = false, allocationAuthorized = false,
            status = collections.All(c => c.Status is "Succeeded" or "SucceededEmpty") ? "Collected" : "Partial",
            coverage = "Only resources visible to the signed-in Azure user in this registered subscription. Successful collection is not proof of enterprise-wide visibility.",
            limitations = new[] {
                "Generic service discovery supplies resource metadata, not execution of the upstream skill or a full assessment.",
                "No billing, telemetry/log contents, data-plane records, Entra directory objects, secrets or keys are queried.",
                "Network records are configured inventory. Effective routes/rules, DNS resolution, endpoint reachability and remote subscriptions are unverified.",
                "IP occupancy, external address ranges, IPAM reservations, ownership and join rights are unknown. No subnet is ranked, reserved or approved.",
                "Missing resources are observations, not Create/Reuse permission. This report is not an ADO discovery manifest."
            }, collections
        };
        var directory = Path.Combine(options.RepositoryRoot, "artifacts/portal-discovery", id); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellation);
        return report;
    }
    async Task<DiscoveryCollection> Collect(string token, string name, string path, string[] prefixes, CancellationToken ct)
    {
        var start = new Uri(Arm + path); var next = start; var seen = new HashSet<string>(StringComparer.Ordinal); var resources = new List<object>();
        int pages = 0, observed = 0;
        DiscoveryCollection Failed(string issue) => new(name, pages > 0 ? "Partial" : "Failed", pages, observed, issue, resources.ToArray());
        try
        {
            while (next is not null)
            {
                // Continuations may only vary the query of this exact ARM collection.
                if (next.Scheme != "https" || next.Host != "management.azure.com" || next.Port != 443 || next.UserInfo.Length != 0 || next.Fragment.Length != 0 ||
                    !next.AbsolutePath.Equals(start.AbsolutePath, StringComparison.OrdinalIgnoreCase) || !seen.Add(next.AbsoluteUri))
                    return Failed("Unsafe or repeated pagination link rejected; remaining coverage unknown.");
                if (pages >= 10 || observed >= 5000) return Failed("Collection limit reached; remaining coverage unknown.");
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return Failed($"HTTP {(int)response.StatusCode}; collection unavailable. This is not an empty result.");
                using var stream = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream();
                var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (bytes.Length + read > 4 * 1024 * 1024) return Failed("Response exceeded 4 MiB; collection incomplete.");
                    bytes.Write(buffer, 0, read);
                }
                var page = JsonSerializer.Deserialize<JsonElement>(bytes.ToArray());
                if (!page.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array) return Failed("Unexpected response schema; coverage unknown.");
                pages++;
                foreach (var value in values.EnumerateArray())
                {
                    if (observed >= 5000) return Failed("Resource limit reached; remaining coverage unknown.");
                    var resourceId = Text(value, "id");
                    if (resourceId is null || !resourceId.StartsWith($"/subscriptions/{start.AbsolutePath.Split('/')[2]}/", StringComparison.OrdinalIgnoreCase))
                        return Failed("Invalid or out-of-scope resource identifier; remaining coverage unknown.");
                    var groupParts = start.AbsolutePath.Split('/');
                    if (groupParts.Length > 4 && groupParts[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) &&
                        !resourceId.StartsWith($"/subscriptions/{groupParts[2]}/resourceGroups/{Uri.UnescapeDataString(groupParts[4])}/", StringComparison.OrdinalIgnoreCase))
                        return Failed("Resource group scope mismatch; remaining coverage unknown.");
                    observed++;
                    var type = Text(value, "type") ?? "";
                    if (prefixes.Length == 0 || prefixes.Any(p => type.StartsWith(p, StringComparison.OrdinalIgnoreCase))) resources.Add(Project(value, name));
                }
                if (!page.TryGetProperty("nextLink", out var link) || link.ValueKind == JsonValueKind.Null || link.GetString() == "") next = null;
                else if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out next)) return Failed("Invalid pagination link; remaining coverage unknown.");
            }
            return new(name, resources.Count == 0 ? "SucceededEmpty" : "Succeeded", pages, observed,
                resources.Count == 0 ? "No matching visible resources in the queried scope." : null, resources.ToArray());
        }
        catch (OperationCanceledException) { return Failed("Collection cancelled or timed out; remaining coverage unknown."); }
        catch (HttpRequestException) { return Failed("Azure could not be reached; remaining coverage unknown."); }
        catch (JsonException) { return Failed("Azure returned invalid JSON; coverage unknown."); }
        catch (InvalidOperationException) { return Failed("Unexpected response schema; coverage unknown."); }
    }
    static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static JsonElement Property(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    static object? Simple(JsonElement e, string key)
    {
        var value = Property(e, key);
        return value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number : null,
            JsonValueKind.Array => value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray(), _ => null };
    }
    static Dictionary<string, object?> Fields(JsonElement e, params string[] keys) => keys.ToDictionary(k => k, k => Simple(e, k));
    static object[] Children(JsonElement e, string key, Func<JsonElement, object> select)
    {
        var values = Property(e, key); return values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(select).ToArray() : [];
    }
    static object Project(JsonElement resource, string collection)
    {
        var result = Fields(resource, "id", "name", "type", "location"); var p = Property(resource, "properties");
        // Whitelist network facts; never forward arbitrary tags/settings/connection material.
        if (collection == "Virtual networks")
        {
            result["addressPrefixes"] = Simple(Property(p, "addressSpace"), "addressPrefixes");
            result["dnsServers"] = Simple(Property(p, "dhcpOptions"), "dnsServers");
            result["subnetsEvidence"] = Property(p, "subnets").ValueKind == JsonValueKind.Array ? "ReportedInline; occupancy unverified" : "Unknown; inline collection missing";
            result["peeringsEvidence"] = Property(p, "virtualNetworkPeerings").ValueKind == JsonValueKind.Array ? "ReportedInline; remote network unverified" : "Unknown; inline collection missing";
            result["subnets"] = Children(p, "subnets", subnet => {
                var s = Property(subnet, "properties"); var row = Fields(subnet, "id", "name");
                row["configured"] = Fields(s, "addressPrefix", "addressPrefixes", "privateEndpointNetworkPolicies", "privateLinkServiceNetworkPolicies");
                row["nsgId"] = Text(Property(s, "networkSecurityGroup"), "id"); row["routeTableId"] = Text(Property(s, "routeTable"), "id");
                row["delegations"] = Children(s, "delegations", d => Fields(Property(d, "properties"), "serviceName"));
                row["occupancy"] = "Unknown; inline inventory does not establish available capacity"; return row;
            });
            result["peerings"] = Children(p, "virtualNetworkPeerings", peering => {
                var pp = Property(peering, "properties"); var row = Fields(peering, "id", "name");
                row["remoteVnetId"] = Text(Property(pp, "remoteVirtualNetwork"), "id");
                row["configured"] = Fields(pp, "peeringState", "allowVirtualNetworkAccess", "allowForwardedTraffic", "allowGatewayTransit", "useRemoteGateways"); return row;
            });
        }
        if (collection == "Network security groups")
        {
            object Rule(JsonElement rule) => new { name = Text(rule, "name"), configured = Fields(Property(rule, "properties"), "priority", "direction", "access", "protocol", "sourceAddressPrefix", "sourceAddressPrefixes", "destinationAddressPrefix", "destinationAddressPrefixes", "sourcePortRange", "sourcePortRanges", "destinationPortRange", "destinationPortRanges") };
            result["rules"] = Children(p, "securityRules", Rule); result["defaultRules"] = Children(p, "defaultSecurityRules", Rule);
            result["rulesEvidence"] = Property(p, "securityRules").ValueKind == JsonValueKind.Array ? "ConfiguredOnly; effective rules unverified" : "Unknown; inline rules missing";
        }
        if (collection == "Route tables")
        {
            result["routes"] = Children(p, "routes", route => new { name = Text(route, "name"), configured = Fields(Property(route, "properties"), "addressPrefix", "nextHopType", "nextHopIpAddress") });
            result["routesEvidence"] = Property(p, "routes").ValueKind == JsonValueKind.Array ? "ConfiguredOnly; effective routes unverified" : "Unknown; inline routes missing";
        }
        if (collection == "Private endpoints")
        {
            result["subnetId"] = Text(Property(p, "subnet"), "id");
            result["connections"] = Children(p, "privateLinkServiceConnections", connection => new { configured = Fields(Property(connection, "properties"), "privateLinkServiceId", "groupIds"), state = Text(Property(Property(connection, "properties"), "privateLinkServiceConnectionState"), "status") });
        }
        if (collection == "Private DNS zones") result["resolution"] = "Unknown; zones listed, links/records/resolver paths not queried";
        return result;
    }
}
