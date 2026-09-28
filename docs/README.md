# Platform Studio wiki

[Repository home](../README.md) · [Getting started](getting-started.md) · [Platform overview](platform-overview.md) · [Workloads](workload-catalog.md) · [Status](completion-status.md)

The reference for developing, operating and extending Platform Studio. This wiki lives in the repository so documentation and implementation can be reviewed in the same pull request. It does not require a separate GitHub Wiki repository or publishing service.

## Start with your task

| I want to… | Reading path |
|---|---|
| Try the local application | [Getting started](getting-started.md) → [Portal setup](local-portal.md) |
| Understand the suite | [Platform overview](platform-overview.md) → [Current status](completion-status.md) |
| Preview or deploy a workload | [Workload catalog](workload-catalog.md) → [ADO onboarding](self-service.md) → [Preview](deployment-preview.md) |
| Discover networks and explain topology | [Network discovery](network-discovery-and-diagrams.md) → [Diagrams](portal-diagrams.md) → [Agent workflows](agent-workflows.md) |
| Analyze or change tags | [Tag governance](tag-governance.md) → [Permissions](security-and-rbac.md) |
| Understand recovery | [Recovery rules](recovery-rules.md) → [State and recovery plan](plans/deployment-state-and-recovery.md) |
| Add a workload or skill | [Source conventions](repository-structure.md) → [Workflow standard](agent-workflow-standard.md) → [Blueprint](templates/agent-workflow-blueprint.md) |

## Understand the platform

| Guide | What it covers |
|---|---|
| [Platform overview](platform-overview.md) | Components, evidence flow, identities, capability suite and agent boundaries. |
| [Current implementation status](completion-status.md) | What is implemented, locally verified, observed in ADO and still awaiting live acceptance. |
| [Workload catalog](workload-catalog.md) | Seven offerings, resource descriptions and exact pipeline entry points. |
| [Detailed catalog and method map](self-service-catalog.md) | Workload types, named instances, target contracts and original adapter methods. |
| [Enterprise platform design](enterprise-platform.md) | Ownership, governance and bounded workload support. |
| [Source conventions](repository-structure.md) | Module/composition placement, configuration, runtime files and test boundaries. |

## Develop and operate locally

| Guide | What it covers |
|---|---|
| [Getting started](getting-started.md) | Ordered installation, upgrade, verification and packaging commands. |
| [Portal setup and authentication](local-portal.md) | Entra registration, Azure/ADO browser sign-in, configuration and Windows packages. |
| [Blob copy local development](local-development.md) | Functions/Azurite prerequisites, data, lifecycle and troubleshooting. |
| [Exact local workflow](local-workflow.md) | Blob upload, dispatcher, queue worker, ledger and timers. |
| [Dispatcher reference](dispatcher/README.md) | Methods, dependencies, settings and execution behavior. |
| [Blob copy architecture](architecture.md) | Transfer design and resource responsibilities. |
| [Operations and recovery](operations.md) | Blob copy reconciliation and operator procedures. |

## Configure Azure DevOps delivery

| Guide | What it covers |
|---|---|
| [Developer and platform onboarding](self-service.md) | Definitions, protected environments, permissions and self-service use. |
| [Bulk pipeline registration](ado-pipeline-registration.md) | Reviewed registration of the 20 root entry points, reuse and conflict handling. |
| [Pipeline flow](pipeline-flow.md) | Stages, script calls, shared templates and artifact handoffs. |
| [Deployment Preview](deployment-preview.md) | Bicep validation, Azure What-If, report artifacts and deployment gates. |
| [Deployment Stacks and Template Specs](deployment-stacks-upgrade.md) | Publication, stack ownership and lifecycle controls. |
| [Subscription discovery](subscription-discovery.md) | Saved manifests, naming, scope and static menu generation. |
| [Prerequisite resolution](prerequisite-resolution.md) | Event flow Create / Reuse / Manage / Blocked decisions. |
| [Workload onboarding](workload-onboarding.md) | Shared dependencies, identities and acceptance tests for the seven products. |
| [Cost assumptions](self-service-costs.md) | Dated estimates, exclusions, freshness and missing-price behavior. |
| [Security and RBAC](security-and-rbac.md) | Identity separation, permissions, protected operations and data handling. |

## Discover, analyze and design

| Guide | What it covers |
|---|---|
| [Azure Skills library](azure-skill-discovery.md) | Pinned Microsoft skills, read-only discovery profiles and scope limitations. |
| [Network discovery and IPAM](network-discovery-and-diagrams.md) | Tenant/management-group collection, deterministic checks, AVNM setup and manual tests. |
| [Connected portal diagrams](portal-diagrams.md) | Observed topology, conceptual proposals and saved Azure Preview diagrams. |
| [Offline discovery analysis](self-service-analysis.md) | Saved inventory/manifest reports without cloud access or a model. |
| [Codex agent workflows](agent-workflows.md) | Six explicit reviews/drafts, AHP coordination, MCP evidence, identity and receipts. |
| [AI-assisted Bicep drafts](bicep-source-drafts.md) | Local module contracts, generated parameters, source downloads and qualification. |
| [Tag governance](tag-governance.md) | Resource/tag matrix, deterministic analysis, editable drafts and protected changes. |
| [Recovery rules](recovery-rules.md) | Fourteen policies, exclusions, portal views and non-authorizing offline checks. |

## Extend the suite

| Reference or plan | Purpose |
|---|---|
| [Reusable modules](../modules/README.md) | Shared Bicep contracts and module-specific documentation. |
| [Workflow standard](agent-workflow-standard.md) and [blueprint](templates/agent-workflow-blueprint.md) | Repeatable evidence → assessment → optional AI → reviewed draft → governed execution. |
| [Expansion roadmap](self-service-expansion-plan.md) | Future capabilities and delivery priorities. |
| [Readiness and validation plan](plans/enhanced-self-service-validation.md) | Cross-cutting acceptance gates and rollout sequencing. |
| [Bicep composition and module audit](plans/bicep-composition-and-module-audit.md) | Module gaps, parameter authority and source qualification. |
| [Private networking design](plans/private-networking-self-service.md) | Complex network self-service, deterministic checks and allocation authority. |
| [Tenant discovery assessment](plans/tenant-network-discovery.md) | Collection feasibility, visibility and coverage requirements. |
| [Diagram delivery plan](plans/network-diagram-delivery.md) | Evidence validation, rendering and network integration increments. |
| [State and recovery plan](plans/deployment-state-and-recovery.md) | Retained baselines, before/after evidence and future protected restoration. |
| [Tagging implementation plan](plans/tagging-self-service.md) | Domain-specific workflow and live acceptance gates. |
| [Platform skills and MCP design](plans/platform-mcp-skills.md) | Proposed lifecycle, policy, identity and runtime review adapters. |
| [Azure Skills assessment](azure-skills-assessment.md) | Upstream skill adoption and boundaries. |
| [Implementation progress](plans/implementation-progress.md) | Dated delivery milestones; use current status for present capability claims. |

Plans can contain both delivered increments and future phases. A planned entry does not register a pipeline, enable a target or grant permission to deploy.

## Evidence and historical references

Use the [validation log](validation.md) and [validation results](validation-results.json) for dated commands, test counts, artifacts and observed ADO behavior. Historical evidence may be stored locally or in ADO and is not necessarily included in a fresh clone.

Earlier design material is retained for context: [initial ADO assessment](self-service-azure-devops-assessment.md), [Microsoft Learn assessment](microsoft-learn-platform-assessment.md), [Event flow implementation plan](plans/logic-app-event-grid-workload.md), [Blob copy diagrams](diagram-guide.md) and [static diagram](diagram-guide.html). [Dev exception records](reviews/eventflow-dev-exceptions.md) apply only to their documented scope. Consult [references](references.md) for platform sources; a citation is not proof of deployment acceptance.

## Maintaining these guides with Codex

The [self-service-docs skill](../.agents/skills/self-service-docs/SKILL.md) and [repository instructions](../AGENTS.md) require documentation to change alongside capabilities. Use the [workflow-builder skill](../.agents/skills/platform-workflow-builder/SKILL.md) when adding a self-service workflow.

Keep the root README concise. Put procedures and method contracts in the owning guide, current capability claims in [completion status](completion-status.md), dated results in [validation](validation.md), and proposals under `plans/`. Link those pages from this home instead of adding another announcement to every document. Use relative links so the wiki works on GitHub and in a checkout.

For documentation-only changes, validate links and anchors, review the diff, then run:

```powershell
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
git diff --check
```

These commands update/check source metadata; they do not establish runtime or Azure acceptance. Full application tests and service restarts are unnecessary for documentation-only edits.
