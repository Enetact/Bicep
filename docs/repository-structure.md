# Bicep repository structure and authoring conventions

Revision 22 adds separate scoped recovery inventory/artifact/parser/rehearsal, live vault capture and metadata-only retention files under `src/SelfService.Foundation/`. [The guide](foundation-scoped-recovery.md) distinguishes signed retained structure from actual AES/HMAC authentication. Existing v1 contracts and SQL schema v8 are unchanged; no default route is added.

Revision 21 adds source absence-proof contracts/cryptography and a guarded inspection partial under `src/SelfService.Foundation/`. The explicit fresh source mode versions rollback binding/state while retaining default v1 serialization and kinds. [Source proofs](foundation-absence-scope-provenance.md) and [inspection](foundation-absence-scope-inspection.md) describe the executable scope and recovery gates. SQL schema v8 and existing signed-set/stage/review formats are unchanged.

Revision 20 adds dedicated credential-set checkpoint contracts/authority and a concrete checkpoint-backed stage executor under `src/SelfService.Foundation/`. [Checkpoint custody](foundation-credential-recovery-checkpoints.md) stores only bounded selection metadata, while [staging](foundation-credential-checkpoint-staging.md) reuses the existing signed-set/AES and inactive-output contracts. Existing operational guards recognize both new partial-root sentinels. SQL schema v8 and earlier wire formats are unchanged.

Revision 19 adds recovery-stage review contracts, a dedicated append-only schema v8 and transactional store, plus the held verification coordinator under `src/SelfService.Foundation/`. [Review](foundation-credential-recovery-review.md) and [execution](foundation-credential-recovery-review-execution.md) share the existing stage verifier and guarded storage. Frozen schema v1–v7 text/checksums remain unchanged; synthetic downgrade fixtures remove only newer test tables.

Revision 18 places exact credential recovery staging plans, executor, private stage storage and completed-output verifier under `src/SelfService.Foundation/`. Internal crypto helpers reuse the existing AES/AAD profile format and add bounded source profile/absence projections without changing the signed-set wire format or database schema. [Staging](foundation-credential-recovery-staging.md) and [verification](foundation-credential-recovery-stage-verification.md) explain the permanently held output. Existing operational sentinel arrays and their data-driven tests recognize partial stage guard/hold markers.

Revision 17 places signed encrypted credential recovery-set contracts, capture, retained-state validation and shared envelope authentication in `src/SelfService.Foundation/`. The explicit rehearsal API uses the same AES/AAD validation as live envelope reads while authenticating retained bytes with configured source-key handles. [Recovery-set format and capture](foundation-credential-recovery-set.md) and [rehearsal](foundation-credential-recovery-rehearsal.md) describe the private artifact/public report distinction. No default route, key store, database schema or source-namespace format is added.

**Managed DevOps Pool placement:** `modules/devops/managed-devops-pool/` is the bounded native resource module; `workloads/managed-devops-pool/` holds composition/stack/naming/environment source, README and a manual consumer canary. `scripts/managed-devops-pool-common.ps1` adapts the standard product flow, with four disabled `devopspool` targets. The module is excluded from generic AI source-draft emission. [Guide](../workloads/managed-devops-pool/README.md).

**Fabric source placement:** `workloads/fabric/` owns compositions, parameters, operation examples and its guide; `modules/fabric/` owns capacity, workspace Private Link and private ADLS examples; `self-service/fabric/` owns reviewed API manifests. `scripts/fabric-common.ps1` adapts standard Azure delivery; `fabric-workspace-common.ps1` and `Invoke-FabricWorkspace.ps1` own typed API operations. Fabric MCP uses the existing broker and provider guidance. [Design and acceptance](../workloads/fabric/README.md).

**17 October 2026 — AVM source library:** six project-owned wrappers under `modules/avm/` consume pinned official compiled ARM artifacts, OCI manifests and a complete MIT license under `vendor/azure-verified-modules/`. `config/avm-module-contracts.json` holds their expanded child schemas, origins and exact dependency closures separately from the 17 native contracts. The application validates and merges both catalogs: 23 inventoried modules, 18 selectable for source drafts. The nine registered workload compositions and deployment targets are unchanged. See [versions, child interfaces and qualification](avm-module-library.md).

**Operations source increment — 4 October 2026:** `src/SelfService.Operations/` owns typed contracts and deterministic DNS/drift rules; `src/SelfService.Operations.Cli/` and `scripts/operations/Analyze-Operations.ps1` reuse them offline. `src/SelfService.Portal/Operations/` owns authorized collection and session orchestration; `wwwroot/operations.mjs` owns the view. Two project skills provide bounded review instructions through the existing AHP/Codex runtime. Generated evidence stays in ignored `artifacts/portal-operations/`. Future domain/profile/pipeline folders are not created as placeholders. See [the guide](operations-workflows.md).

**4 October 2026 — shared IaC evidence:** the dedicated workload Deploy path now captures live before/What-If/after state using pinned Microsoft Azure MCP and scoped ARM reads, renders projected comparisons in the portal and supports an explicit AHP/Codex visualizer review. See [the component contract, identity boundaries, reuse steps and manual acceptance](agentic-iac.md). This does not implement restore execution or establish live Azure acceptance.


**AKS/Helm increment — 4 October 2026:** AKS implementation: `modules/compute/private-aks/` is a leaf resource module; `workloads/private-aks/` holds RG composition, subscription wrapper, environment examples and namespace onboarding; `charts/platform-app/` holds the independent application chart. `scripts/aks-common.ps1` supplies product adapter checks; `helm-common.ps1` and `Invoke-HelmDelivery.ps1` supply the application evidence/deployment contract. See [the implementation and onboarding guide](../workloads/private-aks/README.md).

**Private Data Platform:** leaf modules live under `modules/analytics/{databricks-workspace,data-factory}` and `modules/storage/data-lake`; composition, stack wrapper, environments and manually imported acceptance notebook live under `workloads/private-data-platform`. Adapter rules stay in `scripts/data-platform-common.ps1`; this does not add a .NET runtime service. See [module contracts](../modules/README.md).

[Wiki home](README.md) · [Platform overview](platform-overview.md) · [Status](completion-status.md)

**Recovery policy source:** `config/recovery-capabilities.json` defines descriptions/exclusions; `scripts/recovery-common.ps1` owns fixed mode/check mappings and deterministic offline assessment. `Test-RecoveryCandidate.ps1` is the source-checkout command; `Test-RecoveryRules.ps1` and `tests/portal/recovery.test.mjs` cover its rules/UI projection. Portal metadata and `wwwroot/recovery.mjs` present policies without execution. See [the recovery guide](recovery-rules.md).

**Bicep composition source:** `src/SelfService.Portal/BicepDrafts.cs` owns context projection, typed proposal validation and deterministic emission; `BicepParameterSchema.cs` resolves bounded compiled schemas into typed inputs, and `BicepAvmSources.cs` verifies exact AVM source closures. `.agents/skills/platform-bicep-composition/SKILL.md` owns model instructions. `scripts/Update-BicepModuleContracts.ps1` generates the native catalog and invokes `Update-AvmModuleContracts.ps1`, which verifies upstream artifacts before generating the separate AVM catalog. The updated portal exposes `GET /api/bicep/modules` and **Agent workflows** module details. Tests live in `tests/SelfService.Portal.Tests/BicepDraftTests.cs` and `scripts/Test-BicepDrafts.ps1`; isolated test build output is under `artifacts/bicep-draft-build/`. Draft copies live only in ignored `artifacts/portal-agents/<run>/source-draft/`; promotion follows [the draft guide](bicep-source-drafts.md).

**Tag governance update (27 September 2026):** subscription evidence, deterministic checks, structured Codex advice, editable drafts, source proposals and two manual ADO pipelines are implemented locally. Apply is disabled until onboarding and live acceptance. See [the tagging guide](tag-governance.md) for current methods, source structure, permissions and limits. Earlier dated verification records remain historical.


**Registration implementation:** `config/pipeline-registration.json` supplements the nine-product catalog with qualification, compatibility and network entries. `src/SelfService.Portal/PipelineRegistration.cs` owns inventory/review/create-only orchestration; `wwwroot/pipeline-setup.mjs` owns the setup view. Tests remain in `tests/SelfService.Portal.Tests/PipelineRegistrationTests.cs`; receipts are ignored artifacts. See [registration](ado-pipeline-registration.md).

**Network increment:** `EvidenceDiagram.cs`, `NetworkDiscovery.cs`, `NetworkAssessment.cs` and `NetworkPipeline.cs` own validation/collection/rules/ADO request contracts; `wwwroot/network.mjs` owns the network workspace. `modules/network/ipam-reservation.bicep` is reusable allocation infrastructure; `platform/network/` owns the separately operated spoke stack. Reviewed pools live in `config/network-allocation.json`; `azure-pipelines-network.yml` is its manual entry. See [network guide](network-discovery-and-diagrams.md).

**Expansion placement:** the original five-product expansion added `workloads/` compositions reusing `modules/security/key-vault`, `modules/messaging/service-bus`, `modules/monitoring/observability`, `modules/compute/private-functions` and existing Storage/Private Endpoint modules. `scripts/product-service-common.ps1` supplies the shared product adapter; `src/ProductFunctions` holds the two runtime samples and `tests/ProductFunctions.Tests` their tests. With Private Data Platform and private AKS, catalog metadata now describes nine allowlisted products; no arbitrary script/plugin dispatch was introduced. AVM wrappers add no product dispatch. See [onboarding](workload-onboarding.md). The detailed original-product tree below remains applicable to its listed paths.

`vendor/azure-skills/` preserves a pinned Microsoft skill snapshot, nested guidance, supporting resources, licenses and `bundle.json` hashes. It is reference content for the portal, outside the active project `.agents/skills` instructions. `AzureDiscovery.cs` implements fixed read-only ARM adapters; vendor scripts and plugin hooks are not executed. See [bundle maintenance](azure-skill-discovery.md#updating-and-packaging-the-bundle).

The optional local UI lives in `src/SelfService.Portal/`, with `tests/SelfService.Portal.Tests/`, `tests/portal/` and `scripts/*-Portal.ps1`. It consumes the existing generated YAML/target/skill sources without moving Bicep compositions or introducing another workload registry. `AgentWorkflows`, `AgentPolicy`, `CodexAgentRuntime`, `AgentMcpBridge` and `AgentHostChannel` implement the bounded local [Codex/MCP review path](agent-workflows.md); the broader enterprise facade remains proposed. See the [portal structure and method map](local-portal.md#structure-methods-and-boundaries).

## Source, runtime state and test boundaries

Revision 12 adds `FoundationCredentialMaintenanceContracts.cs`, `FoundationCredentialMaintenanceStore.cs`, `FoundationCredentialMaintenanceExecutor.cs` and the private `CredentialVaultMaintenance.cs` session. Existing human roles and validated customer contexts govern local revoke/rewrap, with separate exact approval. Schema v6 adds three dedicated maintenance tables and immutable admission/result guards; restore quarantines unfinished authority. [Maintenance](foundation-credential-maintenance.md) and [evidence](foundation-credential-maintenance-evidence.md) describe the typed records and synthetic verification. No new product registry, model tool, provider transport or default worker operation is introduced.

Revision 13 adds `FoundationCredentialMaintenanceObservationContracts.cs`, `FoundationCredentialMaintenanceObservationStore.cs`, `FoundationCredentialMaintenanceObservationRecorder.cs` and the private `CredentialVaultObservation.cs` session. Schema v7 appends observation requests, independent approvals and immutable current-publication receipts. Existing maintenance roles/capabilities and read-only custody guards apply; the original uncertain execution remains unchanged. [Observation](foundation-maintenance-observation.md) and [evidence](foundation-maintenance-observation-evidence.md) cover the trusted composition and restore boundary. The shared synthetic downgrade fixture removes only newer test tables before constructing legacy snapshots; production upgrades retain history.

Revision 14 strengthens existing operational code: `FoundationCellActivationAuthority` reuses native private reservation primitives from `CredentialNamespaceCustody`, preview installation shares the known custody-ancestor refusal boundary, and backup admission compares required versioned schema structures before staging. The v1 schema batch is mechanically extracted into a private constant for the in-memory reference; migration SQL and checksums remain unchanged. [Activation](foundation-cell-activation.md), [preview staging](foundation-preview-installation.md) and [signed backups](foundation-signed-backups.md) describe the actual entry points. No new registry, schema version, model tool or live transport is introduced.

Revision 15 adds immutable recovery inventory contracts, a read-only `CredentialVault` partial and a pure retention assessor. [Inventory](foundation-credential-recovery-inventory.md) and [retention](foundation-credential-recovery-retention.md) explain authenticated current dependencies, explicitly supplied historical metadata, bounded sorted commitments and unconditional refusal to infer retirement authority. Existing custody/key-provider primitives are reused; no parallel credential store, schema version, public parser, provider adapter or runtime registration is added.

Revision 16 adds the protected local `FoundationBackupCheckpointAuthority` and concrete `FoundationBackupCheckpointRecovery` composition. [Checkpoint custody](foundation-backup-checkpoint-custody.md) and [recovery](foundation-backup-checkpoint-recovery.md) describe fixed signer/root identity, guarded checkpoint history, exact next-candidate acceptance and use of the retained latest checkpoint during staged restore. Existing activation, credential, Owner, worker receipt and preview guards recognize the new partial-root guard sentinel. The existing signed-backup verifier, native private reservation and recovery holds are reused; no database schema, credential store or default runtime route is added.

Revision 11 adds `CredentialRollbackAuthority.cs` for an explicitly provisioned fenced vault and a separate local authority, without changing SQLite schema v5 or adopting existing namespaces. Owner command inputs retain a bounded read handle and validate file/parent write custody before operations; result creation rechecks its parent. Worker receipt guards also protect partial `.rollback.lock` authority roots. [Credential rollback guidance](foundation-credential-rollback.md) records the fresh-only contract, interrupted-write reconciliation and local trust limits. Final archive static secret inspection remains a separate development-evidence operation, with its coverage limits retained in the release record.

Revision 10 adds `CredentialNamespaceCustody.cs` and explicit `CredentialVault.Provision`/`CredentialWindowsUserKeyProvider.ProvisionNamespace` APIs; normal constructors open existing custody only. `WorkerReceiptFile.cs` protects fresh receipt reservation/publication before database access; the existing launcher invokes its shared `--validate-config` policy before private log creation. Owner output/mutation guards also preserve credential namespaces. `FoundationReleaseExecutionEvidence.ps1` optionally binds commercial qualification to a pinned actual Foundation run and exact reviewed status-only deltas; its contract tests live in `tests/release/foundation-release-execution.tests.ps1`. Existing caches are not migrated and live execution remains disabled.

Revision 9 adds `FoundationRecoveryReconciliationStore.cs` and restricted host recovery routes for current human Operators, `CredentialVault.RewrapAsync` for one-profile custody maintenance, and `FoundationCredentialDispatchPermitSource`/`FoundationCredentialDispatchStore.cs` for current durable authorization. `FoundationTestExecutionEvidence.ps1` supports the fixed `Test-Foundation.ps1` runner and fresh source/TRX receipts under `artifacts/foundation-test-executions/`; its synthetic contracts live in `tests/release/foundation-test-execution.tests.ps1`. These are foundation changes, without a new product registry, live transport option or customer UI.

`src/SelfService.Foundation/`, `src/SelfService.Foundation.Host/`, `src/SelfService.Foundation.Worker/`, `src/SelfService.Foundation.Owner/`, `tests/SelfService.Foundation.Tests/` and `scripts/Test-Foundation.ps1` form the separately startable, disconnected dedicated-customer foundation. The Foundation-only owner entry point supplies trusted local setup/verification/staging/activation and public Entra proposals. `Run-FoundationWorker.ps1` and `Test-FoundationWorker.ps1` provide owned worker lifecycle/process qualification. `Manage-FoundationPreviewInstallation.ps1` and `tests/release/foundation-installation.tests.ps1` stage/verify/repair pinned software generations and test runtime prerequisites without selecting or launching an active version. `examples/customer-pipelines/` remains inert validation-only source; no automatic pipeline or target is enabled. The existing portal remains independently hosted. See [the service guide](foundation-service.md).

The customer service references existing portal emitter/MCP/Codex classes; it adds rotating identity keys, encrypted custody, schema/access/lifecycle migrations, signed audit/backup commitments, isolated model homes and cell metrics. Schema v5 stores database instance identities; `FoundationCellActivationAuthority` leases coordinate local recovery through a separately protected authority directory. `FoundationLoopbackTls` selects an optional existing pinned certificate. `Test-FoundationLoad.ps1` measures fixed local regression targets. `Publish-Foundation.ps1`, `New-FoundationDependencyEvidence.ps1`, `Test-CommercialRelease.ps1`, `Test-CommercialSourceSecrets.ps1` and `tests/release/` provide LocalPreview packaging and mechanical source/evidence qualification. `vendor/foundation-package-notices/` retains exact upstream license bytes/provenance, checked by `Test-FoundationPackageNoticePins.ps1`. `config/commercial-release-policy.draft.json` remains a non-binding business direction. Runtime databases/keys, test custody, scan staging and packages stay under ignored state/artifact directories.

| Location | Purpose and Git policy |
|---|---|
| `src/SelfService.Portal/*.cs` | Real application endpoints, identities, readers, Codex host, AHP coordination and MCP bridge. Included by the SDK project in Release/publish builds. |
| `src/SelfService.Portal/wwwroot/` | Real portal UI, agent controls and deterministic topology renderer. No fixture-server dependency. |
| `scripts/analysis/` | Shared production offline analyzer invoked by the portal and command-line wrapper; test fixtures live separately. |
| `config/`, `self-service/`, `schemas/`, root YAML and `pipelines/` | Versioned catalog, contracts, targets and deployment routing. Local credentials do not belong here. |
| `workloads/`, `modules/`, `environments/`, `platform/` | Versioned workload compositions, reusable Bicep modules, environment inputs and separately operated platform infrastructure. |
| `scripts/*DataPlatform*.ps1`, `scripts/data-platform-common.ps1`, `workloads/private-data-platform/databricks/` and `workloads/private-data-platform/notebooks/` | Keep reusable PowerShell/Python scripts, notebook source, sample input and `bundle.template.json` in Git. ADF pipeline/linked-service definitions are source in `modules/analytics/data-factory/main.bicep`. Generated deployment-bound recipe exports belong in ignored `artifacts/data-platform/recipes/`. |
| `.agents/skills/`, `vendor/azure-skills/` | Versioned project instructions and pinned Microsoft guidance/support files. Upstream scripts and hooks are not automatically enabled. |
| `modules/avm/`, `vendor/azure-verified-modules/`, `config/avm-module-contracts.json` | Project-owned Bicep wrappers, exact upstream compiled ARM/OCI artifacts plus MIT license, and generated reviewed source closures. These pinned distribution dependencies are intentionally versioned; they are separate from locally generated compilation output. |
| `tests/` | Versioned test code and synthetic fixtures. Never a production application dependency. The optional diagram fixture server runs separately on 5098; normal startup uses 5087. |
| `.local/`, `.tools/`, `.azure/`, `artifacts/`, `bin/`, `obj/` | Ignored runtime state, tools, credentials, generated reports and build/package output. Generated `SKILL.md` copies in a Codex runtime home are not authoritative source. |

Run `./scripts/Test-RepositoryHygiene.ps1` before a source commit. It checks representative private/generated paths, real first-party source tracking eligibility, mistakenly tracked ignored files, portal test-path references and any available Release dependency graph. It does not stage/remove files, read token values, run inference or certify repository history as secret-free. New source shown by `git status --short` as `??` must still be included in the intended commit. Keep sanitized example settings and dependency lock files versioned; do not ignore `.codex/` wholesale because it can contain project instructions and configuration.

Python environments and caches (`.venv/`, `__pycache__/`, `.pytest_cache/`, `.ruff_cache/`), notebook checkpoints, Databricks deployment state (`.databricks/`) and `.databrickscfg` credentials are ignored. This does not ignore the normal `databricks/` source directory, `.py`, `.sql`, `.ipynb`, Bicep, YAML or the reviewed JSON bundle template. The hygiene check covers these source formats and representative runtime paths independently.

The agent integration uses real application code and an in-process MCP server; it does not require a test harness or a separate external MCP daemon. Its functional limits remain explicit: four bounded reviews, AHP coordination only, and broader Azure MCP/tenant networking work still proposed. HTTP API and Service Bus worker remain documented starter workload samples rather than completed business applications. Source placement does not establish live provider acceptance.

Reviewed against Microsoft Learn and Microsoft's Bicep/AVM repositories on 19 September 2026. This is a workload deployment repository containing its application, not the Bicep compiler or an AVM publishing repository.

## What Microsoft recommends, and what we choose

Microsoft documents composable modules, meaningful parameters, explicit scopes and consistent authoring. The reviewed guidance does not mandate a universal Clean Architecture or vertical-slice folder tree for Bicep. Our choice is **workload composition plus reusable resource modules**, separated from independently operated platform infrastructure. This keeps each workload's deployment contract together while allowing genuine resource reuse.

| Source | Relevant guidance or observed convention | Application here |
|---|---|---|
| [Bicep best practices](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/best-practices) | Clear names, useful descriptions, parameter constraints, implicit dependencies, existing references and safe outputs. | Preserve deterministic resource names, security settings and references; document module interfaces and enforce selected linter rules. |
| [Bicep modules](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/modules) | Compose related resources through modules; use explicit versions for registry references; avoid colliding module deployment names during concurrent runs. | Shared storage/endpoint modules; workload-specific helpers; serialized protected deployments for stable instance names. |
| [Bicep parameter files](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/parameter-files) | Separate environment values from templates and identify the environment in parameter filenames. Values are plain text, so secrets need a secure source. | `environments/main.dev.bicepparam` and equivalents sit beside their workload; no secrets belong there. |
| [Bicep configuration](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/bicep-config) | The nearest parent `bicepconfig.json` determines authoring rules. | One root configuration governs workloads, shared modules and platform templates. |
| [Microsoft's public module repository](https://github.com/Azure/bicep-registry-modules) and [AVM classifications](https://azure.github.io/Azure-Verified-Modules/specs/shared/module-classifications/print.html) | Separate resource, pattern and utility modules. | Resource-oriented `modules/`; workload patterns in `workloads/`. We do not label custom modules as AVM. |
| [AVM resource module specification](https://azure.github.io/Azure-Verified-Modules/specs/bcp/res/) and [private endpoint example](https://github.com/Azure/bicep-registry-modules/tree/main/avm/res/network/private-endpoint) | Resource-oriented paths with `main.bicep`, documentation and tests; published AVM has additional packaging/versioning requirements. | Project wrappers constrain six pinned official AVM artifacts. Locally compiled output stays in artifacts; exact upstream distribution layers are versioned under `vendor/azure-verified-modules/`. This repo does not publish an AVM package. |
| [ADO templates](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/templates?view=azure-devops) | Reuse templates and use `extends` to constrain pipeline structure. | Stable root entry points, generated reviewed routing and protected shared stages; resource checks remain external ADO setup. |

Clean Architecture remains an application design choice for `src/`; it is not a reason to introduce domain/application/infrastructure layers inside declarative resource definitions. A workload directory resembles a vertical slice in ownership, but cross-workload resource modules remain shared. The [Azure/bicep repository](https://github.com/Azure/bicep) contains the language/compiler/tooling implementation and should not be copied as an infrastructure deployment layout.

## Current tree

```text
azure-pipelines.yml                       manual build, tests and package
azure-pipelines-{blobcopy,eventflow}-discover.yml  workload-specific Discover menus
azure-pipelines-{blobcopy,eventflow}-deploy.yml    workload-specific Deploy menus
azure-pipelines-self-service.yml          legacy generic Discover menu
azure-pipelines-self-service-deploy.yml   legacy generic Deploy menu
bicepconfig.json                          common lint rules
.editorconfig                            source formatting and LF endings
config/
  platform.json                          blob-transfer defaults, region and topology policy
  workloads.json                         nine allowlisted composition/package/phase contracts
  bicep-module-contracts.json             17 native module interfaces
  avm-module-contracts.json               six AVM wrapper interfaces and pinned source closures
  deployment-stack.json                  lifecycle policy and publishing bindings
  logic-prerequisites.json               Event flow prerequisite allocations and policy
workloads/blob-transfer/
  main.bicep                             resource-group composition
  stack.bicep                            subscription stack: workload RG + composition
  environments/main.{dev,qa,uat,prod}.bicepparam
  modules/                               workload-specific Function, monitoring,
                                         isolated network and scoped access helpers
  request.schema.json                    developer request shape
  request.example.json
  README.md
workloads/logic-app-event-grid/
  main.bicep, stack.bicep                 second composition and subscription wrapper
  environments/                          four parameter profiles
  modules/                               prerequisites, event/runtime storage and scoped access
  event.schema.json, request.schema.json  message and developer contracts
  README.md                              requirements, methods, onboarding, operations
src/LogicAppEventFlow/                    separately packaged Standard workflow files
modules/
  avm/{identity,containers,configuration,monitoring,network}/
                                         six constrained AVM-backed source-draft wrappers
  event-grid/topic/main.bicep             private custom topic
  event-grid/event-subscription/main.bicep queue delivery and dead lettering
  logic-app/standard/main.bicep           private Standard hosting
  network/private-endpoint/main.bicep     reusable endpoint + DNS zone group
  network/workload-vnet/main.bicep        workload VNet, subnets and integration NSG
  network/private-dns-zone/main.bicep     workload-owned zone and VNet link
  monitoring/log-analytics/main.bicep     workload-owned monitoring workspace
  storage/storage-account/main.bicep     reusable private account + children/diagnostics
vendor/azure-verified-modules/
  <module-id>/main.json                  exact official compiled ARM layer, including children
  <module-id>/manifest.json              exact official OCI manifest
  bundle.json, LICENSE                  pinned GitHub/MCR provenance, hashes and full MIT license
platform/
  policy/guardrails.bicep                 separately operated policy definitions
  registry/main.bicep                     separately operated private registry
self-service/
  targets/*.json                         reviewed instance bindings and overrides
  pipeline-settings.json                 discovery definition name
  pricing/*.json                         dated reference prices
pipelines/
  deploy-entry.yml                       generated Required Template entry
  catalog-bindings.yml                   generated literal protected-resource mapping
  templates/                             discovery, setup, qualify, publish, plan/apply
    steps/                               shared qualification, cleanup and evidence
scripts/                                 setup, qualification and lifecycle commands
  analysis/                              pure saved-evidence analysis and rendering, Node 22+
  Verify-AvmSources.ps1                   offline upstream artifact and inline-template checks
  Update-AvmModuleContracts.ps1           six reviewed wrapper contracts and dependency closure
schemas/analysis/                         versioned offline analysis output contract
tests/analysis/                           synthetic compatibility/reporting cases
.agents/skills/                          project documentation and offline review guidance
tests/infrastructure/                    locked YAML parser + pipeline/Bicep contracts
tests/BlobTransfer.Tests/                 application and opt-in emulator tests
src/                                     Functions and operator tool
docs/                                    architecture, runbooks and evidence
artifacts/                               ignored compiler output, bundles and receipts
```

## Ownership and configuration precedence

The [analysis component](self-service-analysis.md) extends this layout without relocating Bicep. `scripts/analysis/core.mjs` is pure and shared by CLI/pipeline adapters; `render.mjs` only produces projected report text. Filesystem access stays in `cli.mjs`. Future MCP hosting belongs in a separate `src/` project after runtime selection, and must reuse tested contracts rather than fork analysis rules. `platform/` remains independently operated infrastructure. Tenant reports stay under ignored `artifacts/`, not beside reusable module source.

The developer selects pattern, registered workload, environment and region. `config/platform.json` constrains those choices; `self-service/targets/*.json` resolves the service connection, subscription, private agent, protected environment, parameter file and approved overrides. Bicep supplies defaults, the selected `.bicepparam` supplies environment values, and the validated target/platform settings are overlaid before qualification. The deployment phase sets `deployFunctionApp` and the immutable package name. The bundle freezes the effective parameters and reviewed lifecycle configuration.

`config/deployment-stack.json` is platform policy, not another environment parameter file: it holds publication and stack lifecycle bindings. `stack.bicep` owns the dedicated workload RG and calls `./main.bicep`; it does not own central networks, DNS, resolver, shared workspace or external destination storage. Event flow can create its own missing VNet/subnets, DNS zones/links and workspace through `modules/prerequisites.bicep`; shared resources still remain outside stack ownership. `config/logic-prerequisites.json` holds reviewed environment address allocations and resolver policy. Stack state lives in Azure and lifecycle receipts, not in a checked-in state file. See [the stack runbook](deployment-stacks-upgrade.md).

`workloads/blob-transfer/modules/` is intentionally local to the pattern: Function settings, blob-transfer alert queries, source/package access grants and the five-zone isolated-network exception are not generic infrastructure building blocks. Shared modules own their resource and related child/diagnostic resources; they never load environment files or target JSON. Promote a helper into `modules/` only when another composition can use the same explicit interface without workload assumptions.

## Authoring and dependency rules

- Keep entry points descriptive and relatively thin; declare scope, metadata, parameters, variables, resources/modules and outputs clearly. Bicep dependency references control execution, not declaration order.
- Pass names, location, tags and approved existing IDs into modules. Keep environment-specific subscription IDs, identities, CIDRs and SKUs in reviewed configuration rather than reusable modules.
- Use symbolic references and `parent`/`existing` resources where practical. Keep existing explicit dependencies that order RBAC before runtime deployment. Do not rename resource symbols, deployment names or role GUID inputs merely to rearrange folders.
- Keep current supported APIs and assess upgrades with compilation, What-If and live tests. This structural refactor deliberately does not change API versions, SKUs, data retention or network policy.
- Root lint fails on unused parameters/variables, insecure secure-parameter usage and secret-bearing outputs. Existing parameter constraints remain part of the platform contract. Formatting is editor-guided; lint/compile is enforced.
- Prefer evaluating an existing AVM before inventing a new generic module. Adoption must pin a reviewed version and compare names, scopes, defaults, RBAC, telemetry, dependencies, managed inventory and cost. Six wrappers now consume pinned official AVM compiled dependencies; existing native workload modules were not replaced, and no registry publication is claimed. Follow [the AVM maintenance contract](avm-module-library.md#source-ownership-and-maintenance).
- Shared modules are currently versioned with the repository. Template Spec versions are hashes of compiled stack content; metadata edits can therefore create a new version even when deployed resources are equivalent. Publishable modules need their own release/version process before external consumers depend on them.
- Keep locally compiled ARM, `.local`, restored dependency folders, credentials and execution evidence out of source. The exact official OCI manifests and compiled AVM layers under `vendor/azure-verified-modules/` are a deliberate distribution-dependency exception, verified byte-for-byte and copied with provenance/license when selected in a draft. Never edit them as project-owned infrastructure source.

## Pipeline correctness checks

From the repository root, with PowerShell 7.4+, .NET from `global.json`, Bicep 0.47.16 (or the existing Azure CLI fallback), and Node.js 22+ with npm:

```powershell
./scripts/Update-ServiceCatalog.ps1 -Check
./scripts/Verify-AvmSources.ps1
./scripts/Update-BicepModuleContracts.ps1 -Check
./scripts/Test-BicepDrafts.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

`Test-Project.ps1` compiles four environments for each of the two patterns plus their stack/platform templates, runs existing offline contracts, and calls `Test-PipelineStructure.ps1`. The latter restores only the locked YAML test dependency using `npm ci --ignore-scripts`, then parses all pipeline YAML, checks template/script paths, parameter bindings, discovery routing, disabled/enabled stage contracts, protected-resource bindings and the artifact chain. It checks that the subscription wrapper forwards every composition parameter/output. The build pipeline now installs Node before these checks; Deploy qualification already does so.

This local verifier supports only the template-expression forms used here and fails on unsupported expressions. It is not the ADO server compiler. Supplied ADO evidence establishes discovery and Preview preparation for specific runs, not complete platform acceptance. Authorization/checks for every route, private-agent capacity, Template Spec publication, successful Azure What-If and private runtime acceptance remain to be verified. Targets stay disabled until that acceptance is performed.

## Path migration

| Previous source path | Current source path |
|---|---|
| `main.bicep` | `workloads/blob-transfer/main.bicep` |
| `environments/dev.bicepparam` (and peers) | `workloads/blob-transfer/environments/main.dev.bicepparam` (and peers) |
| `modules/storage.bicep` | `modules/storage/storage-account/main.bicep` |
| `modules/private-endpoint.bicep` | `modules/network/private-endpoint/main.bicep` |
| `modules/{function-app,monitoring,network,storage-access,destination-access}.bicep` | `workloads/blob-transfer/modules/<same-name>.bicep` |

Existing ADO definitions keep their root YAML paths. Scripts and checked-in target profiles use the new paths. Update any separately maintained profiles or direct Bicep invocations before running them. No compatibility wrapper was added at the old root path because another nested deployment would change the deployment graph. Source moves do not authorize adopting an existing RG; the original stack ownership guards still apply.

The validation report records the comparison with compiled pre-refactor templates. Historical screenshots/diagrams and dated assessments can still show old paths; this document and the current source are authoritative for navigation.
