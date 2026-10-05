namespace SelfService.Operations;

public static class OperationsRules
{
    public static OperationsAssessment Assess(string workflow, OperationsEvidence observed, OperationsEvidence? reference, DesiredEdit[] edits)
    {
        if (!OperationsRegistry.AnalysisWorkflows.Contains(workflow)) throw new OperationsException("Workflow has no implemented analysis adapter.");
        if (edits is null || workflow == "private-dns" && edits.Length != 0) throw new OperationsException("Desired property edits require the drift workflow.");
        var findings = PrivateDnsRules.Analyze(observed);
        var comparisons = workflow == "drift" ? DriftRules.Compare(reference, observed, edits) : [];
        findings = findings.Concat(comparisons.Select(r => new OperationFinding(OperationsJson.Hash(r)[..16], "DRIFT-001", r.State,
            r.State == "Conflict" ? "Warning" : "Information", r.ResourceId,
            $"{r.Property}: {r.State}. Reference authority and missing desired/property coverage remain explicit; no automatic reconciliation.", [r.ResourceId]))).ToArray();
        var result = new OperationsAssessment("platform.operations-assessment/v1", Guid.NewGuid().ToString("N"), workflow, observed.Digest, reference?.Digest,
            OperationsJson.Hash(edits), reference is null ? "Missing accepted baseline" : "Local observed reference; not an accepted deployment baseline", edits, findings, comparisons);
        return result with { Digest = OperationsJson.Hash(result with { Digest = "" }) };
    }
    public static OperationsAdvice Advice(string output, OperationsAssessment assessment, OperationsEvidence evidence)
    {
        OperationsAdvice a;
        try { a = OperationsJson.Read<OperationsAdvice>(output); } catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { throw new OperationsException("Agent output is not the operations review contract."); }
        if (a.Schema != "platform.operations-advice/v1" || a.Workflow != assessment.Workflow || a.AssessmentDigest != assessment.Digest || a.Summary is null || a.Summary.Length > 8000 ||
            a.Recommendations is not { Length: <= 30 } || a.Recommendations.Any(r => r is null) || a.Recommendations.Select(r => r.Id).Distinct().Count() != a.Recommendations.Length)
            throw new OperationsException("Agent advice schema, assessment binding or count rejected.");
        foreach (var r in a.Recommendations)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 100 || r.Operation is not ("Investigate" or "SourceAmendment" or "MigrationReview") ||
                assessment.Workflow == "drift" && r.Operation == "MigrationReview" || !evidence.Resources.Any(e => e.Id == r.ResourceId) || r.Rationale is null || r.Rationale.Length > 2000 || r.EvidenceRefs is not { Length: > 0 and <= 20 } ||
                r.EvidenceRefs.Any(id => !assessment.Findings.Any(f => f.Id == id && (f.ResourceId == r.ResourceId || f.EvidenceRefs.Contains(r.ResourceId)))))
                throw new OperationsException("Agent invented a resource, operation or applicable finding reference.");
        }
        return a with { AdvisoryOnly = true };
    }
    public static OperationsProposal Propose(OperationsEvidence evidence, OperationsAssessment assessment, string operation, string[] resourceIds, string? destinationId, string owner, string rationale)
    {
        if (operation is not ("Investigate" or "SourceAmendment" or "MigrationReview") || resourceIds is not { Length: > 0 and <= 50 } || resourceIds.Distinct().Count() != resourceIds.Length ||
            resourceIds.Any(id => !evidence.Resources.Any(r => r.Id == id)) || string.IsNullOrWhiteSpace(owner) || owner.Length > 200 || string.IsNullOrWhiteSpace(rationale) || rationale.Length > 4000)
            throw new OperationsException("Select observed resources, a supported proposal type, owner and rationale.");
        if (operation == "MigrationReview")
        {
            var destination = evidence.Resources.SingleOrDefault(r => r.Id == destinationId && PrivateDnsRules.Zone(r)) ?? throw new OperationsException("Select an observed destination zone.");
            if (resourceIds.Any(id => !evidence.Resources.Any(r => r.Id == id && PrivateDnsRules.Zone(r) && r.Id != destination.Id && r.Name.Equals(destination.Name, StringComparison.OrdinalIgnoreCase))))
                throw new OperationsException("Migration candidates must be distinct same-name zones. This is not approval to consolidate them.");
        }
        else if (destinationId is not null) throw new OperationsException("Destination applies only to a migration proposal.");
        var p = new OperationsProposal("platform.operations-proposal/v1", Guid.NewGuid().ToString("N"), assessment.Digest, operation, resourceIds, destinationId, owner, rationale,
            ["Analysis-only adapter: no Azure writes or ADO execution handler.", "Source ownership, private-path verification, approved baseline retention and qualified Preview/Apply/Verify remain required."], DateTimeOffset.UtcNow.AddMinutes(15));
        return p with { Digest = OperationsJson.Hash(p with { Digest = "" }) };
    }
    public static OperationsPreview Preview(OperationsProposal proposal, OperationsAssessment assessment, DateTimeOffset now)
    {
        if (proposal.Digest != OperationsJson.Hash(proposal with { Digest = "" }) || proposal.AssessmentDigest != assessment.Digest || proposal.ExpiresUtc <= now)
            throw new OperationsException("Proposal changed, belongs to another assessment or expired. Create a new proposal.");
        return new("Proposal review — not ARM What-If or a deployable migration plan", proposal.Digest, assessment.Digest, proposal.ExpiresUtc,
            proposal.ResourceIds.Select(id => $"{proposal.Operation}: {id}").Concat(assessment.DesiredEdits.Where(e => proposal.ResourceIds.Contains(e.ResourceId)).Select(e => $"Proposed {e.Property} = {e.Value} on {e.ResourceId}; unqualified source edit")).ToArray(), proposal.Blockers);
    }
}
