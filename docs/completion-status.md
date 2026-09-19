# Repository completion audit

Audit date: 18 September 2026. Scope: the blob-transfer repository, its local runtime, Azure infrastructure, developer self-service release workflow and documentation. A working local stack and implemented deployment scripts do not establish a deployed Azure service.

## Current completion

| Area | Repository state | Verification and remaining work |
|---|---|---|
| Dispatcher, queue worker, ledger and timers | Implemented | Recorded real local Functions-host smoke passed on 17 September: BlobTrigger, QueueTrigger, three timers, three requests and shared destination. See [validation evidence](validation.md). |
| Local setup/run/test/stop/reset | Implemented | Recorded Windows ARM64 execution and lifecycle checks passed. Clean-machine missing-tool download branches and Windows x64 remain untested. Azure is not required locally. |
| Unit and storage integration tests | Implemented | Historical full emulator suite: 33 passed, zero skipped. Ordinary project verification excludes 15 opt-in integration cases. Evidence is dated, not presented as continuous monitoring. |
| Infrastructure and manual deployment | Implemented | Four Bicep environments compile. Private identity, networking, policies, quota and actual Azure deployment remain unverified. |
| Self-service workload/environment selection | Implemented | Separate menus with four disabled target profiles; only `blobcopy` is registered. Disabled Deploy targets now run a configuration-only check on hosted `windows-latest`, with no private-pool or deployment-environment jobs. Enabled targets restore private deployment routing. |
| Frozen release, previews, guarded apply and readiness | Implemented | 41 offline contract/orchestration cases pass, including discovery/cost evidence hashing, selected options in previews, drift, immutable package conflicts and smoke gating. Azure commands are mocked. A real qualified bundle/run and Azure DevOps template expansion remain unverified. |
| Run-menu costs and resource checkboxes | Implemented; live UI verification outstanding | Dated East US 2 USD retail references; actual destination-endpoint and log-alert options; frozen cost report and approval summaries. 15 cost/option cases pass and all four environments compile. Required resources stay included; production requires alerts. Usage is additional. See [cost guide](self-service-costs.md). |
| Platform service connections, agent network and approvals | External setup required | No organization, subscription, destination owner, service connection, private agent or approvals were configured by this work. Enable profiles only after [onboarding](self-service.md). |
| Azure acceptance and production operations | Incomplete | Real trigger/runtime behavior, source versions, RBAC, all network paths, alerts, poison/restart/load tests and recovery drills remain required. |
| Cross-environment artifact promotion | Not implemented | Each selected target run builds its own release. Same-run stage artifacts are frozen; no previously approved build selector exists. |
| Subscription/network discovery and manifest handoff | Implemented; updated UI/handoff verification outstanding | 78 offline cases pass, including hosted setup routing for disabled targets and private deployment routing for enabled targets. Enabled deployments use the Resources run picker and verified discovery manifest; hosted setup does not validate that artifact. The new hosted route, resource-picker handoff and Azure deployment remain unverified in ADO. |
| Existing monitoring workspace reuse | Not implemented | Template creates a workload workspace. |
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
| Subscription discovery, subnet selection and standard names | [Discovery guide](subscription-discovery.md) |
| Azure components and topology | [Architecture](architecture.md), [diagram guide](diagram-guide.md) |
| Permission boundaries and operations | [Security/RBAC](security-and-rbac.md), [operations](operations.md) |
| Measured results and unverified acceptance | [Validation](validation.md), [machine-readable results](validation-results.json) |
| Original proposals and deferred architecture | [Historical assessment](self-service-azure-devops-assessment.md) |

Generated logs, test evidence and packages remain ignored local artifacts or Azure DevOps artifacts. They are not all contained in a fresh Git clone. The checked-in validation documents identify the run and evidence path; retrieve the actual artifacts before independently attesting to historical results.
