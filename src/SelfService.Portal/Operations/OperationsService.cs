using System.Text.Json;
using SelfService.Operations;

namespace SelfService.Portal;

public record OperationsCollectRequest(string[] SubscriptionIds);
public record OperationsAssessRequest(string Workflow, string EvidenceId, string? ReferenceId, DesiredEdit[] Edits);
public record OperationsDraftRequest(string AssessmentId, string Operation, string[] ResourceIds, string? DestinationId, string Owner, string Rationale);
public record OperationsReviewContext(OperationsAssessment Assessment, OperationsEvidence Evidence);
public record OperationsScopeInventory(string TenantId, AzureScopeItem[] Subscriptions, CollectionCoverage Coverage, string[] Exclusions);
public sealed class OperationsSession
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Dictionary<string,OperationsEvidence> Evidence { get; } = new();
    public Dictionary<string,OperationsAssessment> Assessments { get; } = new();
    public Dictionary<string,OperationsProposal> Proposals { get; } = new();
    public void Clear() { lock (Evidence) { Evidence.Clear(); Assessments.Clear(); Proposals.Clear(); } }
}
public sealed class OperationsService(PrivateDnsReader reader, ITokenProvider identity, PortalOptions options)
{
    static void Authorized(BrowserSession s) { if (!s.Connected.ContainsKey("azure")) throw new PortalException("Connect Azure before collecting or reviewing operations evidence.", 401); }
    public static OperationsEvidence Evidence(BrowserSession s, string id, bool fresh = true)
    {
        Authorized(s);
        lock (s.Operations.Evidence)
        {
            if (!s.Operations.Evidence.TryGetValue(id, out var value)) throw new PortalException("Evidence is unavailable or belongs to another browser.", 404);
            if (fresh) OperationsJson.Fresh(value, DateTimeOffset.UtcNow); else OperationsJson.Verify(value);
            return value;
        }
    }
    public object List(BrowserSession s)
    {
        lock(s.Operations.Evidence) return s.Operations.Evidence.Values.Select(e => new { e.Id, e.CompletedUtc, e.Subscriptions, e.Digest, resources = e.Resources.Length,
            status = e.Coverage.All(OperationsJson.Complete) ? "Complete-visible" : "Partial", authority = "Observed reference; not accepted deployment baseline" }).ToArray();
    }
    public async Task<OperationsScopeInventory> Scopes(BrowserSession s, CancellationToken ct)
    {
        Authorized(s);
        var epoch = Interlocked.Read(ref s.NetworkEpoch);
        if (!Guid.TryParse(s.AzureTenantId, out var tenant)) throw new PortalException("Reconnect Azure to establish the authenticated tenant returned by Microsoft sign-in.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = await identity.Token(s, "azure");
        var result = await reader.List(token, "/subscriptions?api-version=2022-12-01", "/tenants/" + tenant, "Visible subscriptions", timeout.Token);
        var rows = new List<AzureScopeItem>(); var exclusions = new List<string>();
        foreach (var item in result.Values)
        {
            var id = EvidenceDiagram.Text(item, "subscriptionId");
            if (!Guid.TryParse(id, out var subscription) || !string.Equals(EvidenceDiagram.Text(item, "tenantId"), tenant.ToString(), StringComparison.OrdinalIgnoreCase) || EvidenceDiagram.Text(item, "state") != "Enabled")
            { exclusions.Add("A returned subscription was excluded because its tenant, enabled state or identifier did not match the authenticated scope."); continue; }
            rows.Add(new(subscription.ToString(), EvidenceDiagram.Text(item, "displayName") ?? subscription.ToString()));
        }
        if (epoch != Interlocked.Read(ref s.NetworkEpoch) || s.AzureTenantId != tenant.ToString()) throw new PortalException("Azure connection changed during scope discovery.",409);
        Authorized(s);
        return new(tenant.ToString(), rows.DistinctBy(r => r.Id).OrderBy(r => r.Name).ToArray(), result.Coverage, exclusions.Distinct().ToArray());
    }
    async Task Save<T>(string id, string name, T value, CancellationToken ct)
    {
        var folder = Path.Combine(options.RepositoryRoot, "artifacts/portal-operations", id); Directory.CreateDirectory(folder);
        await using var stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, OperationsJson.Options, ct);
    }
    public async Task<OperationsEvidence> Collect(BrowserSession s, OperationsCollectRequest request, CancellationToken ct)
    {
        Authorized(s);
        var epoch = Interlocked.Read(ref s.NetworkEpoch);
        if (request.SubscriptionIds is not { Length: > 0 and <= 8 } || request.SubscriptionIds.Any(id => !Guid.TryParse(id, out _))) throw new PortalException("Choose 1–8 subscriptions returned by Azure scope discovery.");
        var subscriptions = request.SubscriptionIds.Select(id => Guid.Parse(id).ToString()).Distinct().Order().ToArray();
        var scopes = await Scopes(s, ct);
        if (!OperationsJson.Complete(scopes.Coverage)) throw new PortalException("Subscription enumeration is incomplete. Retry before collecting resource evidence.",409);
        if (subscriptions.Any(id => !scopes.Subscriptions.Any(t => t.Id == id))) throw new PortalException("Subscription is outside the enabled scopes visible in this authenticated tenant.", 403);
        if (!await s.Operations.Gate.WaitAsync(0, ct)) throw new PortalException("An operations collection is already running.", 409);
        try
        {
            lock (s.Operations.Evidence) if (s.Operations.Evidence.Count >= 20) throw new PortalException("This session holds 20 observations. Export needed evidence and start a new session.", 409);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var token = await identity.Token(s, "azure");
            var evidence = await reader.Collect(token, scopes.TenantId, subscriptions, timeout.Token);
            if (System.Text.Encoding.UTF8.GetByteCount(OperationsJson.Write(evidence)) > 16 * 1024 * 1024) throw new PortalException("Evidence exceeds the 16 MiB budget. Select fewer subscriptions.", 413);
            Authorized(s); if (epoch != Interlocked.Read(ref s.NetworkEpoch)) throw new PortalException("Azure connection changed during collection.", 409);
            await Save(evidence.Id, "evidence.json", evidence, timeout.Token);
            lock (s.Operations.Evidence) { if (epoch != Interlocked.Read(ref s.NetworkEpoch)) throw new PortalException("Azure connection changed during collection.",409); s.Operations.Evidence.Add(evidence.Id, evidence); }
            return evidence;
        }
        catch (OperationCanceledException) { throw new PortalException("Operations collection cancelled or timed out. No successful snapshot was saved to this session.",408); }
        finally { s.Operations.Gate.Release(); }
    }
    public async Task<OperationsAssessment> Assess(BrowserSession s, OperationsAssessRequest request, CancellationToken ct)
    {
        var e = Evidence(s, request.EvidenceId); var baseline = request.ReferenceId is null ? null : Evidence(s, request.ReferenceId, false);
        if (request.Edits is null) throw new PortalException("Provide an edits array.");
        var result = OperationsRules.Assess(request.Workflow, e, baseline, request.Edits);
        lock (s.Operations.Evidence) { if (s.Operations.Assessments.Count >= 100) throw new PortalException("Assessment limit reached.", 409); s.Operations.Assessments.Add(result.Id, result); }
        await Save(result.Id, "assessment.json", new { assessment = result, desiredEdits = request.Edits }, ct); return result;
    }
    public static OperationsReviewContext ReviewContext(BrowserSession s, string id, string workflow)
    {
        lock (s.Operations.Evidence)
        {
            if (!s.Operations.Assessments.TryGetValue(id, out var a) || a.Workflow != workflow) throw new PortalException("Select a matching browser-owned assessment.", 404);
            var e = s.Operations.Evidence.Values.SingleOrDefault(e => e.Digest == a.EvidenceDigest) ?? throw new PortalException("Assessment evidence is unavailable.", 404);
            return new(a, Evidence(s, e.Id));
        }
    }
    public async Task<object> Draft(BrowserSession s, OperationsDraftRequest request, CancellationToken ct)
    {
        OperationsAssessment a;
        lock(s.Operations.Evidence) a = s.Operations.Assessments.GetValueOrDefault(request.AssessmentId) ?? throw new PortalException("Assessment unavailable.", 404);
        var context = ReviewContext(s, a.Id, a.Workflow);
        var proposal = OperationsRules.Propose(context.Evidence, a, request.Operation, request.ResourceIds, request.DestinationId, request.Owner, request.Rationale);
        lock(s.Operations.Evidence) { if(s.Operations.Proposals.Count >= 100) throw new PortalException("Proposal limit reached.",409); s.Operations.Proposals.Add(proposal.Id, proposal); }
        await Save(proposal.Id, "proposal.json", proposal, ct); return proposal;
    }
    public static OperationsPreview Preview(BrowserSession s, string id)
    {
        lock(s.Operations.Evidence)
        {
            var p = s.Operations.Proposals.GetValueOrDefault(id) ?? throw new PortalException("Proposal unavailable.",404);
            var a = s.Operations.Assessments.Values.SingleOrDefault(a => a.Digest == p.AssessmentDigest) ?? throw new PortalException("Assessment unavailable.",404);
            ReviewContext(s, a.Id, a.Workflow); return OperationsRules.Preview(p, a, DateTimeOffset.UtcNow);
        }
    }
}
