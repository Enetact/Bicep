# Documentation maintenance

When adding, changing or removing developer-facing capabilities in this repository, use the [self-service-docs skill](.agents/skills/self-service-docs/SKILL.md) to update the affected documentation in the same task. Also use it for documentation/status audits. Routine changes with no documented behavior impact do not require unrelated documentation churn.

Keep implemented behavior, local verification, observed ADO steps, Azure acceptance and proposed expansion distinct. Derive current facts from the checkout and retain dated evidence. Follow the skill's document map and source-manifest verification workflow; it does not authorize deployments or target enablement.

When designing or adding evidence-based self-service agent workflows, follow [the workflow standard](docs/agent-workflow-standard.md) and start from [the workflow blueprint](docs/templates/agent-workflow-blueprint.md). Reuse the existing AHP coordination, Codex runtime/policy and scoped MCP evidence bridge. Keep deterministic validation, optional agent advice, user-authored drafts and protected execution separate. The standard identifies proposed shared contracts; do not claim they exist until implemented and verified.

For new self-service capabilities or alignment reviews, apply the [platform-workflow-builder skill](.agents/skills/platform-workflow-builder/SKILL.md). Its reusable sequence does not authorize cloud execution.
