# Documentation index

Reviewed 26 September 2026. Start with the [repository README](../README.md), [catalog](self-service-catalog.md) and [current completion status](completion-status.md). Code implementation, local tests, observed ADO steps and live Azure acceptance are distinct evidence levels.

## Current self-service guides

| Need | Guide |
|---|---|
| Available products, menus, resources and methods | [Catalog and implementation map](self-service-catalog.md) |
| Register definitions, configure permissions and queue runs | [Developer/platform self-service](self-service.md) |
| YAML jobs and artifact handoffs | [Pipeline flow](pipeline-flow.md) |
| Preview only and its README/What-If | [Deployment preview](deployment-preview.md) |
| Subscription inventory, static dropdowns and registration | [Discovery and naming](subscription-discovery.md) |
| Event flow Create / Reuse / Manage / Blocked | [Prerequisite resolution](prerequisite-resolution.md) |
| Template Specs, stack ownership and permissions | [Deployment Stacks runbook](deployment-stacks-upgrade.md) |
| Reference prices, exclusions and freshness | [Cost guide](self-service-costs.md) |
| Future products and platform improvements | [Expansion plan — proposed](self-service-expansion-plan.md) |
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
