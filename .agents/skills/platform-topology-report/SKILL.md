---
name: platform-topology-report
description: Generate workload-specific Markdown, Mermaid and static SVG reports from saved Blob copy or Event flow discovery. Use to explain observed resources and saved prerequisite actions, not to prove network reachability.
---

# Explain saved topology

Use [the implemented offline report workflow](../../../docs/self-service-analysis.md). The future MCP facade and live enterprise network analyzer are not implemented. Prefer the selected existing artifact over an independent scan.

Generate a new report using `scripts/Export-SelfServiceAnalysis.ps1 -DiscoveryDirectory <saved directory> -OutputDirectory <new artifacts path>`. Review the JSON findings before presenting diagrams. Observed means represented in saved inventory; Proposed means a saved prerequisite action. Contains edges mean resource parentage, not DNS, routes or traffic flow. Runtime remains Unverified.

Use the emitted SVG pages when Mermaid cannot render. Link the README and all relevant pages for larger graphs; cross-page relationships remain in `analysis.json`. Do not redraw missing edges from architectural expectations. Blob copy discovery has no full prerequisite composition plan, and unrelated ARM resource-catalog entries are omitted; state those coverage limits.

Store tenant reports only in ignored artifacts or the authorized pipeline artifact, never in reusable public examples. Show sanitized generated output rather than raw provider errors/settings. Do not commit tenant reports, read secret values, infer approvals, or invoke Azure deployment while fulfilling a diagram request.
