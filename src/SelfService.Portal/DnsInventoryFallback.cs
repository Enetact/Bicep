using System.Text.Json;

namespace SelfService.Portal;

// ARM catalog metadata can recover zone identities, never provider properties or DNS health.
public static class DnsInventoryFallback
{
    public static JsonElement[] Zones(IEnumerable<JsonElement> rows, string subscription) => rows
        .Where(r => string.Equals(EvidenceDiagram.Text(r, "type"), "Microsoft.Network/privateDnsZones", StringComparison.OrdinalIgnoreCase) &&
                    PrivateDnsReader.ResourcePath(EvidenceDiagram.Text(r, "id") ?? "", subscription, "privateDnsZones"))
        .Select(r => JsonSerializer.SerializeToElement(new { id = EvidenceDiagram.Text(r, "id"), name = EvidenceDiagram.Text(r, "name"),
            type = "Microsoft.Network/privateDnsZones", location = EvidenceDiagram.Text(r, "location") }))
        .DistinctBy(r => EvidenceDiagram.Text(r, "id"), StringComparer.OrdinalIgnoreCase).ToArray();
    public const string Limitation = "ARM inventory fallback supplies zone identities only. The original DNS API failure is retained; properties, records, links, resolution and access remain unverified until separately collected.";
}
