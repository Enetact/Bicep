using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SelfService.Portal;

public record NetworkFinding(string Rule, string State, string ResourceId, string Summary, bool BlocksAutomaticPlacement);
public readonly record struct Ipv4Range(uint First, uint Last, int Prefix)
{
    public ulong Size => (ulong)Last - First + 1;
    public bool Contains(Ipv4Range other) => First <= other.First && Last >= other.Last;
    public bool Overlaps(Ipv4Range other) => First <= other.Last && other.First <= Last;
    public static bool TryParse(string? value, out Ipv4Range range)
    {
        range = default; var parts = value?.Split('/');
        if (parts?.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32 ||
            !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != parts[0]) return false;
        var b = ip.GetAddressBytes(); uint address = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        if ((address & mask) != address) return false;
        range = new(address, address | ~mask, prefix); return true;
    }
}
public static class NetworkAssessment
{
    static IEnumerable<string> Prefixes(JsonElement r, bool configured = false)
    {
        if (configured) r = EvidenceDiagram.Part(r, "configured");
        var one = EvidenceDiagram.Text(r, "addressPrefix"); if (one is not null) yield return one;
        foreach (var p in EvidenceDiagram.Array(r, "addressPrefixes")) if (p.ValueKind == JsonValueKind.String) yield return p.GetString()!;
    }
    public static object Evaluate(JsonElement evidence)
    {
        var reports = EvidenceDiagram.Array(evidence, "reports").ToArray(); if (reports.Length == 0) reports = [evidence];
        var findings = new List<NetworkFinding>();
        void Add(string rule, string state, string id, string summary, bool block = true) => findings.Add(new(rule, state, id, summary, block));
        var all = reports.SelectMany(r => EvidenceDiagram.Array(r, "collections")).ToArray();
        foreach (var c in all.Where(c => EvidenceDiagram.Text(c, "status") is not ("Succeeded" or "SucceededEmpty"))) Add("coverage", "Unknown", "scope", $"{EvidenceDiagram.Text(c, "name")}: collection incomplete.");
        var networks = all.Where(c => EvidenceDiagram.Text(c, "name") == "Virtual networks").SelectMany(c => EvidenceDiagram.Array(c, "resources")).ToArray();
        var links = all.Where(c => EvidenceDiagram.Text(c, "name") == "Private DNS links").SelectMany(c => EvidenceDiagram.Array(c, "resources")).ToArray();
        foreach (var vnet in networks)
        {
            var id = EvidenceDiagram.Text(vnet, "id") ?? "unknown";
            var parent = Prefixes(vnet).Select(p => Ipv4Range.TryParse(p, out var r) ? (Ipv4Range?)r : null).ToArray();
            if (parent.Length == 0 || parent.Any(p => p is null)) Add("address-family", "Unsupported", id, "Missing, noncanonical or IPv6 address space; IPv4 automatic placement cannot be qualified.");
            var subnets = EvidenceDiagram.Array(vnet, "subnets").ToArray();
            var ranges = new List<(string Id, Ipv4Range Range)>();
            foreach (var subnet in subnets)
            {
                var sid = EvidenceDiagram.Text(subnet, "id") ?? id;
                var prefixes = Prefixes(subnet, true).Distinct().ToArray();
                if (prefixes.Length == 0) Add("subnet-prefix", "Unknown", sid, "Subnet prefixes not reported.");
                foreach (var prefix in prefixes)
                {
                    if (!Ipv4Range.TryParse(prefix, out var range)) { Add("subnet-prefix", "Unsupported", sid, "IPv4 prefix invalid/noncanonical or IPv6 not qualified."); continue; }
                    var contained = parent.Any(p => p?.Contains(range) == true);
                    Add("containment", contained ? "Known" : "Conflict", sid, contained ? "Subnet is contained in reported VNet address space." : "Subnet is not contained in any reported IPv4 VNet prefix.", !contained);
                    var usable = range.Size > 5 ? range.Size - 5 : 0;
                    Add("capacity", "Unknown", sid, $"{prefix}: {range.Size} total addresses, {usable} after five Azure-reserved addresses. Actual occupancy, PaaS scale headroom and IPAM reservations are unknown; this is not free capacity.");
                    Add("web-host-size", range.Prefix <= 26 ? "Known" : "Conflict", sid, range.Prefix <= 26 ? "Meets the current /26 Web-host integration size baseline; suitability still requires delegation and capacity checks." : "Too small for this platform's /26 Web-host integration baseline; other subnet roles have different requirements.", range.Prefix > 26);
                    ranges.Add((sid, range));
                }
                var delegated = EvidenceDiagram.Array(subnet, "delegations").Select(d => EvidenceDiagram.Text(d, "serviceName")).Where(x => x is not null).ToArray();
                Add("delegation", "Known", sid, delegated.Length > 0 ? "Reported delegation: " + string.Join(", ", delegated) : "No delegation reported; Web integration requires Microsoft.Web/serverFarms.", false);
                Add("effective-routing", "Unknown", sid, "Configured NSG/route references do not prove effective rules, BGP, return routes, firewall policy or reachability.");
            }
            for (var i = 0; i < ranges.Count; i++) for (var j = i + 1; j < ranges.Count; j++)
                if (ranges[i].Range.Overlaps(ranges[j].Range)) Add("subnet-overlap", "Conflict", ranges[i].Id, "Overlaps reported subnet prefix: " + ranges[j].Id);
            var dns = EvidenceDiagram.Array(vnet, "dnsServers").Any();
            Add("dns-path", "Unknown", id, dns ? "Custom DNS configured; forwarder/resolver path requires a qualified probe." : "Azure-provided DNS configuration reported; service FQDN resolution still requires a probe.");
            var count = links.Count(l => string.Equals(EvidenceDiagram.Text(l, "virtualNetworkId"), id, StringComparison.OrdinalIgnoreCase));
            Add("dns-links", "Known", id, $"{count} visible private DNS links refer to this VNet. Zero does not prove no inaccessible or remote links exist.", false);
        }
        foreach (var c in all.Where(c => EvidenceDiagram.Text(c, "name") == "Route tables")) foreach (var r in EvidenceDiagram.Array(c, "resources"))
        foreach (var route in EvidenceDiagram.Array(r, "routes"))
        {
            var config = EvidenceDiagram.Part(route, "configured");
            if (EvidenceDiagram.Text(config, "nextHopType") == "VirtualAppliance") Add("nva-route", "Unknown", EvidenceDiagram.Text(r, "id") ?? "unknown", "Virtual appliance route configured; appliance availability and policy are not established by the route.");
        }
        foreach (var c in all.Where(c => EvidenceDiagram.Text(c, "name") == "Private endpoints")) foreach (var r in EvidenceDiagram.Array(c, "resources"))
        foreach (var connection in EvidenceDiagram.Array(r, "connections"))
        {
            var approved = EvidenceDiagram.Text(connection, "state") == "Approved";
            Add("endpoint-approval", approved ? "Known" : "Unknown", EvidenceDiagram.Text(r, "id") ?? "unknown", approved ? "Connection reports Approved; DNS, private data-plane access and public-access settings need separate verification." : "Endpoint connection is not reported Approved.", !approved);
        }
        Add("allocation-authority", "Unknown", "scope", "AVNM IPAM must reserve address space through the separately protected allocation pipeline. Inventory never reserves space.");
        return new { schemaVersion = 1, kind = "network-evidence-assessment", allocationAuthorized = false, deploymentAuthorized = false,
            automaticPlacement = "Blocked", findings, limits = "IPv4 configuration assessment only. No packet probes, free-IP inference, external-prefix completeness or ownership authorization." };
    }
}
