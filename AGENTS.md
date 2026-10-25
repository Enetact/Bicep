# Documentation maintenance

When adding, changing or removing developer-facing capabilities in this repository, use the [self-service-docs skill](.agents/skills/self-service-docs/SKILL.md) to update the affected documentation in the same task. Also use it for documentation/status audits. Routine changes with no documented behavior impact do not require unrelated documentation churn.

Keep implemented behavior, local verification, observed ADO steps, Azure acceptance and proposed expansion distinct. Derive current facts from the checkout and retain dated evidence. Follow the skill's document map and source-manifest verification workflow; it does not authorize deployments or target enablement.

When designing or adding evidence-based self-service agent workflows, follow [the workflow standard](docs/agent-workflow-standard.md) and start from [the workflow blueprint](docs/templates/agent-workflow-blueprint.md). Reuse the existing AHP coordination, Codex runtime/policy and scoped MCP evidence bridge. Keep deterministic validation, optional agent advice, user-authored drafts and protected execution separate. The standard identifies proposed shared contracts; do not claim they exist until implemented and verified.

For new self-service capabilities or alignment reviews, apply the [platform-workflow-builder skill](.agents/skills/platform-workflow-builder/SKILL.md). Its reusable sequence does not authorize cloud execution.

# Connected provider guidance

For Azure Bicep, Azure, Azure DevOps, GitHub, Microsoft Fabric or Microsoft Learn MCP workflows, use the [orchestration skill](.agents/skills/platform-mcp-orchestration/SKILL.md), its editable [instruction.md](.agents/skills/platform-mcp-orchestration/instruction.md), and the appropriate provider skill/instruction pair under `providers/`. The runtime consumes the generated, self-contained `SKILL.md`; companion files are its reviewed sources. Root `AGENTS.md`, arbitrary Markdown and upstream packages are not implicitly loaded into the isolated model process.

After editing indexed guidance, run `./scripts/Update-McpGuidance.ps1`, then `./scripts/Update-McpGuidance.ps1 -Check` and `node scripts/mcp/verify-skill-sources.mjs`. Retain upstream licenses and pinned provenance. Full Azure, ADO and Learn authoring packages are indexed by [the source reference](.agents/skills/platform-mcp-orchestration/references/provenance.md); do not silently replace the host's tool names, credential routing or approval rules with upstream examples. A skill or instruction file does not register a tool, grant permissions or approve a plan. Follow the existing documentation and source-manifest workflow after final edits.
