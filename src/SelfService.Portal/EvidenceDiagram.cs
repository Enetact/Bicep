using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record EvidenceNode(string Id, string ResourceId, string Label, string Type, string Status, int Level, string Detail);
public record EvidenceEdge(string From, string To, string Label);
public record EvidenceGraph(EvidenceNode[] Nodes, EvidenceEdge[] Edges, string Coverage);
public record DiagramResult(string Status, string Issue, string? Source, EvidenceNode[] Nodes, EvidenceEdge[] Edges, string Title, string Subtitle, string Note, string[] Coverage);

// A deliberately small Mermaid language, not a general Markdown/HTML interpreter.
// Only server-evidenced IDs and relationships can enter the image renderer.
public static class EvidenceDiagram
{
    static readonly Regex NodeLine = new("^([A-Z][A-Z0-9]{0,15})\\[\"([^\"<>`\\\\]{1,180})\"\\]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex EdgeLine = new("^([A-Z][A-Z0-9]{0,15}) -->\\|([a-zA-Z0-9 ;()./-]{1,100})\\| ([A-Z][A-Z0-9]{0,15})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static string? Text(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static JsonElement Part(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) ? v : default;
    public static IEnumerable<JsonElement> Array(JsonElement e, string p) => Part(e, p).ValueKind == JsonValueKind.Array ? Part(e, p).EnumerateArray() : [];
    public static EvidenceGraph Build(JsonElement evidence)
    {
        var resources = new Dictionary<string, (string Label, string Type, bool Observed)>(StringComparer.OrdinalIgnoreCase);
        var links = new HashSet<(string From, string To, string Label)>();
        string? Add(string? id, string? name = null, string? type = null, bool observed = false)
        {
            if (id is null || id.Length > 2048 || !id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) return null;
            id = id.ToLowerInvariant();
            if (!resources.TryGetValue(id, out var prior) || !prior.Observed)
                resources[id] = (name ?? id.Split('/').Last(), type ?? "Referenced resource", observed);
            return id;
        }
        void Link(string? from, string? to, string label)
        {
            var a = Add(from); var b = Add(to); if (a is not null && b is not null) links.Add((a, b, label));
        }
        var reports = Array(evidence, "reports").ToArray();
        if (reports.Length == 0) reports = [evidence];
        foreach (var report in reports)
        foreach (var collection in Array(report, "collections"))
        foreach (var r in Array(collection, "resources"))
        {
            var id = Add(Text(r, "id"), Text(r, "name"), Text(r, "type") ?? Text(collection, "name"), true);
            foreach (var s in Array(r, "subnets"))
            {
                var sid = Add(Text(s, "id"), Text(s, "name"), "Subnet", true);
                Link(id, sid, "contains subnet"); Link(sid, Text(s, "nsgId"), "configured NSG"); Link(sid, Text(s, "routeTableId"), "configured route table");
            }
            foreach (var p in Array(r, "peerings")) Link(id, Text(p, "remoteVnetId"), "configured peering");
            Link(id, Text(r, "subnetId"), "configured subnet");
            Link(id, Text(r, "virtualNetworkId"), "configured DNS link");
            foreach (var c in Array(r, "connections")) Link(id, Text(Part(c, "configured"), "privateLinkServiceId"), "configured private link");
        }
        if (resources.Count > 200 || links.Count > 500) throw new PortalException("Diagram evidence exceeds 200 nodes or 500 relationships. Select a smaller scope; no silent truncation.", 413);
        var aliases = resources.Keys.Order(StringComparer.Ordinal).Select((id, i) => (id, alias: $"R{i + 1:D3}")).ToDictionary(x => x.id, x => x.alias);
        return new(resources.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new EvidenceNode(aliases[r.Key], r.Key, r.Value.Label,
            r.Value.Type, r.Value.Observed ? "Observed configuration" : "Referenced only", r.Value.Type == "Subnet" ? 2 : 1,
            r.Value.Observed ? "Configured inventory; runtime connectivity unverified" : "Reference reported; target visibility unverified")).ToArray(),
            links.OrderBy(e => e.From).ThenBy(e => e.To).ThenBy(e => e.Label).Select(e => new EvidenceEdge(aliases[e.From], aliases[e.To], e.Label)).ToArray(),
            $"Discovery status: {Text(evidence, "status") ?? "Unspecified"}. Only configured relationships in the collected snapshot. Partial/denied collections remain unknown. Model selection is interpretation, not complete topology or reachability proof.");
    }
    public const string Instructions = "For visualize or network return exactly one mermaid fenced block, graph TB or graph LR. " +
        "Use ONLY aliases and relationships in diagramGraph from platform_evidence. Declare each node on its own line: R001[\"short plain label\"]. " +
        "Declare edges on separate lines: R001 -->|configured subnet| R002. Copy edge labels exactly. Declare nodes before edges. " +
        "No subgraphs, styling, comments, HTML, directives, links, escaped labels, chained edges or other syntax. Unknown relationships belong only in prose, explicitly unknown. " +
        "If diagramGraph has zero nodes explain the empty/partial coverage without a diagram.";
    public static DiagramResult Validate(string review, EvidenceGraph graph)
    {
        DiagramResult Rejected(string why) => new("Rejected", why, null, [], [], "Agent interpretation", "Diagram rejected", graph.Coverage, [why]);
        if (review.Length > 128 * 1024) return Rejected("Review exceeds diagram validation size limit.");
        var blocks = Regex.Matches(review, "```mermaid[ \\t]*\\r?\\n([\\s\\S]*?)```", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (blocks.Count != 1) return graph.Nodes.Length == 0 && blocks.Count == 0
            ? new("Empty", "No resource nodes in this evidence; check coverage.", null, [], [], "Agent interpretation", "No diagram", graph.Coverage, [])
            : Rejected("Expected exactly one Mermaid block. The original review is retained as untrusted text.");
        var source = blocks[0].Groups[1].Value.Trim();
        if (source.Length > 64 * 1024 || source.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) return Rejected("Unsafe or oversized diagram.");
        var lines = source.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 2 || lines[0] is not ("graph TB" or "graph LR" or "flowchart TB" or "flowchart LR")) return Rejected("Use the supported TB/LR flowchart grammar.");
        var known = graph.Nodes.ToDictionary(n => n.Id); var declared = new HashSet<string>(); var edges = new List<EvidenceEdge>();
        foreach (var line in lines.Skip(1))
        {
            var n = NodeLine.Match(line);
            if (n.Success)
            {
                var id = n.Groups[1].Value;
                if (!known.ContainsKey(id) || !declared.Add(id)) return Rejected("Unknown or duplicate resource alias; diagram was not rendered.");
                // Model label is never used in the visual; canonical resource names come from evidence.
                continue;
            }
            var e = EdgeLine.Match(line);
            if (!e.Success) return Rejected("Unsupported Mermaid construct. Directives, links, HTML and arbitrary styles are not accepted.");
            var edge = new EvidenceEdge(e.Groups[1].Value, e.Groups[3].Value, e.Groups[2].Value);
            if (!declared.Contains(edge.From) || !declared.Contains(edge.To) || !graph.Edges.Contains(edge)) return Rejected("Relationship or resource reference is not supported by the collected evidence.");
            if (!edges.Contains(edge)) edges.Add(edge);
        }
        if (declared.Count == 0) return Rejected("No evidenced resources declared.");
        return new("Validated", "Syntax and resource/relationship references passed. This does not verify network reachability.", source,
            graph.Nodes.Where(n => declared.Contains(n.Id)).ToArray(), edges.ToArray(), "Agent interpretation", "Validated against observed configuration",
            graph.Coverage, [$"Agent included {declared.Count}/{graph.Nodes.Length} evidence nodes and {edges.Count}/{graph.Edges.Length} configured relationships. Labels use canonical evidence names."]);
    }
}
