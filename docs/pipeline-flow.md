# Pipeline flow and refactor

Three manual Azure DevOps definitions use the same GitHub checkout. Discovery supplies verified inventory. Deploy freezes its own release, publishes the compiled infrastructure as a Template Spec, then manages the workload through a Deployment Stack. Reusable Bicep modules remain local to this repository; no module registry is required.

## Entry points

| ADO definition | YAML path | Purpose |
|---|---|---|
| Build / Validate | `/azure-pipelines.yml` | Compile, test, exercise local runtime and package; no Azure deployment. |
| Discover (`Enetact.Bicep`) | `/azure-pipelines-self-service.yml` | Read selected subscription scope and publish `wosubscription-discovery`. |
| Deploy | `/azure-pipelines-self-service-deploy.yml` | Select workload type/name, environment and region; choose the discovery run under **Resources > discovery**. |

Push, PR and discovery-completion triggers remain disabled. Deploy downloads the exact selected discovery run. The standalone Build artifact is for validation/inspection; Deploy does not consume or promote its application ZIP.

The native Resources picker operates before queueing. A discovery artifact cannot populate a new interactive form midway through an executing YAML pipeline. Platform owners review inventory, update approved target configuration and regenerate the catalog. See Microsoft's [pipeline resource picker](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/resources?view=azure-devops#manual-resource-version-picker) and our [discovery handoff guide](subscription-discovery.md).

**All eight checked-in targets remain disabled.** Deploy currently runs hosted `SetupOnly` and publishes `setup-guidance`. It does not download discovery, publish a Template Spec or deploy a stack. Successful setup checks do not establish Azure readiness.

## Workload routing

`config/workloads.json` identifies both patterns and their allowlisted composition/wrapper/package/phase contracts. Blobcopy keeps the Function qualifier and phase parameter `deployFunctionApp`. Eventflow uses `qualify-logic-app.yml`, deterministic Standard workflow packaging and `releaseActivated`. Its schema-2 discovery/bundle cannot be substituted with blobcopy artifacts.

Eventflow Qualify compiles/tests but does not run a local Logic Apps host. In ApplyRelease it applies the stack first, then deploys/compares workflow files, verifies indexing and waits for a matching synthetic receipt. Blobcopy retains its original Function deployment and three-request smoke path. Both use the six stages below; the detailed action rows describe blobcopy unless otherwise stated. See the [Event Flow action/method map](../workloads/logic-app-event-grid/README.md). The standalone Build also packages both applications.

## Full enabled flow

```mermaid
flowchart TD
    G[Reviewed GitHub main] --> D[Discover: scoped inventory]
    D --> I[subscription-discovery artifact]
    I --> M[Deploy menu: choose matching discovery run]
    M --> E{Target enabled?}
    E -->|No| S[Hosted SetupOnly and setup-guidance]
    E -->|Yes| Q[Qualify: verify handoff, test and freeze bundle]
    G --> Q
    Q --> P[Protected PublishTemplate: publish or verify version]
    P --> PF[PlanFoundation: validate and preview]
    PF --> AF[Protected ApplyFoundation: recheck and apply]
    AF --> PR[PlanRelease: preview runtime changes]
    PR --> AR[Protected ApplyRelease: package, stack and smoke]
    AR --> R[Release receipt: Ready only after checks pass]
    B[Standalone Build: shared qualification steps] --> V[Package and validation evidence]
```

| Stage | Implementation and actions | Artifact | Agent / gate |
|---|---|---|---|
| Discover | `Export-DeploymentInventory.ps1`: scoped subscription/network/DNS and optional ADO inventory; distinguishes empty from unknown results. | `subscription-discovery` | Hosted Windows; authorized discovery connection. |
| Qualify | `Test-DiscoveryHandoff.ps1` verifies manifest, source run, scope and freshness. Shared qualification compiles/tests, runs recovery and local-host smoke. `Build-Package.ps1` packages the app; `New-SelfServiceBundle.ps1` freezes parameters, costs, discovery, application and stack template hashes. | `self-service-bundle`, `self-service-tests` | Hosted Windows; enabled target, manual main, valid handoff. |
| PublishTemplate | `Publish-WorkloadTemplate.ps1` checks provenance and publisher bindings; publishes or verifies the exact content-hashed Template Spec version. | `template-publication` | Private publisher pool; protected publication environment. |
| PlanFoundation | `Invoke-SelfService.ps1 -Action PlanFoundation` validates prerequisites, ownership, published content and native stack What-If; saves approval fingerprint. | `plan-Foundation` | Private deployment pool; deployment connection. |
| ApplyFoundation | `Invoke-SelfService.ps1 -Action ApplyFoundation` rechecks the saved plan and applies initial foundation. An already released stack skips foundation mutation. | `result-Foundation` | Protected deployment environment, approval and exclusive lock. |
| PlanRelease | `Invoke-SelfService.ps1 -Action PlanRelease` previews the complete runtime-enabled stack and freezes its fingerprint. | `plan-Release` | Private deployment pool; deployment connection. |
| ApplyRelease | `Invoke-SelfService.ps1 -Action ApplyRelease` rechecks the approved plan, uploads/verifies the package, applies the same stack and checks private connectivity, five indexed Functions and three smoke requests. | `result-Release` | Protected deployment environment, approval and exclusive lock. |

Every enabled stage depends on its predecessor succeeding. Apply consumes the matching current-run plan and bundle. A failed/missing plan cannot authorize apply. Plan stages write temporary Azure preview metadata; they differ from read-only Discover. See [stack ownership, preview cleanup and recovery](deployment-stacks-upgrade.md).

## Source and deployment responsibilities

```text
modules/*/main.bicep                       reusable resource implementations
workloads/blob-transfer/modules/*.bicep    workload-specific compositions
               ^ local relative references
workloads/blob-transfer/main.bicep         resource-group composition
workloads/blob-transfer/stack.bicep        subscription wrapper and owned RG
               | compile during Qualify
self-service-bundle/stack-template.json   embedded ARM templates
               | protected publication
Template Spec / sha256-<content hash>      pinned infrastructure version
               | preview and approved apply
Subscription Deployment Stack            tracks dedicated workload resources
```

The Template Spec packages the compiled composition; the Deployment Stack tracks ownership and lifecycle. Neither needs to clone GitHub or fetch source modules at deployment time. `platform/registry/main.bicep` remains a deferred source template compiled by project checks. There is no ACR deployment, module publication or registry dependency in this flow.

Environment values live in `workloads/blob-transfer/environments/`; platform policy in `config/platform.json`; publication/lifecycle settings in `config/deployment-stack.json`; approved bindings in `self-service/targets/`. The catalog generator owns both self-service roots, `pipelines/deploy-entry.yml` and `pipelines/catalog-bindings.yml`. Change their source configuration and regenerate instead of hand-editing generated routing.

## Refactor delivered

- `pipelines/templates/steps/qualify-application.yml` supplies identical tool installation, contracts, recovery, local-host checks, cleanup and packaging to Build and Deploy. Separate task names pinpoint failures. Packaging retains the default success condition.
- Local-service cleanup is a dedicated `always()` step, with a five-minute cancellation allowance. It stops only services recorded as owned by local mode. Cancellation before the job starts or agent loss can prevent cleanup/evidence; these steps are best effort.
- `steps/publish-qualification.yml` collects all available project, tooling, self-service, discovery, cost, platform, stack, recovery and local-host evidence. Missing test files before tests start no longer cause a second failure; failed tests still fail the run. Build's `validation-evidence` groups results under those folders, including `project/unit.trx`. Its `blob-transfer` and optional `local-host-evidence` names remain stable.
- `steps/prepare-stage-evidence.yml` creates run/stage/attempt/commit context before downloads and Azure sign-in in publication, preview and apply jobs. Publication now cleans its workspace too. Failure/cancellation uploads require a successfully prepared directory, avoiding an additional missing-directory error.
- Disabled setup guidance now covers the Template Spec catalog, publishing identity/environment, external checks, fresh stack-owned RG and local-module model. It is retained as `setup-guidance`.

This follows Microsoft's [include and extends templates](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/templates?view=azure-devops). `deploy-entry.yml` remains the Required Template entry. Cleanup/evidence follow [ADO condition and cancellation semantics](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/conditions?view=azure-devops); deployment actions retain success dependencies.

## Inspecting a run

1. Open the first failed task and its log. Qualification actions now have separate names.
2. Inspect artifacts. `pipeline-context.json` means an Azure stage entered its job, not that its action succeeded. If `receipt.json` is absent, check download, authentication or interruption logs.
3. Review plan artifacts and cost summaries before approvals. Apply rechecks after approval; drift requires new review. An exclusive lock serializes the protected stage, not the entire six-stage run, so rechecks remain necessary with concurrent runs.
4. Inspect `result-Release/receipt.json` and smoke evidence. Discovery, publication, Foundation or setup success does not establish workload readiness. Failure can leave resources in Azure; there is no automatic rollback or destructive teardown.

## Platform rollout and checks

Keep targets disabled until [onboarding](deployment-stacks-upgrade.md#platform-setup-and-exact-local-checks) is complete: catalog RG, scoped federated connections, deployment/RBAC/deny permissions, private agents/network paths, protected environments, main-branch/Required Template checks, approvals and exclusive locks. These ADO settings live outside YAML. Do not precreate the workload RG. Module registry setup is excluded.

From the repository root, validate configuration/source changes in this order:

```powershell
./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

After merge, run Discover on main and inspect its artifact, then select that matching successful run in Deploy. Enable dev first only after platform onboarding. Verify server-expanded stages and resource authorization before approving Azure actions. Local checks cannot prove ADO expansion, UI rendering, permissions or Azure behavior. See [dated validation evidence](validation.md).
