# Documentation index

- [Bulk ADO pipeline registration](ado-pipeline-registration.md): register the 18 root entry points from the portal, with source checks, conflict detection and no runs.

- [Network discovery, validated agent diagrams and AVNM IPAM](network-discovery-and-diagrams.md): implemented scope/diagram/allocation flows, platform setup and ordered manual tests.

- [Workload onboarding and seven-product catalog](workload-onboarding.md): dedicated menus, shared adapter, new modules, runtime samples and acceptance gates.

Reviewed 27 September 2026. Start with the [repository README](../README.md), [catalog](self-service-catalog.md) and [current completion status](completion-status.md). Code implementation, local tests, observed ADO steps and live Azure acceptance are distinct evidence levels.

## Current self-service guides

| Need | Guide |
|---|---|
| Local website, browser sign-in, ADO requests and ARM64/x64 packages | [Platform Studio](local-portal.md) |
| Codex Astra/High/Standard authentication, scoped MCP tools and agent reviews | [Agent workflows](agent-workflows.md) |
| Repeated AHP/Codex/MCP workflow, shared contracts and new-skill design | [Workflow standard](agent-workflow-standard.md), [copyable blueprint](templates/agent-workflow-blueprint.md) |
| Existing-resource diagrams, proposed workloads and saved Azure Preview changes | [Connected portal diagrams](portal-diagrams.md) |
| Complete Microsoft skill bundle and Azure-only read-only discovery | [Azure skill discovery](azure-skill-discovery.md) |
| Tenant/management-group discovery feasibility and proposed scope menu | [Scope verification and design](plans/tenant-network-discovery.md) |
| Available products, menus, resources and methods | [Catalog and implementation map](self-service-catalog.md) |
| Register definitions, configure permissions and queue runs | [Developer/platform self-service](self-service.md) |
| YAML jobs and artifact handoffs | [Pipeline flow](pipeline-flow.md) |
| Preview only and its README/What-If | [Deployment preview](deployment-preview.md) |
| Subscription inventory, static dropdowns and registration | [Discovery and naming](subscription-discovery.md) |
| Saved-discovery coverage, topology and local skill workflows | [Offline analysis guide](self-service-analysis.md) |
| Delivered enhancement increments and remaining gates | [Implementation progress](plans/implementation-progress.md) |
| Event flow Create / Reuse / Manage / Blocked | [Prerequisite resolution](prerequisite-resolution.md) |
| Template Specs, stack ownership and permissions | [Deployment Stacks runbook](deployment-stacks-upgrade.md) |
| Reference prices, exclusions and freshness | [Cost guide](self-service-costs.md) |
| Future products and platform improvements | [Expansion plan — proposed](self-service-expansion-plan.md) |
| Subscription tag discovery, resource/tag matrix, skill advice and governed updates | [Tag governance implementation plan — proposed](plans/tagging-self-service.md) |
| Implementation order, approval stages and final acceptance gates | [Enhanced self-service readiness review — proposed](plans/enhanced-self-service-validation.md) |
| Automatic network/subnet planning, IPAM and AI assistance | [Private networking design — proposed](plans/private-networking-self-service.md) |
| Microsoft Azure skills for discovery, diagrams and assessment | [Azure skills assessment and adoption plan — proposed](azure-skills-assessment.md) |
| Platform-specific skill workflows and typed MCP interfaces | [Platform skills and MCP design — proposed](plans/platform-mcp-skills.md) |
| Module/environment placement | [Repository conventions](repository-structure.md), [reusable modules](../modules/README.md) |

## Workload guides

| Scope | Guide |
|---|---|
| Blob copy composition | [Blob transfer](../workloads/blob-transfer/README.md) |
| Blob copy methods and dependencies | [Dispatcher](dispatcher/README.md), [local workflow](local-workflow.md) |
| Blob copy local setup | [Local development](local-development.md) |
| Blob copy flow, recovery and permissions | [Architecture](architecture.md), [operations](operations.md), [security/RBAC](security-and-rbac.md) |
| Blob copy diagrams | [Diagram guide](diagram-guide.md), [static HTML](diagram-guide.html) |
| Event flow resources, methods and onboarding | [Logic App + Event Grid](../workloads/logic-app-event-grid/README.md) |
| Dev-only exception authorization | [Review record](reviews/eventflow-dev-exceptions.md) |

Module READMEs describe implemented resource contracts, not extra catalog offerings. Functions/Azurite instructions do not emulate Event flow's hosted runtime.

## Evidence and historical design

- [Completion status](completion-status.md) is the current summary.
- [Validation log](validation.md) and [machine-readable evidence](validation-results.json) preserve dated results. Read `currentDocumentationReview` before interpreting earlier snapshot fields as current state.
- [Enterprise platform review](enterprise-platform.md) explains ownership and bounded workload support.
- [Original ADO assessment](self-service-azure-devops-assessment.md) and [Microsoft Learn gap assessment](microsoft-learn-platform-assessment.md) predate later implementation and are historical.
- [Event flow implementation plan](plans/logic-app-event-grid-workload.md) records the delivered second-workload scope and outstanding acceptance. The expansion plan describes future work.
- [References](references.md) contains source links; a reference is not proof that a capability is implemented or deployed.

Plans do not grant deployment authority or replace protected pipeline checks. Updating documentation does not refresh historical execution evidence.

## Maintaining these guides with Codex

The repository includes the [self-service-docs skill](../.agents/skills/self-service-docs/SKILL.md) and an [AGENTS.md rule](../AGENTS.md) to apply it as capabilities change. It maps code changes to affected guides, preserves dated verification evidence, reconciles the expansion plan, and checks the source manifest.

Open Codex in the `Bicep` repository directory (or a subdirectory) and request: `Use $self-service-docs to update documentation for the current changes.` Automatic selection is enabled for matching tasks; this is a task workflow, not a background file watcher or CI job. Repository skill discovery follows the [official Codex skill guidance](https://learn.chatgpt.com/docs/build-skills). If it does not appear, restart Codex.
