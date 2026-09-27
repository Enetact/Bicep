using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record NetworkScopeRequest(string Mode, string[]? SubscriptionIds = null, string? ManagementGroupId = null);
public record AzureScopeItem(string Id, string Name);
public record ScopeInventory(string TenantId, string Status, AzureScopeItem[] Subscriptions, AzureScopeItem[] ManagementGroups, string[] Issues);
public record ScopeCollection(string Status, JsonElement[] Values, string? Issue);

// Read-only scope resolution deliberately stays separate from the deployment catalog.
public sealed class NetworkDiscovery(HttpClient http, ITokenProvider identity, PortalOptions options, Catalog catalog)
{
    const string Arm = "https://management.azure.com";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static bool Success(string status) => status is "Succeeded" or "SucceededEmpty";
    async Task<ScopeCollection> List(string token, string path, CancellationToken ct)
    {
        var first = new Uri(Arm + path); Uri? next = first;
        var visited = new HashSet<string>(); var values = new List<JsonElement>();
        ScopeCollection Fail(string issue) => new(values.Count == 0 ? "Failed" : "Partial", values.ToArray(), issue);
        try
        {
            for (var page = 0; next is not null; page++)
            {
                if (page >= 20 || values.Count >= 10000) return Fail("Scope enumeration limit reached; coverage unknown.");
                if (next.Scheme != "https" || next.Host != "management.azure.com" || next.Port != 443 || next.UserInfo != "" || next.Fragment != "" ||
                    !next.AbsolutePath.Equals(first.AbsolutePath, StringComparison.OrdinalIgnoreCase) || !visited.Add(next.AbsoluteUri)) return Fail("Unsafe or repeated scope continuation rejected.");
                using var req = new HttpRequestMessage(HttpMethod.Get, next); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return Fail($"Scope enumeration HTTP {(int)response.StatusCode}; not an empty scope.");
                using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > 4 * 1024 * 1024) return Fail("Scope response too large."); output.Write(buffer, 0, count); }
                var root = JsonSerializer.Deserialize<JsonElement>(output.ToArray());
                if (EvidenceDiagram.Part(root, "value").ValueKind != JsonValueKind.Array) return Fail("Unexpected scope response.");
                foreach (var value in EvidenceDiagram.Array(root, "value")) { if (values.Count == 10000) return Fail("Scope item limit reached."); values.Add(value); }
                var link = EvidenceDiagram.Part(root, "nextLink");
                if (link.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || link.ValueKind == JsonValueKind.String && link.GetString() == "") next = null;
                else if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out next)) return Fail("Invalid scope continuation.");
            }
            return new(values.Count == 0 ? "SucceededEmpty" : "Succeeded", values.ToArray(), null);
        }
        catch (OperationCanceledException) { return Fail("Scope enumeration cancelled or timed out."); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException) { return Fail("Scope enumeration unavailable; coverage unknown."); }
    }
    public async Task<ScopeInventory> Scopes(BrowserSession session, CancellationToken ct)
    {
        if (!options.AllowExpandedNetworkDiscovery) throw new PortalException("Expanded discovery is disabled by local platform configuration.", 403);
        if (!Guid.TryParse(options.TenantId, out _)) throw new PortalException("Configure the portal tenant before expanded discovery.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = await identity.Token(session, "azure");
        var subscriptions = await List(token, "/subscriptions?api-version=2022-12-01", timeout.Token);
        var groups = await List(token, "/providers/Microsoft.Management/managementGroups?api-version=2020-05-01", timeout.Token);
        var issues = new List<string>();
        if (!Success(subscriptions.Status)) issues.Add(subscriptions.Issue!);
        if (!Success(groups.Status)) issues.Add("Management group listing: " + groups.Issue);
        var permitted = new List<AzureScopeItem>();
        foreach (var row in subscriptions.Values)
        {
            var tenant = EvidenceDiagram.Text(row, "tenantId"); var id = EvidenceDiagram.Text(row, "subscriptionId");
            if (!string.Equals(tenant, options.TenantId, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(id, out var guid) || EvidenceDiagram.Text(row, "state") != "Enabled")
            { issues.Add("A subscription was excluded: tenant membership, identifier or enabled state could not be qualified."); continue; }
            permitted.Add(new(guid.ToString(), EvidenceDiagram.Text(row, "displayName") ?? guid.ToString()));
        }
        var management = new List<AzureScopeItem>();
        foreach (var row in groups.Values)
        {
            var name = EvidenceDiagram.Text(row, "name"); var p = EvidenceDiagram.Part(row, "properties");
            if (name is null || !Regex.IsMatch(name, "^[a-zA-Z0-9_.()-]{1,90}$") || !string.Equals(EvidenceDiagram.Text(p, "tenantId"), options.TenantId, StringComparison.OrdinalIgnoreCase))
            { issues.Add("A management group was excluded because its ID or tenant could not be verified."); continue; }
            management.Add(new(name, EvidenceDiagram.Text(p, "displayName") ?? name));
        }
        return new(options.TenantId, Success(subscriptions.Status) ? "VisibleScopes" : "Partial", permitted.DistinctBy(s => s.Id).ToArray(), management.DistinctBy(g => g.Id).ToArray(), issues.Distinct().ToArray());
    }
    public async Task<object> Discover(BrowserSession session, NetworkScopeRequest request, CancellationToken ct)
    {
        var epoch = Interlocked.Read(ref session.NetworkEpoch);
        if (request.Mode is not ("registered" or "selected" or "tenant" or "managementGroup")) throw new PortalException("Select a supported discovery scope.");
        if (request.SubscriptionIds?.Length > 64) throw new PortalException("Select at most 64 subscriptions.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var selected = new List<AzureScopeItem>(); var issues = new List<string>();
        string membership = "Registered scope only; tenant-wide coverage unknown";
        if (request.Mode == "registered")
        {
            if (request.SubscriptionIds?.Length != 1) throw new PortalException("Select one registered subscription.");
            var target = catalog.Products.SelectMany(p => p.Targets).FirstOrDefault(t => t.SubscriptionId.Equals(request.SubscriptionIds[0], StringComparison.OrdinalIgnoreCase)) ?? throw new PortalException("Unregistered scope.", 403);
            selected.Add(new(target.SubscriptionId, target.Subscription));
        }
        else
        {
            if (!options.AllowExpandedNetworkDiscovery) throw new PortalException("Expanded discovery is disabled by local platform configuration.", 403);
            var scopes = await Scopes(session, timeout.Token); issues.AddRange(scopes.Issues);
            if (scopes.Status == "Partial") throw new PortalException("Subscription enumeration is incomplete. Retry scope discovery before scanning.", 409);
            membership = "Caller-visible subscriptions in configured tenant; undisclosed subscriptions remain unknown. Resource-level visibility is not complete tenant coverage.";
            if (request.Mode == "tenant") selected.AddRange(scopes.Subscriptions);
            if (request.Mode == "selected")
            {
                if (request.SubscriptionIds is not { Length: > 0 }) throw new PortalException("Select subscriptions.");
                foreach (var id in request.SubscriptionIds.Distinct(StringComparer.OrdinalIgnoreCase)) selected.Add(scopes.Subscriptions.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new PortalException("Subscription is outside the visible configured tenant.", 403));
            }
            if (request.Mode == "managementGroup")
            {
                var group = scopes.ManagementGroups.FirstOrDefault(g => g.Id == request.ManagementGroupId) ?? throw new PortalException("Select a visible management group in this tenant.", 403);
                var token = await identity.Token(session, "azure");
                var descendants = await List(token, $"/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(group.Id)}/descendants?api-version=2020-05-01", timeout.Token);
                if (!Success(descendants.Status)) throw new PortalException("Management-group hierarchy is incomplete: " + descendants.Issue, 409);
                foreach (var item in descendants.Values)
                {
                    var id = EvidenceDiagram.Text(item, "id") ?? "";
                    if (!id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Guid.TryParse(id[15..], out var parsed)) { issues.Add("Invalid subscription descendant; hierarchy coverage incomplete."); continue; }
                    var sub = scopes.Subscriptions.FirstOrDefault(s => s.Id == parsed.ToString());
                    if (sub is null) issues.Add($"Descendant {parsed} unavailable or excluded by tenant/state policy; not scanned."); else selected.Add(sub);
                }
                membership += " Management-group descendants were reconciled with visible enabled subscriptions.";
            }
        }
        selected = selected.DistinctBy(s => s.Id).OrderBy(s => s.Id).ToList();
        if (selected.Count > 64) throw new PortalException("Scope exceeds the 64-subscription scan limit. Select smaller subscription batches; no truncation was performed.", 413);
        var reader = new AzureDiscovery(http, identity, options, catalog);
        var reports = new List<JsonElement>();
        // Sequential bounded fanout avoids flooding ARM; every selected subscription gets a status.
        long evidenceBytes = 0;
        foreach (var sub in selected)
        {
            var report = await reader.CollectAuthorizedSubscription(session, sub.Id, timeout.Token);
            var json = JsonSerializer.SerializeToElement(report, Json); evidenceBytes += System.Text.Encoding.UTF8.GetByteCount(json.GetRawText());
            if (evidenceBytes > 32 * 1024 * 1024) { issues.Add("32 MiB scan limit reached; remaining selected subscriptions not collected. Select smaller batches."); break; }
            reports.Add(json);
        }
        var idReport = Guid.NewGuid().ToString("N");
        var provisional = JsonSerializer.SerializeToElement(new { reports }, Json);
        var assessment = NetworkAssessment.Evaluate(provisional);
        var result = new { schemaVersion = 2, kind = "portal-network-discovery", id = idReport, generatedUtc = DateTimeOffset.UtcNow,
            tenantId = options.TenantId, request, resolvedSubscriptions = selected, reports, assessment,
            status = issues.Count == 0 && reports.All(r => EvidenceDiagram.Text(r, "status") == "Collected") ? (reports.Count == 0 ? "CollectedEmpty" : "Collected") : "Partial",
            membershipCoverage = membership, issues, readOnly = true, allocationAuthorized = false, deploymentAuthorized = false };
        var directory = Path.Combine(options.RepositoryRoot, "artifacts/portal-network", idReport); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(result, Json), ct);
        if (epoch != Interlocked.Read(ref session.NetworkEpoch)) throw new PortalException("Identity disconnected during collection. Reconnect and rediscover before review.", 409);
        session.NetworkReports.Clear();
        session.NetworkReports[idReport] = JsonSerializer.SerializeToElement(result, Json);
        return result;
    }
}
