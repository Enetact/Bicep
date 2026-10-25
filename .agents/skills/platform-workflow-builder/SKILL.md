---
name: platform-workflow-builder
description: Design or implement another Platform Studio self-service capability using the shared discovery, deterministic assessment, AHP/Codex/MCP review, editable draft and governed ADO workflow. Use for new iterations or alignment reviews, not for executing an existing deployment.
---

# Build a repeatable platform workflow

Locate the repository containing `config/workloads.json`. Read [the standard](../../../docs/agent-workflow-standard.md) and copy [the blueprint](../../../docs/templates/agent-workflow-blueprint.md) into the capability's design when useful. Inspect the current implementations before deciding which parts can be reused; distinguish documented plans from executable adapters.

Map the user journey to: scope → discovery/saved evidence → deterministic assessment → optional explicit agent review → validated recommendations → user-selected/edited draft → Preview → protected Apply → verification. Analysis-only capabilities stop before the draft/execution path. Pick a useful visual for the domain, such as topology or a matrix; do not require diagrams for every skill.

Reuse browser sessions, `AgentHostChannel`, `CodexAgentRuntime`, `AgentPolicy`, and `AgentMcpBridge`. AHP's bounded `workflow-orchestration` profile supplies readiness and registered typed dispatch; HTTP and AHP use the same workflow/broker handlers. Scoped review workflows expose only frozen evidence and the pinned skill. The user-authorized `mcp-orchestration` workflow separately exposes full Azure Bicep, Azure, ADO, GitHub, Fabric and Learn catalogs through `McpProviderBroker` using catalog/call/plan/run tools. Preserve this implemented expansion; do not reapply the earlier universal frozen-tools-only restriction. Changing, sensitive or unclassified calls require exact human approval through the separate HTTP route; no model/AHP approval tool exists. Keep Azure, ADO, GitHub, pipeline and Codex identities distinct, credentials out of model context, and identity/catalog/connection bindings intact. Readiness/sign-in must not start work automatically. Follow [the provider contract](../../../docs/provider-mcp.md) and distinguish source implementation from live acceptance.

Implement domain collectors/rules as deterministic code shared with the pipeline where necessary. Freeze scope and provenance; show partial, denied, stale and unsupported evidence explicitly. Register typed handlers and test them: a skill-library card or descriptor row alone does not implement a capability. Use `WorkflowReviews` and tagging's shared core as concrete examples, without inheriting tag-specific assumptions into other domains.

Validate structured recommendations against known evidence and allowed operations before creating drafts. User edits receive the same domain validation. Source-owned resources follow their source of truth. Separate model completion, output acceptance and verified execution. Do not let confidence scores bypass missing evidence or authorization.

For action-capable workflows, define exact request/artifact bindings, protected ADO stages, drift checks, scoped identities, partial-result receipts and unknown-outcome reconciliation. Preserve reviewed targets and unrelated state. Do not infer external write permission from a request to create this capability; implement locally and record live acceptance separately unless the user authorizes execution.

Extend root registration, packaging, shared contracts and documentation together. Follow the repository documentation-maintenance instructions; use the skill-creator guidance available in the authoring environment when creating an actual new skill. Test no-model functionality, scope/session isolation, malicious evidence, invalid recommendations, cancellation, stale plans and preservation. Run meaningful existing regression checks and the source manifest check. Report what is implemented, locally tested, live-accepted and still blocked by platform onboarding.
