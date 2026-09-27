# Bicep repository structure and authoring conventions

**Expansion placement:** five new `workloads/` compositions reuse `modules/security/key-vault`, `modules/messaging/service-bus`, `modules/monitoring/observability`, `modules/compute/private-functions` and existing Storage/Private Endpoint modules. `scripts/product-service-common.ps1` supplies the shared product adapter; `src/ProductFunctions` holds the two runtime samples and `tests/ProductFunctions.Tests` their tests. Catalog metadata describes seven allowlisted products; no arbitrary script/plugin dispatch was introduced. See [onboarding](workload-onboarding.md). The detailed original-product tree below remains applicable to its listed paths.

`vendor/azure-skills/` preserves a pinned Microsoft skill snapshot, nested guidance, supporting resources, licenses and `bundle.json` hashes. It is reference content for the portal, outside the active project `.agents/skills` instructions. `AzureDiscovery.cs` implements fixed read-only ARM adapters; vendor scripts and plugin hooks are not executed. See [bundle maintenance](azure-skill-discovery.md#updating-and-packaging-the-bundle).

The optional local UI lives in `src/SelfService.Portal/`, with `tests/SelfService.Portal.Tests/`, `tests/portal/` and `scripts/*-Portal.ps1`. It consumes the existing generated YAML/target/skill sources; it does not move Bicep compositions, introduce another workload registry or implement the planned MCP server. See the [portal structure and method map](local-portal.md#structure-methods-and-boundaries).

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
| [AVM resource module specification](https://azure.github.io/Azure-Verified-Modules/specs/bcp/res/) and [private endpoint example](https://github.com/Azure/bicep-registry-modules/tree/main/avm/res/network/private-endpoint) | Resource-oriented paths with `main.bicep`, documentation and tests; published AVM has additional packaging/versioning requirements. | Shared modules have category/resource directories and README interfaces. Compiled outputs stay in artifacts; this repo does not publish an AVM package. |
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
  workloads.json                         two allowlisted composition/package/phase contracts
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
  event-grid/topic/main.bicep             private custom topic
  event-grid/event-subscription/main.bicep queue delivery and dead lettering
  logic-app/standard/main.bicep           private Standard hosting
  network/private-endpoint/main.bicep     reusable endpoint + DNS zone group
  network/workload-vnet/main.bicep        workload VNet, subnets and integration NSG
  network/private-dns-zone/main.bicep     workload-owned zone and VNet link
  monitoring/log-analytics/main.bicep     workload-owned monitoring workspace
  storage/storage-account/main.bicep     reusable private account + children/diagnostics
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
- Prefer evaluating an existing AVM before inventing a new generic module. Any adoption must pin a reviewed version and compare names, scopes, defaults, RBAC, telemetry, dependencies and managed inventory. No AVM replacement or registry publication is claimed by this rearrangement.
- Shared modules are currently versioned with the repository. Template Spec versions are hashes of compiled stack content; metadata edits can therefore create a new version even when deployed resources are equivalent. Publishable modules need their own release/version process before external consumers depend on them.
- Keep compiled ARM, `.local`, dependency folders, credentials and execution evidence out of source. AVM's committed generated JSON convention belongs to its distribution repository, not a requirement for this consumer repository.

## Pipeline correctness checks

From the repository root, with PowerShell 7.4+, .NET from `global.json`, Bicep 0.47.16 (or the existing Azure CLI fallback), and Node.js 22+ with npm:

```powershell
./scripts/Update-ServiceCatalog.ps1 -Check
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
