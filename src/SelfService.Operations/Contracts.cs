using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Operations;

public sealed class OperationsException(string message) : Exception(message);
public record CollectionCoverage(string Scope, string Kind, string Status, int Pages, int Observed, string? Issue);
public record ObservedResource(string Id, string Type, string Name, string? ParentId, Dictionary<string,string> Properties);
public record OperationsEvidence(string Schema, string Id, string Tenant, string[] Subscriptions, DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc, string Collector, CollectionCoverage[] Coverage, ObservedResource[] Resources, string Digest = "");
public record OperationFinding(string Id, string Rule, string State, string Severity, string ResourceId, string Message, string[] EvidenceRefs);
public record DesiredEdit(string ResourceId, string Property, string Value);
public record PropertyComparison(string ResourceId, string Property, string State, string? Baseline, string? Observed, string? Desired);
public record OperationsAssessment(string Schema, string Id, string Workflow, string EvidenceDigest, string? ReferenceDigest,
    string DesiredDigest, string BaselineAuthority, DesiredEdit[] DesiredEdits, OperationFinding[] Findings, PropertyComparison[] Comparisons, string Digest = "");
public record OperationRecommendation(string Id, string ResourceId, string Operation, string[] EvidenceRefs, string Rationale);
public record OperationsAdvice(string Schema, string Workflow, string AssessmentDigest, string Summary, OperationRecommendation[] Recommendations, bool AdvisoryOnly = true);
public record OperationsProposal(string Schema, string Id, string AssessmentDigest, string Operation, string[] ResourceIds,
    string? DestinationId, string Owner, string Rationale, string[] Blockers, DateTimeOffset ExpiresUtc, string Digest = "");
public record OperationsPreview(string Kind, string ProposalDigest, string AssessmentDigest, DateTimeOffset ExpiresUtc,
    string[] Operations, string[] Blockers, bool ExecutionAvailable = false);

public static class OperationsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n", UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new OperationsException("Empty operations contract.");
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Write(value)))).ToLowerInvariant();
    public static bool Complete(CollectionCoverage c) => c.Status is "Succeeded" or "SucceededEmpty";
    public static OperationsEvidence Seal(OperationsEvidence evidence) => evidence with { Digest = Hash(evidence with { Digest = "" }) };
    public static void Verify(OperationsEvidence evidence)
    {
        if (evidence.Schema != "platform.operations-evidence/v1" || evidence.Resources is null || evidence.Coverage is null || evidence.Subscriptions is null || evidence.Digest != Hash(evidence with { Digest = "" }) ||
            evidence.Resources.Any(r => r is null || r.Id is null || r.Type is null || r.Name is null || r.Properties is null) ||
            evidence.Resources.Length > 5000 || evidence.Resources.Select(r => r.Id.ToLowerInvariant()).Distinct().Count() != evidence.Resources.Length ||
            evidence.Subscriptions.Length is < 1 or > 8 || evidence.Subscriptions.Any(s => !Guid.TryParse(s, out _)) ||
            evidence.Resources.Any(r => !evidence.Subscriptions.Any(s => r.Id.StartsWith($"/subscriptions/{s}/", StringComparison.OrdinalIgnoreCase))))
            throw new OperationsException("Evidence schema, scope, duplicate resources or digest rejected.");
    }
    public static void Fresh(OperationsEvidence evidence, DateTimeOffset now)
    {
        Verify(evidence);
        if (evidence.CompletedUtc < now.AddMinutes(-15) || evidence.CompletedUtc > now.AddMinutes(1)) throw new OperationsException("Evidence is stale. Collect a new observation before review or Preview.");
    }
}

public static class OperationsRegistry
{
    public static readonly string[] AnalysisWorkflows = ["private-dns", "drift"];
    public static object Catalog => new {
        schema = "platform.operations-catalog/v1",
        implemented = AnalysisWorkflows.Select(id => new { id, version = "1", collector = "private-dns-arm/v1", scope = "1–8 subscriptions returned by Azure in the authenticated tenant",
            operations = new[] { "Collect", "Assess", "Review", "Draft", "ProposalPreview", "Export" }, execute = false }),
        planned = new[] { "identity-access", "private-connectivity", "pipeline-analysis", "data-release", "aks-maintenance" },
        retention = "Browser-owned local evidence; 20 observations per session, 15-minute review freshness. Saved files persist locally; no platform baseline store is qualified."
    };
}
