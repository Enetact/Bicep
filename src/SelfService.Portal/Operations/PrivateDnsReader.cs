using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfService.Operations;

namespace SelfService.Portal;

// Fixed ARM GET adapter. Model output and user input never supply endpoints or tool names.
public sealed class PrivateDnsReader(HttpClient http)
{
    const string Arm = "https://management.azure.com";
    const string DnsVersion = "2024-06-01";
    const string NetworkVersion = "2025-09-01";
    const string ResolverVersion = "2022-07-01";
    static string Text(JsonElement e, string key) => EvidenceDiagram.Text(e, key) ?? "";
    public static bool ResourcePath(string id, string subscription, string type) => !id.Split('/').Any(p => p is "." or "..") && Regex.IsMatch(id,
        "^/subscriptions/" + Regex.Escape(subscription) + "/resourceGroups/[^/?#%\\\\]+/providers/Microsoft.Network/" + Regex.Escape(type) + "/[^/?#%\\\\]+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    public async Task<OperationsEvidence> Collect(string token, string tenant, string[] subscriptions, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow; var coverage = new List<CollectionCoverage>(); var resources = new Dictionary<string,ObservedResource>(StringComparer.OrdinalIgnoreCase);
        var childCount = 0;
        async Task<JsonElement[]> Read(string scope, string kind, string suffix, string version, string expectedType, string? parent = null)
        {
            if (resources.Count >= 5000 || ++childCount > 220) { coverage.Add(new(scope, kind, "Partial", 0, 0, "Scan resource/request budget reached.")); return []; }
            var result = await List(token, scope + suffix + "?api-version=" + version, scope, kind, ct);
            coverage.Add(result.Coverage);
            var valid = new List<JsonElement>();
            foreach (var r in result.Values)
            {
                var id = Text(r, "id"); var sub = scope.Split('/')[2];
                var type = Text(r, "type");
                var accepted = parent is null ? ResourcePath(id, sub, suffix.Split('/').Last()) : id.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase) && !id.Contains('?') && !id.Contains('#') && !id.Contains('%');
                if (!accepted || !(type.Equals(expectedType, StringComparison.OrdinalIgnoreCase) || kind == "Records" && type.StartsWith(expectedType + "/", StringComparison.OrdinalIgnoreCase)))
                { coverage.Add(new(scope, kind, "Partial", 0, 0, "Out-of-scope or unexpected resource type rejected.")); continue; }
                if (resources.Count >= 5000) { coverage.Add(new(scope, kind, "Partial", 0, 0, "Resource limit reached.")); break; }
                var item = Project(r, parent);
                if (!resources.TryAdd(item.Id, item)) coverage.Add(new(scope, kind, "Partial", 0, 0, "Duplicate resource observation; consistency unknown."));
                else valid.Add(r);
            }
            return valid.ToArray();
        }
        foreach (var sub in subscriptions)
        {
            ct.ThrowIfCancellationRequested(); var scope = $"/subscriptions/{sub}";
            var zones = await Read(scope, "Zones", "/providers/Microsoft.Network/privateDnsZones", DnsVersion, "Microsoft.Network/privateDnsZones");
            if (coverage.Any(c => c.Scope == scope && c.Kind == "Zones" && c.Status is not ("Succeeded" or "SucceededEmpty")))
            {
                var fallback = await List(token, scope + "/resources?api-version=2021-04-01", scope, "Zone metadata fallback", ct);
                coverage.Add(fallback.Coverage with { Issue = DnsInventoryFallback.Limitation + " " + fallback.Coverage.Issue });
                var recovered = DnsInventoryFallback.Zones(fallback.Values, sub);
                foreach (var row in recovered) { var item = Project(row, null); if (resources.Count < 5000) resources.TryAdd(item.Id, item); }
                zones = zones.Concat(recovered).DistinctBy(r => Text(r, "id"), StringComparer.OrdinalIgnoreCase).ToArray();
            }
            foreach (var zone in zones.Take(50))
            {
                var id = Text(zone, "id");
                await Read(id, "Records", "/ALL", DnsVersion, "Microsoft.Network/privateDnsZones", id);
                await Read(id, "VNet links", "/virtualNetworkLinks", DnsVersion, "Microsoft.Network/privateDnsZones/virtualNetworkLinks", id);
            }
            if (zones.Length > 50) coverage.Add(new(scope, "DNS children", "Partial", 0, 0, "Only the first 50 zones have child coverage. Split the registered scope."));
            var endpoints = await Read(scope, "Private endpoints", "/providers/Microsoft.Network/privateEndpoints", NetworkVersion, "Microsoft.Network/privateEndpoints");
            foreach (var endpoint in endpoints.Take(50))
            {
                var id = Text(endpoint, "id");
                await Read(id, "Zone groups", "/privateDnsZoneGroups", NetworkVersion, "Microsoft.Network/privateEndpoints/privateDnsZoneGroups", id);
            }
            if (endpoints.Length > 50) coverage.Add(new(scope, "Zone groups", "Partial", 0, 0, "Only the first 50 endpoints have zone-group coverage."));
            await Read(scope, "Virtual networks", "/providers/Microsoft.Network/virtualNetworks", NetworkVersion, "Microsoft.Network/virtualNetworks");
            var resolvers = await Read(scope, "Resolvers", "/providers/Microsoft.Network/dnsResolvers", ResolverVersion, "Microsoft.Network/dnsResolvers");
            foreach (var resolver in resolvers.Take(20))
            {
                var id = Text(resolver, "id");
                await Read(id, "Resolver inbound endpoints", "/inboundEndpoints", ResolverVersion, "Microsoft.Network/dnsResolvers/inboundEndpoints", id);
                await Read(id, "Resolver outbound endpoints", "/outboundEndpoints", ResolverVersion, "Microsoft.Network/dnsResolvers/outboundEndpoints", id);
            }
            if (resolvers.Length > 20) coverage.Add(new(scope, "Resolver endpoints", "Partial", 0, 0, "Resolver child limit reached."));
            var rulesets = await Read(scope, "Forwarding rulesets", "/providers/Microsoft.Network/dnsForwardingRulesets", ResolverVersion, "Microsoft.Network/dnsForwardingRulesets");
            foreach (var ruleset in rulesets.Take(20))
            {
                var id = Text(ruleset, "id");
                await Read(id, "Forwarding rules", "/forwardingRules", ResolverVersion, "Microsoft.Network/dnsForwardingRulesets/forwardingRules", id);
                await Read(id, "Ruleset links", "/virtualNetworkLinks", ResolverVersion, "Microsoft.Network/dnsForwardingRulesets/virtualNetworkLinks", id);
            }
            if (rulesets.Length > 20) coverage.Add(new(scope, "Ruleset children", "Partial", 0, 0, "Ruleset child limit reached."));
        }
        return OperationsJson.Seal(new("platform.operations-evidence/v1", Guid.NewGuid().ToString("N"), tenant, subscriptions.Order().ToArray(), started,
            DateTimeOffset.UtcNow, "private-dns-arm/v1; browser Azure identity; provider-specific REST GET", coverage.ToArray(), resources.Values.OrderBy(r => r.Id).ToArray()));
    }
    public async Task<(CollectionCoverage Coverage, JsonElement[] Values)> List(string token, string path, string scope, string kind, CancellationToken ct)
    {
        var first = new Uri(Arm + path); var next = first; var seen = new HashSet<string>(); var rows = new List<JsonElement>(); int pages = 0;
        (CollectionCoverage, JsonElement[]) Failed(string state, string issue) => (new(scope, kind, pages > 0 ? "Partial" : state, pages, rows.Count, issue), rows.ToArray());
        try
        {
            while (next is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (next.Scheme != "https" || next.Host != "management.azure.com" || next.Port != 443 || next.UserInfo != "" || next.Fragment != "" || !next.AbsolutePath.Equals(first.AbsolutePath, StringComparison.OrdinalIgnoreCase) || !seen.Add(next.AbsoluteUri))
                    return Failed("Failed", "Unsafe or repeated continuation rejected.");
                if (pages >= 10 || rows.Count >= 5000) return Failed("Partial", "Pagination budget reached.");
                using var request = new HttpRequestMessage(HttpMethod.Get, next); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return Failed(response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "Denied" : "Failed", $"HTTP {(int)response.StatusCode}; not an empty collection.");
                using var stream = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (bytes.Length + read > 4 * 1024 * 1024) return Failed("Partial", "Response exceeds 4 MiB."); bytes.Write(buffer, 0, read); }
                var page = JsonSerializer.Deserialize<JsonElement>(bytes.ToArray());
                if (!page.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array) return Failed("Failed", "Unexpected list schema.");
                pages++;
                foreach (var value in values.EnumerateArray()) { if (rows.Count >= 5000) return Failed("Partial", "Resource limit reached."); rows.Add(value.Clone()); }
                if (!page.TryGetProperty("nextLink", out var link) || link.ValueKind == JsonValueKind.Null || link.GetString() == "") next = null;
                else if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out next)) return Failed("Failed", "Malformed continuation.");
            }
            return (new(scope, kind, rows.Count == 0 ? "SucceededEmpty" : "Succeeded", pages, rows.Count, null), rows.ToArray());
        }
        catch (HttpRequestException) { return Failed("Failed", "Azure request failed; coverage unknown."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return Failed("Failed", "Invalid Azure response schema."); }
    }
    public static ObservedResource Project(JsonElement row, string? parent)
    {
        var p = EvidenceDiagram.Part(row, "properties"); var type = Text(row, "type").ToLowerInvariant();
        var values = new SortedDictionary<string,string>(StringComparer.Ordinal);
        void Scalar(string target, JsonElement source, string key) { var v = EvidenceDiagram.Part(source, key); if (v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) values[target] = v.ValueKind == JsonValueKind.String ? v.GetString()![..Math.Min(v.GetString()!.Length, 4096)] : v.GetRawText(); }
        void Strings(string target, IEnumerable<string> strings) => values[target] = JsonSerializer.Serialize(strings.Where(s => s.Length is > 0 and <= 2048).Distinct().Order());
        Scalar("etag", row, "etag"); Scalar("provisioningState", p, "provisioningState");
        if (type.StartsWith(PrivateDnsRules.ZoneType + "/") && !type.EndsWith("/virtualnetworklinks"))
        {
            values["recordType"] = type.Split('/').Last().ToUpperInvariant(); Scalar("ttl", p, "ttl");
            values["ownership"] = EvidenceDiagram.Part(p, "isAutoRegistered").ValueKind == JsonValueKind.True ? "AutoRegistered" : "Unknown";
            // TXT values and arbitrary metadata may contain secrets. Never retain/project them.
            var recordType = values["recordType"];
            if (recordType is "A" or "AAAA" or "PTR") Strings("values", EvidenceDiagram.Array(p, recordType == "A" ? "aRecords" : recordType == "AAAA" ? "aaaaRecords" : "ptrRecords").Select(r => Text(r, recordType == "A" ? "ipv4Address" : recordType == "AAAA" ? "ipv6Address" : "ptrdname")));
            else if (recordType == "CNAME") Strings("values", [Text(EvidenceDiagram.Part(p, "cnameRecord"), "cname")]);
            else values["valueCoverage"] = "Unsupported record-value profile; values omitted";
        }
        if (type.EndsWith("/virtualnetworklinks")) { Scalar("registrationEnabled", p, "registrationEnabled"); Scalar("linkState", p, "virtualNetworkLinkState"); Scalar("virtualNetworkId", EvidenceDiagram.Part(p, "virtualNetwork"), "id"); }
        if (type.EndsWith("/privatednszonegroups")) Strings("zoneIds", EvidenceDiagram.Array(p, "privateDnsZoneConfigs").Select(z => Text(EvidenceDiagram.Part(z, "properties"), "privateDnsZoneId").ToLowerInvariant()));
        if (type.EndsWith("/privateendpoints")) { Scalar("subnetId", EvidenceDiagram.Part(p, "subnet"), "id"); Strings("fqdns", EvidenceDiagram.Array(p, "customDnsConfigs").Select(d => Text(d, "fqdn"))); Strings("connectionStates", EvidenceDiagram.Array(p, "privateLinkServiceConnections").Select(c => Text(EvidenceDiagram.Part(EvidenceDiagram.Part(c, "properties"), "privateLinkServiceConnectionState"), "status"))); }
        if (type.EndsWith("/virtualnetworks")) Strings("dnsServers", EvidenceDiagram.Array(EvidenceDiagram.Part(p, "dhcpOptions"), "dnsServers").Select(v => v.GetString() ?? ""));
        if (type.EndsWith("/dnsresolvers")) Scalar("virtualNetworkId", EvidenceDiagram.Part(p, "virtualNetwork"), "id");
        if (type.EndsWith("/forwardingrules")) { Scalar("domainName", p, "domainName"); Scalar("forwardingRuleState", p, "forwardingRuleState"); Strings("targetDnsServers", EvidenceDiagram.Array(p, "targetDnsServers").Select(s => Text(s, "ipAddress") + ":" + EvidenceDiagram.Part(s, "port").ToString())); }
        return new(Text(row, "id").ToLowerInvariant(), type, Text(row, "name").Split('/').Last(), parent?.ToLowerInvariant(), values.ToDictionary());
    }
}
