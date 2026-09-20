# Repository completion audit

Dev onboarding update: the operator supplied the deployment identity and authorized generated dev tags and the two scoped exceptions. The [dev review record](reviews/eventflow-dev-exceptions.md) documents those values, provenance and controls. Dev parameters now pass local onboarding with a resolved prerequisite plan; actual Azure What-If/deployment and platform readiness remain unverified.

Event flow prerequisite resolution is now implemented: complete discovery saves Reuse / Create / Manage decisions, and conditional Bicep creates absent standard networking, DNS and monitoring in the workload stack. Failed reads never authorize creation. A fresh Discover artifact is required; owner, cost center, identity, reviews and private-agent/platform setup remain necessary. See [prerequisite resolution](prerequisite-resolution.md).

Current deployment menu update: the dedicated Blob copy/Event flow files expose **Preview** and **Deploy**, defaulting to Preview only. Preview consumes saved discovery and publishes its resource-change README to Summary / Extensions; it permits configured disabled targets, while actual Deploy still requires enablement. See [the run guide and method map](deployment-preview.md) and [current validation evidence](validation.md). The generic compatibility menu keeps its older setup/six-stage route. Azure preview and deployment acceptance remain unverified.

Audit date: 19 September 2026. Scope: the blob-transfer repository, its local runtime, Azure infrastructure, developer self-service release workflow and documentation. A working local stack and implemented deployment scripts do not establish a deployed Azure service.

## Second workload implementation

`logic-app-event-grid` / `eventflow` now has four disabled targets, reusable Event Grid/Logic App modules, separate runtime/event storage, eight endpoints, scoped RBAC, monitoring, deterministic workflow ZIP, typed discovery and bundles, its own Template Spec and guarded stack adapter. The [runbook](../workloads/logic-app-event-grid/README.md) documents all fields/methods and remaining platform work. Full local verification passed 225 existing contracts, 46 Logic App contracts, 34 pipeline contracts and 18 application tests; 15 opt-in emulator cases were skipped. Both wrappers and eight environment parameter sets compile; 15 YAML files parse.

This is local implementation evidence, not Azure production acceptance. The queue bridge and runtime credential exceptions, private topology, workflow expression/runtime behavior, delivery/RBAC, deployment/indexing and alerts need live acceptance before enabling targets. No ADO resource changes or Azure deployment were made.

## Current completion

| Area | Repository state | Verification and remaining work |
|---|---|---|
| Dispatcher, queue worker, ledger and timers | Implemented | Recorded real local Functions-host smoke passed on 17 September: BlobTrigger, QueueTrigger, three timers, three requests and shared destination. See [validation evidence](validation.md). |
| Local setup/run/test/stop/reset | Implemented | Recorded Windows ARM64 execution and lifecycle checks passed. Clean-machine missing-tool download branches and Windows x64 remain untested. Azure is not required locally. |
| Unit and storage integration tests | Implemented | Historical full emulator suite: 33 passed, zero skipped. Ordinary project verification excludes 15 opt-in integration cases. Evidence is dated, not presented as continuous monitoring. |
| Infrastructure and manual deployment | Implemented | Four Bicep environments compile. Private identity, networking, policies, quota and actual Azure deployment remain unverified. |
| Self-service workload/environment selection | Implemented | Separate menus with eight disabled target profiles; `blobcopy` and `eventflow` are registered. Disabled Deploy targets now run a configuration-only check on hosted `windows-latest`, with no private-pool or deployment-environment jobs. Enabled targets restore private deployment routing. |
| Frozen release, previews, guarded apply and readiness | Implemented | 42 offline contract/orchestration cases pass, including ARM-validation failure, evidence hashing, drift, immutable package conflicts and smoke gating. Azure commands are mocked. A real qualified bundle/run and Azure DevOps template expansion remain unverified. |
| Run-menu costs and platform options | Implemented; live UI verification outstanding | Dated East US 2 USD retail references, frozen cost report and approval summaries. Endpoint/alert settings are platform-owned; developers select workload type/name, environment and region. 15 cost/option cases pass. Usage and shared-platform costs are additional. See [cost guide](self-service-costs.md). |
| Enterprise intent and topology governance | Implemented for both registered patterns | 37 offline cases pass for strict requests, central-network requirements, resolver references and sensitive What-If changes. Unsupported patterns are rejected. Fifteen YAML files parse; all eight intent routes preserve protected bindings and hosted setup for disabled targets. See [architecture review](enterprise-platform.md). |
| Bicep source layout and ongoing pipeline verification | Implemented | Workload composition/environment values colocated; shared qualification/evidence steps serve Build and Deploy. 34 pipeline/infrastructure checks cover nested step expansion, cleanup and partial evidence. Six earlier compiled layout comparisons passed, excluding authoring metadata. See [repository conventions](repository-structure.md) and [pipeline flow](pipeline-flow.md). |
| Platform service connections, agent network and approvals | External setup required | No organization, subscription, destination owner, service connection, private agent or approvals were configured by this work. Enable profiles only after [onboarding](self-service.md). |
| Azure acceptance and production operations | Incomplete | Real trigger/runtime behavior, source versions, RBAC, all network paths, alerts, poison/restart/load tests and recovery drills remain required. |
| Cross-environment artifact promotion | Infrastructure version reuse implemented; application promotion not implemented | Identical compiled infrastructure reuses the content-hashed Template Spec version. Each selected target run still builds its own application release; no previously approved application build selector exists. |
| Subscription/network discovery and manifest handoff | Implemented; updated UI/handoff verification outstanding | 78 offline cases pass, including hosted setup routing for disabled targets and private deployment routing for enabled targets. Enabled deployments use the Resources run picker and verified discovery manifest; hosted setup does not validate that artifact. The new hosted route, resource-picker handoff and Azure deployment remain unverified in ADO. |
| Existing monitoring workspace and hub resolver reuse | Implemented; Azure acceptance outstanding | Explicit cross-subscription workspace/resolver/DNS IDs; shared workspace is referenced without updating its settings/RBAC. Alert queries filter by workload. Resolver support checks direct spoke DNS settings and hub zone links; it does not provision routing/peering. |
| Deployment Stacks and Template Specs | Implemented locally; Azure acceptance pending | 34 offline cases pass for content-hashed publication/reuse, native stack previews, guarded apply, subscription RG ownership, phase safety and lifecycle receipts. Implicit adoption/destructive teardown are rejected. See [upgrade runbook](deployment-stacks-upgrade.md). |
| Policy, private module registry and AVM | Partial platform foundation; registry deferred | Separate Policy-definition and private ACR templates compile. The workload uses local modules; registry setup/publication is excluded from this flow. No assignments, module publication or AVM migration performed. |
| Automated rollback, teardown and historical data migration | Not implemented | Failure retains resources and evidence. Migration/cleanup requires an explicit operator workflow. |
| Claims platform and AI agents | Outside this repository's implemented scope | No claims UI/API/database, Semantic Kernel agents, model calls or COBOL integration. This stack can transport files for such a platform. |

## Audit corrections included

- Added the current [developer/platform self-service guide](self-service.md), with exact usage, method responsibilities, prerequisites, permissions, artifacts and failure behavior.
- Replaced stale claims that all release automation is future work; retained the original architecture assessment as historical context.
- Retained self-service contract results and local host logs in pipeline artifacts, including partial qualification failures.
- Made trigger synchronization accept successful responses without JSON bodies.
- Required three distinct smoke request IDs before reporting Ready, with rejection coverage.
- Added entrypoint failure-receipt and existing-package reuse/conflict tests.

## Remaining acceptance order

1. Platform owner fills target parameters, provisions identity/permissions and establishes private routing/DNS to the workload VNet and destination.
2. Azure DevOps owner registers the discovery and deployment entry points, configures protected resources, approvals and exclusive lock checks, grants build/artifact read access for the handoff, then authorizes developers to queue them.
3. Enable only dev first. Run the complete hosted qualification, inspect both previews and perform the first real deployment/smoke. Retain receipts and resolve any Azure-specific failures.
4. Complete the additional live checks in [validation](validation.md), including alert delivery, version recovery, restart/poison behavior and load/capacity.
5. Enable higher environments after their independent acceptance. If release policy requires byte-identical cross-environment promotion, implement that flow before using this pipeline for production releases.

No completion percentage is assigned: code implementation, local verification, platform setup and production acceptance are different milestones.

## Documentation map

| Need | Reference |
|---|---|
| First local run and prerequisites | [Local development](local-development.md) |
| Exact local actions and method sequence | [Local workflow](local-workflow.md) |
| Dispatcher requirements, contracts and dependencies | [Dispatcher README](dispatcher/README.md) |
| Developer deployment and platform setup | [Self-service guide](self-service.md) |
| Enterprise ownership, strict intent and architecture gaps | [Enterprise platform review](enterprise-platform.md) |
| Subscription discovery, subnet selection and standard names | [Discovery guide](subscription-discovery.md) |
| Azure components and topology | [Architecture](architecture.md), [diagram guide](diagram-guide.md) |
| Permission boundaries and operations | [Security/RBAC](security-and-rbac.md), [operations](operations.md) |
| Measured results and unverified acceptance | [Validation](validation.md), [machine-readable results](validation-results.json) |
| Original proposals and deferred architecture | [Historical assessment](self-service-azure-devops-assessment.md) |

Generated logs, test evidence and packages remain ignored local artifacts or Azure DevOps artifacts. They are not all contained in a fresh Git clone. The checked-in validation documents identify the run and evidence path; retrieve the actual artifacts before independently attesting to historical results.
