# Platform Studio wiki

**Managed DevOps Pools:** [Create a private build-agent pool](../workloads/managed-devops-pool/README.md) through the standard Discover/Preview/protected Apply flow, then qualify a consumer job before switching workload runners. Current source has eleven types, 44 disabled Azure targets and 30 registered roots. Earlier dated increments below retain their original counts.

**Fabric:** [Premium workspace, connectors, private networking and deployment pipelines](../workloads/fabric/README.md) follows the standard Azure flow and a separate protected Fabric API lifecycle. Current source has eleven workload types, 44 disabled Azure targets, 30 root pipelines and seven source MCP providers; older dated counts retain their original scope. Source registration does not establish live provider acceptance.

For a fresh local rebuild that preserves eligible sign-ins, use [the protected restart script and recovery guide](portal-fresh-restart.md).

[Repository home](../README.md) · [Getting started](getting-started.md) · [Platform overview](platform-overview.md) · [Workloads](workload-catalog.md) · [Status](completion-status.md)

The reference for developing, operating and extending Platform Studio. This wiki lives in the repository so documentation and implementation can be reviewed in the same pull request. It does not require a separate GitHub Wiki repository or publishing service.

## Start with your task

| I want to… | Reading path |
|---|---|
| Establish scalable foundations, close selected Port/Cycloid gaps and prepare a licensed offering | [Foundation-first commercial roadmap](plans/platform-studio-commercial-release.md) → [Customer isolation and identity architecture](plans/platform-studio-foundations-and-customer-isolation.md) |
| Run the disconnected customer foundation and customer-owned GitHub/ADO pipeline tests | [Foundation service](foundation-service.md) → [Customer pipeline examples](../examples/customer-pipelines/README.md) → [SQLite ADR](adr/0001-foundation-dedicated-cell-sqlite.md) |
| Review rotating identity keys, encrypted credentials, temporary support, signed audit anchors and release evidence | [Foundation security and operations](foundation-security-operations.md) |
| Review the exact current publication after an uncertain local credential operation | [Publication observation](foundation-maintenance-observation.md) → [Observation evidence and restore](foundation-maintenance-observation-evidence.md) |
| Identify authenticated credential dependencies and review advisory key-retention holds | [Recovery inventory](foundation-credential-recovery-inventory.md) → [Retention assessment](foundation-credential-recovery-retention.md) |
| Capture a signed encrypted credential recovery set and rehearse its retained bytes without restoring a vault | [Signed recovery sets](foundation-credential-recovery-set.md) → [Read-only rehearsal](foundation-credential-recovery-rehearsal.md) |
| Re-encrypt a reviewed recovery set into fresh inactive staging and verify its completed output | [Recovery staging](foundation-credential-recovery-staging.md) → [Completed-stage verification](foundation-credential-recovery-stage-verification.md) |
| Authorize one read-only verification across every recovery project scope | [Durable recovery review](foundation-credential-recovery-review.md) → [Held verification execution](foundation-credential-recovery-review-execution.md) |
| Pin the current accepted credential set and retain its selection through inactive staging | [Credential-set checkpoints](foundation-credential-recovery-checkpoints.md) → [Custody-backed staging](foundation-credential-checkpoint-staging.md) |
| Preserve original scope after an abandoned first credential write in a fresh opt-in source format | [Source absence proofs](foundation-absence-scope-provenance.md) → [Authenticated scope inspection](foundation-absence-scope-inspection.md) |
| Capture and rehearse both envelopes and absence proofs from a signed source-v2 artifact | [Scoped retained recovery and key holds](foundation-scoped-recovery.md) |
| Authenticate scoped destination ciphertext and absence proofs under inactive custody | [Scoped destination staging](foundation-scoped-recovery-staging.md) |
| Approve exact verification across every present and absent project/environment | [Complete-scope human review](foundation-scoped-recovery-review.md) |
| Hold the accepted scoped retained-set checkpoint through stage completion | [Scoped checkpoint custody](foundation-scoped-recovery-checkpoints.md) |
| Verify an approved scoped stage while its current checkpoint remains held through the database commit | [Joint checkpoint and stage verification](foundation-scoped-recovery-joint-verification.md) |
| Keep a restored cell from admitting fresh ordinary execution | [Restore execution hold](foundation-recovery-execution-hold.md) |
| Bind Foundation credential dispatch to an exact active instance and custody namespace | [Credential runtime binding](foundation-credential-runtime-binding.md) |
| Compose the mock Foundation consumer, restrict a fresh namespace and retain actual activation through its outcome | [Bound pipeline consumer](foundation-bound-pipeline-consumer.md) |
| Build an owned compiler generation and safely remove superseded intermediates after successful full tests | [Owned builds and cleanup](foundation-owned-build-cleanup.md) |
| Retain the latest accepted backup checkpoint and verify/stage recovery against that custody | [Local checkpoint authority](foundation-backup-checkpoint-custody.md) → [Custody-backed recovery](foundation-backup-checkpoint-recovery.md) |
| Operate separate workers, export frozen audit metadata or review feature policy | [Independent worker](foundation-independent-worker.md) → [Paged exports](foundation-paged-exports.md) → [Feature configuration](foundation-feature-configuration.md) |
| Measure cell health/load, verify signed recovery or inspect distribution evidence | [Cell metrics](foundation-cell-metrics.md) → [Local load qualification](foundation-load-qualification.md) → [Signed backups](foundation-signed-backups.md) → [Dependency evidence](foundation-dependency-evidence.md) |
| Coordinate restored cell activation locally | [Protected activation authority and runtime leases](foundation-cell-activation.md) |
| Initialize a new cell or verify/stage signed recovery through trusted owner commands | [Owner CLI](foundation-owner-cli.md) |
| Prepare the cell's Entra roles/scopes and exact callers without granting directory permissions | [Entra onboarding proposals](foundation-entra-onboarding.md) |
| Review explicit key provisioning, revocation ordering and credential deadlines | [Credential lifecycle](foundation-credential-lifecycle.md) |
| Move an encrypted profile to an already-provisioned key while preserving expiry and revocation | [Credential rewrap](foundation-credential-rewrap.md) |
| Recheck current durable job, approval and connection authority before credential admission | [Durable dispatch authorization](foundation-durable-dispatch-authorization.md) |
| Resolve uncertain jobs while suspended/offboarding without customer reactivation | [Inactive reconciliation](foundation-inactive-reconciliation.md) |
| Bind a fresh foundation test run to source inventories and its actual TRX | [Test execution evidence](foundation-test-execution-evidence.md) |
| Provision fresh owner-protected credential custody and validate existing namespaces without repair | [Credential namespaces](foundation-credential-namespaces.md) |
| Detect older credential envelopes using a separately protected local authority | [Opt-in credential rollback checks](foundation-credential-rollback.md) |
| Prepare and separately approve local credential revoke/rewrap with durable outcomes | [Reviewed credential maintenance](foundation-credential-maintenance.md) |
| Verify scoped maintenance audit evidence and restore fencing | [Maintenance evidence](foundation-credential-maintenance-evidence.md) |
| Validate worker receipt destinations before logs, database access or registration | [Worker receipts and preflight](foundation-worker-receipts.md) |
| Verify a pinned execution receipt and exact progress-only changes during release qualification | [Release execution binding](foundation-release-execution-evidence.md) |
| Stage, verify and repair a portable foundation preview without switching a running app | [Preview staging lifecycle](foundation-preview-installation.md) |
| Inspect pinned preview text and managed user strings for credential candidates | [Static package secret review](foundation-package-secret-review.md) |
| Configure a pinned local HTTPS listener | [Loopback certificate configuration and limits](foundation-loopback-https.md) |
| Review proprietary binary licensing and a separate source offering | [Non-binding commercial review draft](commercial-license-review-draft.md) |
| Provision private AKS or deliver an application with Helm | [AKS and Helm onboarding](../workloads/private-aks/README.md) |
| Try the local application | [Getting started](getting-started.md) → [Portal setup](local-portal.md) |
| Review the rebuilt portal experience and improvement priorities | [UI/UX review and phased plan](plans/portal-ui-ux-review.md) |
| Understand the suite | [Platform overview](platform-overview.md) → [Current status](completion-status.md) |
| Preview or deploy a workload | [Workload catalog](workload-catalog.md) → [ADO onboarding](self-service.md) → [Preview](deployment-preview.md) |
| Compare live infrastructure before and after a deployment | [Shared agentic IaC workflow](agentic-iac.md) |
| Configure stack identity and understand future named resource previews | [Stack naming and resource-preview implementation plan](plans/stack-naming-and-resource-previews.md) |
| Calculate and validate resource names offline | [Naming foundation and local commands](resource-naming.md) |
| Validate a new stack-instance draft or review workload naming bindings | [Draft registry](../self-service/instances/README.md) → [Workload binding review](plans/stack-naming-bindings.md) |
| Inspect nested properties and unresolved What-If evidence | [Nested discovery and qualification](nested-iac-evidence.md) |
| Discover networks and explain topology | [Network discovery](network-discovery-and-diagrams.md) → [Diagrams](portal-diagrams.md) → [Agent workflows](agent-workflows.md) |
| Connect Azure Bicep, Azure, ADO, GitHub, Fabric and Learn tools for orchestration | [MCP providers, credentials and exact plans](provider-mcp.md) → [Agent workflows](agent-workflows.md) |
| Verify parameterized AVM generation through AHP and Codex without deployment | [End-to-end results, fixes and offline checks](avm-end-to-end-validation.md) |
| Analyze or change tags | [Tag governance](tag-governance.md) → [Permissions](security-and-rbac.md) |
| Analyze DNS and compare observed/desired properties | [DNS and drift workspace](operations-workflows.md) → [Phased operations plan](plans/operations-workflow-suite.md) |
| Understand recovery | [Recovery rules](recovery-rules.md) → [State and recovery plan](plans/deployment-state-and-recovery.md) |
| Add a workload or skill | [Source conventions](repository-structure.md) → [Workflow standard](agent-workflow-standard.md) → [Blueprint](templates/agent-workflow-blueprint.md) |

## Understand the platform

| Guide | What it covers |
|---|---|
| [Platform overview](platform-overview.md) | Components, evidence flow, identities, capability suite and agent boundaries. |
| [Current implementation status](completion-status.md) | What is implemented, locally verified, observed in ADO and still awaiting live acceptance. |
| [Workload catalog](workload-catalog.md) | Eleven offerings, resource descriptions and exact pipeline entry points. |
| [Detailed catalog and method map](self-service-catalog.md) | Workload types, named instances, target contracts and original adapter methods. |
| [Enterprise platform design](enterprise-platform.md) | Ownership, governance and bounded workload support. |
| [Source conventions](repository-structure.md) | Module/composition placement, configuration, runtime files and test boundaries. |

## Develop and operate locally

| Guide | What it covers |
|---|---|
| [Getting started](getting-started.md) | Ordered installation, upgrade, verification and packaging commands. |
| [Local suite lifecycle](local-suite.md) | Telemetry-first start, isolated builds, protected stop/restart and optional Functions/Azurite profile. |
| [Portal setup and authentication](local-portal.md) | Local Azure CLI sign-in, optional Entra registration, Azure/ADO connections, configuration and Windows packages. |
| [Blob copy local development](local-development.md) | Functions/Azurite prerequisites, data, lifecycle and troubleshooting. |
| [Exact local workflow](local-workflow.md) | Blob upload, dispatcher, queue worker, ledger and timers. |
| [Dispatcher reference](dispatcher/README.md) | Methods, dependencies, settings and execution behavior. |
| [Blob copy architecture](architecture.md) | Transfer design and resource responsibilities. |
| [Operations and recovery](operations.md) | Blob copy reconciliation and operator procedures. |

## Configure Azure DevOps delivery

| Guide | What it covers |
|---|---|
| [Developer and platform onboarding](self-service.md) | Definitions, protected environments, permissions and self-service use. |
| [Bulk pipeline registration](ado-pipeline-registration.md) | Reviewed registration of the 25 root entry points, reuse and conflict handling. |
| [Pipeline flow](pipeline-flow.md) | Stages, script calls, shared templates and artifact handoffs. |
| [Deployment Preview](deployment-preview.md) | Bicep validation, Azure What-If, report artifacts and deployment gates. |
| [Deployment Stacks and Template Specs](deployment-stacks-upgrade.md) | Publication, stack ownership and lifecycle controls. |
| [Subscription discovery](subscription-discovery.md) | Saved manifests, naming, scope and static menu generation. |
| [Prerequisite resolution](prerequisite-resolution.md) | Event flow Create / Reuse / Manage / Blocked decisions. |
| [Workload onboarding](workload-onboarding.md) | Shared dependencies, identities and acceptance tests for the eleven products. |
| [Cost assumptions](self-service-costs.md) | Dated estimates, exclusions, freshness and missing-price behavior. |
| [Security and RBAC](security-and-rbac.md) | Identity separation, permissions, protected operations and data handling. |

## Discover, analyze and design

| Guide | What it covers |
|---|---|
| [Azure Skills library](azure-skill-discovery.md) | Pinned Microsoft skills, read-only discovery profiles and scope limitations. |
| [Network discovery and IPAM](network-discovery-and-diagrams.md) | Tenant/management-group collection, deterministic checks, AVNM setup and manual tests. |
| [Connected portal diagrams](portal-diagrams.md) | Observed topology, conceptual proposals and saved Azure Preview diagrams. |
| [Offline discovery analysis](self-service-analysis.md) | Saved inventory/manifest reports without cloud access or a model. |
| [Codex agent workflows](agent-workflows.md) | Ten explicit workflows, bounded AHP orchestration, scoped MCP evidence, identity and receipts. |
| [Connected MCP providers](provider-mcp.md) | Full six-provider catalogs, skills/instructions and support files, host-held credentials, accepted reads and human-approved ordered change plans. |
| [AVM module library](avm-module-library.md) | Six pinned AVM-backed source modules, seven typed child collections, parameterization, dependency locks and qualification boundaries. |
| [AI-assisted Bicep drafts](bicep-source-drafts.md) | Local module contracts, generated parameters, source downloads and qualification. |
| [Tag governance](tag-governance.md) | Resource/tag matrix, deterministic analysis, editable drafts and protected changes. |
| [Recovery rules](recovery-rules.md) | Seventeen policies, exclusions, portal views and non-authorizing offline checks. |

## Private analytics platform

[Manual data recipes](../workloads/private-data-platform/recipes.md): pre-bound ADF staging and native Job orchestration, a validate/curate/verify Databricks bundle, setup export and acceptance.

[Databricks Premium + Data Factory v2](../workloads/private-data-platform/README.md) documents exact resources, shared prerequisites, managed identities, endpoint approvals and the manual Azure acceptance sequence. See its [delivery plan](plans/private-data-platform.md) for deferred profiles.

## Extend the suite

| Reference or plan | Purpose |
|---|---|
| [Reusable modules](../modules/README.md) | Shared Bicep contracts and module-specific documentation. |
| [Workflow standard](agent-workflow-standard.md) and [blueprint](templates/agent-workflow-blueprint.md) | Repeatable evidence → assessment → optional AI → reviewed draft → governed execution. |
| [Expansion roadmap](self-service-expansion-plan.md) | Future capabilities and delivery priorities. |
| [Operations and drift workflow suite](plans/operations-workflow-suite.md) | Proposed Private DNS consolidation, identity/access, private diagnostics, data release promotion, AKS maintenance, pipeline analysis and three-way drift; shared contracts and phased acceptance. |
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

The [discovery test README and issue log](testing/README.md) records authenticated portal/ADO tests, reproducible bugs and setup blockers from 4 October 2026, including successful Discover runs on merged `main` and incomplete portal DNS coverage.

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
