namespace SelfService.Operations;

public static class PrivateDnsRules
{
    public const string ZoneType = "microsoft.network/privatednszones";
    public static bool Zone(ObservedResource r) => r.Type.Equals(ZoneType, StringComparison.OrdinalIgnoreCase);
    public static bool Record(ObservedResource r) => r.Properties.ContainsKey("recordType");
    public static string Value(ObservedResource r, string key) => r.Properties.GetValueOrDefault(key, "Unknown");
    public static OperationFinding[] Analyze(OperationsEvidence evidence)
    {
        OperationsJson.Verify(evidence);
        var findings = new List<OperationFinding>();
        void Add(string rule, string state, string severity, string resource, string message, params string[] refs) =>
            findings.Add(new(OperationsJson.Hash(new { rule, resource, message })[..16], rule, state, severity, resource, message, refs));
        foreach (var c in evidence.Coverage.Where(c => !OperationsJson.Complete(c)))
            Add("DNS-005", "Unknown", "Warning", c.Scope, $"{c.Kind}: {c.Status}. {c.Issue} Cannot establish absence or retirement safety.", c.Scope);
        var zones = evidence.Resources.Where(Zone).ToArray();
        foreach (var group in zones.GroupBy(z => z.Name.ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            foreach (var zone in group) Add("DNS-001", "Known", "Information", zone.Id, "Same-name zones are consolidation candidates; isolation and ownership must be reviewed.", group.Select(z => z.Id).ToArray());
            var zoneIds = group.Select(z => z.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var records = evidence.Resources.Where(r => Record(r) && zoneIds.Contains(r.ParentId ?? ""));
            foreach (var duplicates in records.GroupBy(r => (r.Name.ToLowerInvariant(), Value(r, "recordType"))))
            {
                if (duplicates.Select(r => Value(r, "values") + "|" + Value(r, "ttl")).Distinct().Count() <= 1) continue;
                foreach (var record in duplicates) Add("DNS-002", "Conflict", "Warning", record.Id, "Same-name/type records differ in value or TTL. Explicit conflict resolution is required.", duplicates.Select(r => r.Id).ToArray());
            }
        }
        foreach (var record in evidence.Resources.Where(Record))
        {
            var ownership = Value(record, "ownership");
            if (ownership != "ManualVerified") Add("DNS-005", "Unknown", "Information", record.Id, $"Record ownership: {ownership}. Preserve it; discovery does not authorize copying, editing or retirement.", record.Id);
        }
        foreach (var link in evidence.Resources.Where(r => r.Type.EndsWith("/virtualnetworklinks", StringComparison.OrdinalIgnoreCase)))
        {
            if (Value(link, "linkState") != "Completed") Add("DNS-004", "Unknown", "Warning", link.Id, "VNet link completion is not established.", link.Id);
            if (Value(link, "registrationEnabled") == "true") Add("DNS-004", "Known", "Information", link.Id, "Auto-registration requires preserving managed record lifecycle during a migration.", link.Id);
        }
        foreach (var endpoint in evidence.Resources.Where(r => r.Type.Equals("microsoft.network/privateendpoints", StringComparison.OrdinalIgnoreCase)))
        {
            var children = evidence.Coverage.Where(c => c.Scope.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase) && c.Kind == "Zone groups").ToArray();
            if (children.Length == 0 || children.Any(c => !OperationsJson.Complete(c)))
                Add("DNS-003", "Unknown", "Warning", endpoint.Id, "Endpoint zone-group evidence is incomplete.", endpoint.Id);
            else if (children.All(c => c.Observed == 0))
                Add("DNS-003", "Known", "Information", endpoint.Id, "No zone group observed. An alternative DNS configuration may exist; do not infer broken connectivity.", endpoint.Id);
        }
        foreach (var zone in zones) Add("DNS-004", "Unknown", "Information", zone.Id, "Consumer completeness, effective forwarding and source-network DNS/application probes remain unverified; migration is blocked.", zone.Id);
        return findings.ToArray();
    }
}
