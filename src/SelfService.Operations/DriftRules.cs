namespace SelfService.Operations;

public static class DriftRules
{
    // This first profile owns only explicit DNS values/TTL and link registration settings.
    // ETags are concurrency evidence, never desired state; omitted properties remain unsupported.
    public static readonly string[] Properties = ["values", "ttl", "registrationEnabled"];
    public static PropertyComparison[] Compare(OperationsEvidence? baseline, OperationsEvidence observed, DesiredEdit[] edits)
    {
        OperationsJson.Verify(observed); if (baseline is not null) OperationsJson.Verify(baseline);
        if (baseline is not null && (!baseline.Subscriptions.Order().SequenceEqual(observed.Subscriptions.Order()) || baseline.Tenant != observed.Tenant))
            throw new OperationsException("Reference and observation scopes differ.");
        if (edits is null || edits.Length > 100 || edits.Any(e => e is null || e.ResourceId is null || e.Property is null || e.Value is null) || edits.Select(e => (e.ResourceId.ToLowerInvariant(), e.Property)).Distinct().Count() != edits.Length) throw new OperationsException("Desired edits are malformed, exceed the limit or repeat a property.");
        foreach (var edit in edits)
        {
            var resource = observed.Resources.SingleOrDefault(r => r.Id.Equals(edit.ResourceId, StringComparison.OrdinalIgnoreCase)) ?? throw new OperationsException("Desired target was not observed.");
            if (!Properties.Contains(edit.Property) || !resource.Properties.ContainsKey(edit.Property) || edit.Value.Length is < 1 or > 4096) throw new OperationsException("Unsupported desired property or value.");
            if (edit.Property == "ttl" && (!int.TryParse(edit.Value, out var ttl) || ttl < 1 || ttl > int.MaxValue)) throw new OperationsException("TTL must be a positive integer.");
            if (edit.Property == "registrationEnabled" && edit.Value is not ("true" or "false")) throw new OperationsException("Registration must be true or false.");
            if (edit.Property == "values") throw new OperationsException("Record value edits need a qualified record-type editor. This profile supports TTL and registration proposals only.");
        }
        var rows = new List<PropertyComparison>();
        foreach (var id in observed.Resources.Select(r => r.Id).Concat(baseline?.Resources.Select(r => r.Id) ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = baseline?.Resources.SingleOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var now = observed.Resources.SingleOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (before is null && now is null) continue;
            if (now is null) { rows.Add(new(id, "resource", "Unknown", "Observed in reference", null, null)); continue; }
            if (before is null && baseline is not null) rows.Add(new(id, "resource", "UnmanagedCandidate", null, "Observed", null));
            foreach (var key in Properties.Where(p => now.Properties.ContainsKey(p) || before?.Properties.ContainsKey(p) == true))
            {
                var b = before?.Properties.GetValueOrDefault(key); var o = now.Properties.GetValueOrDefault(key);
                var edit = edits.SingleOrDefault(e => e.ResourceId.Equals(id, StringComparison.OrdinalIgnoreCase) && e.Property == key);
                // An earlier observation is not implicitly the user's current desired configuration.
                var d = edit?.Value;
                var state = b is null || o is null || d is null ? "Unknown" : o == b && d == b ? "InSync" : o == b ? "DesiredChange" : d == b ? "ObservedDeviation" : o == d ? "DesiredChange" : "Conflict";
                // A missing accepted baseline never becomes historical drift proof.
                if (baseline is null && edit is not null && o is not null) state = o == d ? "InSync" : "DesiredChange";
                rows.Add(new(id, key, state, b, o, d));
            }
        }
        return rows.ToArray();
    }
}
