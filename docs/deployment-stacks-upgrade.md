# Deployment Stacks and Template Specs upgrade

Current scope: both workloads use these stack/publication helpers. The implementation plan below records the delivered upgrade. Dedicated menus now use Preview/Deploy; the generic route retains six stages. Event flow can create workload-owned prerequisites while external shared resources remain referenced. See the [catalog](self-service-catalog.md) and [status](completion-status.md).

## Implementation plan

1. Add a subscription-scoped Bicep wrapper that owns the dedicated workload resource group and preserves existing resource names. Reference shared resources; never create shared topology through the wrapper.
2. Freeze stack configuration and the compiled wrapper into the qualified bundle. Derive the Template Spec version from its canonical content hash.
3. Publish from a protected stage, rejecting changes to an existing version. Separate publication credentials and deployment read credentials through platform configuration.
4. Validate the pinned content, use native stack What-If, reject destructive/uncertain changes, and recheck approval fingerprints before stack apply.
5. Preserve monotonic Foundation-to-Release ownership and reject implicit adoption of any existing unmanaged resource group. Record stack outputs and managed resource inventory.
6. Add offline contracts, compile checks and pipeline verification. Keep all targets disabled pending external setup and live acceptance.

All six implementation steps are complete locally. Azure acceptance remains pending. No Azure deployment or Template Spec publication was performed during this implementation.

The design follows Microsoft's [Deployment Stacks guidance](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks), [Template Specs guidance](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/template-specs), and [native stack What-If](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks-what-if). See the dated [local validation evidence](validation.md#deployment-stacks-upgrade-19-september-2026).

## Delivered flow

The dedicated Deploy roots and `azure-pipelines-self-service-deploy.yml` extend generated `pipelines/deploy-entry.yml`. Dedicated roots expose Preview/Deploy and allow disabled targets to consume discovery for Preview; only the generic compatibility root uses hosted SetupOnly. Private publication/deployment jobs are omitted for disabled targets and dedicated Preview-only selections.

Enabled targets execute **Qualify → PublishTemplate → PlanFoundation → ApplyFoundation → PlanRelease → ApplyRelease**. Discovery remains a separate read-only menu and required handoff.

The [pipeline flow reference](pipeline-flow.md) maps all script/artifact handoffs and the shared qualification/evidence refactor. Module registry publication is deferred; local modules are embedded during compilation.

Each workload's `stack.bicep` runs at subscription scope. The Blob copy wrapper is `workloads/blob-transfer/stack.bicep`; Event flow uses `workloads/logic-app-event-grid/stack.bicep`. It creates the dedicated workload RG and invokes `./main.bicep`, forwarding its parameters/outputs. Existing names remain stable. The resource-group composition moved from the repository root to `workloads/blob-transfer/main.bicep`; `Deploy.ps1` also compiles that source for legacy manual incremental deployment. Do not use the manual path to modify stack-managed instances. See the [source layout and migration](repository-structure.md).

The frozen bundle adds `stack-template.json` and `stack.json`; both are hashed in `bundle.json`. New pipeline bundles require `deploymentEngine: deploymentStack`. Legacy bundles cannot be silently applied by the new pipeline. Stack configuration must also match the reviewed checkout when the bundle is consumed.

## Publication and version reuse

`config/deployment-stack.json` defines the Template Spec subscription, catalog RG, name, region and literal publishing service connection/pool/environment. The checked-in connection is the existing `SC-AZ-A-Bicep`; it does not imply that publishing permissions or the catalog RG have been configured. Configure a separate publishing identity for least privilege before enterprise rollout.

The version is `sha256-<canonical compiled-template hash>`. An identical composition reuses the same version across environments. Parameters and the application ZIP remain separately hashed. Existing versions are read and verified, never overwritten by this workflow. A mismatched version is an error. Protect the publishing environment with an exclusive lock and restrict external writers; Azure itself permits version updates, so a hash-based name alone is not an Azure immutability guarantee.

This implements infrastructure-version reuse. It does not add a UI for arbitrary older versions or promote the same application ZIP between independent environment runs; application builds remain per run. Previous versions can be redeployed only through a separately reviewed source/configuration release and the same validation gates.

`Publish-WorkloadTemplate.ps1` validates manual main-branch provenance, exact publisher bindings and CLI capabilities before publication. The catalog RG is platform-owned and must already exist; workload deployment never creates it. Publication writes `template-publication/publication.json` and a success/failure receipt. Dedicated prepublication Preview uses the frozen local template. Subsequent published-template phase previews and apply verify the exact Template Spec against the frozen wrapper. Nested `templateLink` content is rejected to preserve preview coverage.

## Stack ownership and phase safety

- Stack ID: `/subscriptions/<target>/providers/Microsoft.Resources/deploymentStacks/stack-<workload>-<environment>`.
- Stack policy: `detachAll`, default `denyDelete`; only `none` and `denyDelete` are supported through reviewed platform configuration. No developer lifecycle switches.
- The workload RG and its resources are managed together. Existing shared DNS, subnets, resolver, workspace and destination storage are references, not stack-owned resources. Outside the workload RG, only the reviewed destination-container role grants and their named nested deployment record are admitted.
- A target RG that exists without the matching owned stack is rejected, even if empty. This prevents silent adoption and accidental changes to an existing team's resource group. Do **not** precreate the workload RG for this pipeline.
- Foundation initially creates storage/identity/monitoring infrastructure. Release uploads the immutable package and expands the same stack to include the runtime. Once the stack's persisted `deployFunctionApp` parameter is true, subsequent Foundation stages skip mutation even if the Function App was removed out of band. Release reconciles the complete desired state.
- Preview rejects Delete, Detach, Unsupported, potential changes, diagnostic-bearing/incomplete results, unexpected scope/ownership, lost deny protection and missing managed-resource coverage. The existing sensitive-property gate also inspects converted stack property deltas.
- Existing failed stacks, changed ownership tags, deny exclusions, lifecycle-policy drift and unhealthy managed resources require platform recovery. No bypass-stack-out-of-sync flag is used.

## Preview, apply and evidence

The backend calls `az stack sub validate`, then `az stack-whatif sub create` with Provider validation and machine-readable property changes. The adapter follows Microsoft's [2025-07-01 API schema](https://github.com/Azure/azure-rest-api-specs/blob/main/specification/resources/resource-manager/Microsoft.Resources/deploymentStacks/stable/2025-07-01/deploymentStacks.json), including `properties.changes.resourceChanges`, resource `id`, certainty and `resourceConfigurationChanges.delta`.

The raw response is retained as `stack-what-if.json`; normalized changes feed the existing 24-hour plan fingerprint. That fingerprint includes managed inventory, stored parameters/outputs, lifecycle settings and bundle/content hashes. Apply reruns the preview and rejects drift, then verifies the published content again before `az stack sub create`. No ordinary `deployment group create` is used for new pipeline bundles.

Preview creates a metadata resource but does not apply workload changes. The code uses `P1D`, compatible with the locally installed CLI's documented minimum, and explicitly deletes only that invocation's preview metadata in `finally`. If the agent terminates or cleanup permission fails, platform operators must inspect and clean retained `preview-*` result resources. Local evidence is retained independently. No workload-stack delete is issued.

Apply retains `stack-result.json`, `lifecycle.json`, existing `outputs.json`, plan/recheck evidence and receipts containing stack ID, Template Spec ID and template hash. Post-apply inventory must contain the approved resources and preserve prior membership. Only successful private connectivity, five indexed Functions and the three-request smoke test can produce Ready.

## Platform setup and exact local checks

1. Fill existing target parameters and select approved central networking; keep target `enabled: false` until setup is reviewed.
2. Configure `config/deployment-stack.json`. Precreate **only the catalog RG**, authorize its publisher, and allow the deployment identity to read Template Specs. A version reader also needs separate deployment permissions.
3. Grant the deployment identity the stack operations, preview-result operations, subscription-level RG creation, appropriate deny-setting management, workload resource/RBAC and approved cross-subscription join/read/grant permissions. Contributor alone is insufficient for every operation. Keep application developers from administering the enforcing stack.
4. Install Azure CLI **2.89.1 or newer**, PowerShell 7.4+, and private network connectivity on publishing/deployment agents. CLI 2.89.1 command/help support was checked locally; Azure execution has not been qualified. The entry scripts check required preview flags before mutation.
5. Configure ADO approvals, branch protection and exclusive locks for publishing/deployment environments. Configure the service connection Required Template check against **`pipelines/deploy-entry.yml`**. Protect that generated file, its generator, all scripts and platform configuration through repository review; `extends` alone does not establish permissions.
6. Run these commands from the repository root:

```powershell
pwsh -NoProfile -File scripts/Update-ServiceCatalog.ps1
pwsh -NoProfile -File scripts/Test-Project.ps1
pwsh -NoProfile -File scripts/Update-Manifest.ps1
pwsh -NoProfile -File scripts/Update-Manifest.ps1 -Check
```

After reviewed source is published, enable dev only, regenerate the catalog, run discovery on main and select its matching artifact in Deploy. Use a fresh dedicated workload RG name. Review publication, Foundation and Release approvals separately.

## Acceptance and recovery boundaries

Offline tests cover new publication/reuse/tamper rejection, first-stack creation, Foundation/Release sequencing, pinned-spec apply, package/smoke order, drift, failed-stack recovery rejection, lifecycle removals, uncertainty, incomplete inventory, shared-resource boundaries and deny weakening. Four parameter environments, the stack wrapper and both separate platform templates compile. Runtime tests are mocked for Azure; actual stack creation and Template Spec behavior still need live acceptance.

Before production, exercise create, unchanged rerun, ordinary update, rejected deletion, altered spec, permissions denial, failed apply and retry in a disposable subscription. Confirm the actual preview includes every expected resource and records no unsupported analysis. Validate deny behavior, RG protection, cross-subscription grants and policy effects.

Automatic brownfield adoption, recovery bypasses, destructive teardown and data migration are intentionally unavailable through developer self-service. Those operations need a separate reviewed platform migration runbook with retained inventory/backups; this upgrade does not claim to automate them. Keep unrelated resources out of the dedicated workload RG. Platform Policy assignments, ACR publication and AVM migration also remain external or separate work.
