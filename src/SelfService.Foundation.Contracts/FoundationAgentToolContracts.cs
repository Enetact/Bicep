using System.Collections.ObjectModel;
using System.Text.Json;

namespace SelfService.Foundation.Contracts;

// Transport descriptions only. Authentication, approval and execution stay in the owning service.
public sealed record FoundationAgentToolContract(string Name, string Action, string Description, string InputSchemaJson)
{
    public JsonElement InputSchema
    {
        get { using var document = JsonDocument.Parse(InputSchemaJson); return document.RootElement.Clone(); }
    }
}

public static class FoundationAgentToolContracts
{
    public const string Catalog = "platform_foundation_catalog";
    public const string Bicep = "platform_foundation_bicep";
    public const string Plan = "platform_foundation_plan";
    public const string Job = "platform_foundation_job";
    public const string Enqueue = "platform_foundation_enqueue";
    public const string Cancel = "platform_foundation_cancel";
    public const string Artifact = "platform_foundation_artifact";

    const string Reference = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""";

    // Preserve the existing ordered MCP/AHP surface. Human approval is deliberately absent.
    public static IReadOnlyList<FoundationAgentToolContract> Tools { get; } = Array.AsReadOnly(new[]
    {
        new FoundationAgentToolContract(Catalog, "platform/foundationCatalog", "Read the reviewed module index, exact contract or expanded parameter schema before Bicep. Not observed Azure state.", """{"type":"object","properties":{"moduleId":{"type":"string"},"parameter":{"type":"string"},"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":20}},"additionalProperties":false}"""),
        new FoundationAgentToolContract(Bicep, "platform/foundationBicep", "Generate unqualified parameterized Bicep with reviewed local/AVM modules and persist its source artifact. No discovery or deployment authority.", """{"type":"object","properties":{"subscriptionId":{"type":"string"},"region":{"type":"string"},"goal":{"type":"string","maxLength":2000},"modules":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object"}},"settings":{"type":"object"}},"required":["subscriptionId","region","goal","modules","settings"],"additionalProperties":false}"""),
        new FoundationAgentToolContract(Plan, "platform/foundationPlan", "Create an immutable customer-owned pipeline plan. This never approves or executes it.", """{"type":"object","properties":{"connectionId":{"type":"string"},"ref":{"type":"string"},"sourceRevision":{"type":"string"},"inputs":{"type":"object","additionalProperties":{"type":"string"}},"lifetimeMinutes":{"type":"integer","minimum":1,"maximum":60}},"required":["connectionId","ref","sourceRevision","inputs"],"additionalProperties":false}"""),
        new FoundationAgentToolContract(Job, "platform/foundationJob", "Read a customer-scoped durable job and sanitized receipt. Mock acceptance is never verified deployment.", Reference),
        new FoundationAgentToolContract(Enqueue, "platform/foundationEnqueue", "Enqueue an already separately human-approved exact plan. No approval tool exists. Unknown effects must not be replayed.", """{"type":"object","properties":{"id":{"type":"string"},"digest":{"type":"string"},"idempotencyKey":{"type":"string"}},"required":["id","digest","idempotencyKey"],"additionalProperties":false}"""),
        new FoundationAgentToolContract(Cancel, "platform/foundationCancel", "Request authorized cancellation. Dispatched work may require reconciliation.", Reference),
        new FoundationAgentToolContract(Artifact, "platform/foundationArtifact", "Read scoped artifact metadata and digest without exposing archive bytes to the model.", Reference)
    });

    public static IReadOnlyList<string> ToolNames { get; } = Array.AsReadOnly(Tools.Select(tool => tool.Name).ToArray());
    public static IReadOnlyDictionary<string, string> Actions { get; } = new ReadOnlyDictionary<string, string>(
        Tools.ToDictionary(tool => tool.Action, tool => tool.Name, StringComparer.Ordinal));
}
