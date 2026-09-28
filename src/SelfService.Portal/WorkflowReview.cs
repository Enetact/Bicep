using SelfService.Tagging;

namespace SelfService.Portal;

// Shared structured-advice boundary. Legacy Markdown workflows keep their existing renderer.
public record WorkflowRecommendation(string Id,string ResourceId,string Key,string Value,string Operation,string[] EvidenceRefs,string Rationale);
public record WorkflowReview(string Schema,string Workflow,string EvidenceDigest,string Summary,WorkflowRecommendation[] Recommendations,bool AdvisoryOnly=true);
public static class WorkflowReviews
{
    public static readonly AgentWorkflow[] Definitions=[
        new("visualize","Azure resource visualizer","azure--azure-resource-visualizer","azure","resource-group","Selected group or browser-owned network snapshot. Evidence-validated diagrams; no reachability or deployment authorization."),
        new("network","Private network evidence review","azure--azure-resource-visualizer","azure","resource-group","Configured network evidence only. Partial visibility, free IPs and effective routing remain unknown."),
        new("workload","Workload configuration advisor","platform-request-design",null,"workload","Proposed catalog configuration. Advisory Markdown; no automatic draft or pipeline queue."),
        new("preview","Saved Preview change review","platform-change-review","ado","preview","Bounded saved Preview projection. Does not prove full bundle integrity or approval."),
        new("tagging","Tag governance review","platform-tagging-review","azure","tag-evidence","Selected saved tag evidence. Structured advice validated against rules and resource IDs. Explicit selection creates an editable draft; no agent write tools."),
        new("bicep-draft","Design Bicep source draft","platform-bicep-composition","azure","composition","Saved network discovery plus selected workload context and allowlisted target overrides. Generates local module-based source drafts; compile and qualify before registration. No pipeline queue, deployment or IPAM reservation.")
    ];
    public static WorkflowReview Tags(string output,TagInventory inventory,TagProfile profile)
    {
        var text=output.Trim();if(text.StartsWith("```json\n",StringComparison.Ordinal)&&text.EndsWith("```",StringComparison.Ordinal))text=text[8..^3].Trim();
        var result=TagJson.Read<WorkflowReview>(text);var findings=TagRules.Analyze(inventory,profile);
        if(result.Schema!="platform.agent-review/v1"||result.Workflow!="tagging"||result.EvidenceDigest!=inventory.Digest||!result.AdvisoryOnly||result.Summary.Length>8000||result.Recommendations.Length>50||result.Recommendations.Select(x=>x.Id).Distinct().Count()!=result.Recommendations.Length)throw new TagException("Agent review schema, digest or count rejected.");
        foreach(var r in result.Recommendations){
            var resource=inventory.Resources.SingleOrDefault(x=>x.Id==r.ResourceId)??throw new TagException("Agent invented a resource reference.");
            if(string.IsNullOrWhiteSpace(r.Id)||r.Id.Length>100||r.Rationale.Length>2000||r.Operation is not("AddIfAbsent" or "SetValue")||r.EvidenceRefs is not{Length:>0}||r.EvidenceRefs.Any(id=>!findings.Any(f=>f.Id==id&&f.ResourceId==r.ResourceId)))throw new TagException("Recommendation lacks applicable evidence or operation.");
            TagRules.ValidateKey(resource.Type,r.Key,r.Value,profile);
        }return result with{AdvisoryOnly=true};
    }
}
