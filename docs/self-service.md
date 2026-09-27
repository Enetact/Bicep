# Developer self-service deployment

Status reviewed 26 September 2026: two implemented workload adapters, eight disabled targets and dedicated Discover/Deploy menus. Supplied Preview run 23 verified discovery run 21, then stopped before Azure What-If on five onboarding settings. Dev now passes those checks locally with matching resolved discovery; successful live Azure acceptance is still unverified. See the [catalog/method map](self-service-catalog.md), [completion status](completion-status.md) and [future expansion plan](self-service-expansion-plan.md).

This repository implements `blob-transfer` / `blobcopy` and `logic-app-event-grid` / `eventflow`. The [Event Flow runbook](../workloads/logic-app-event-grid/README.md) specifies its resource inventory, private queue bridge, two approval exceptions, methods and Azure acceptance. The detailed blob transfer sections below remain specific to that pattern. It does not contain a claims UI, claims database, Semantic Kernel agents, or a COBOL gateway. Uploading files is its integration contract; downstream business workflows are separate solutions.

Event flow discovery now saves a [prerequisite resource plan](prerequisite-resolution.md): reuse compatible selected resources, create absent standard resources within the workload stack, and block when inventory is unknown. Rerun Discover before using this behavior.

See the [full pipeline flow](pipeline-flow.md) for the entrypoint/stage diagram, script calls, artifact handoffs, shared qualification steps, failure evidence and remaining platform setup. Module registry setup is excluded; local modules are compiled into the published Template Spec.

## Preview first, deploy second

The workload-specific Deploy pipelines now have **Preview** and **Deploy** stages. **Run stages = Preview only** is the default. Preview consumes the selected Discover artifact, compiles the full runtime-enabled stack and performs Azure validation/What-If without deploying workload resources. Its `README.md` appears under run **Summary / Extensions** and in the **deployment-preview** artifact. See the [preview guide](deployment-preview.md) for exact actions, permissions and evidence.

All checked-in targets remain disabled. A disabled target may preview once its real parameters and Azure permissions are configured; actual Deploy still rejects it. Placeholder owner/destination/network values are blockers, not an empty successful plan. Preview runs on hosted `windows-latest`; Preview-only and disabled routes omit all private pools and deployment environments at compile time. The discovery definition, artifact and service connection still require authorization.

The legacy generic Deploy file retains its original hosted `SetupOnly` behavior for disabled targets and six-stage flow for enabled targets. Use the dedicated workload files for the new two-stage experience.

## Choose the workload pipeline first

Use a separate ADO definition for each workload and operation. Each native Run pipeline form contains only that workload's summaries, dependencies and cost reference. There is no workload-type dropdown in these forms; the generated wrapper passes a literal workload ID into the existing shared templates.

| ADO definition name | YAML path |
|---|---|
| **Discover - Blob copy** | `/azure-pipelines-blobcopy-discover.yml` |
| **Deploy - Blob copy** | `/azure-pipelines-blobcopy-deploy.yml` |
| **Discover - Event flow** | `/azure-pipelines-eventflow-discover.yml` |
| **Deploy - Event flow** | `/azure-pipelines-eventflow-deploy.yml` |

In Discover, select instance, environment, subscription and network. The only blueprint shown is the selected pipeline's workload; discovery itself creates nothing. In Deploy, select instance (`blobcopy` or `eventflow`, already restricted by pipeline), environment and region. Read that workload's resources, dependencies, costs and enabled/disabled status. Choose the matching successful main-branch discovery run under **Resources > discovery**. Summary fields are informational and never flow into resource settings.

The two Deploy menus extend the same `pipelines/deploy-entry.yml`, routing to `pipelines/templates/self-service-two-stage.yml`. The two Discover menus use the same catalog routing and inventory script. Literal service connections, pools, approvals, manifests and instance validation remain unchanged. Separate menus do not enable a target or authorize deployment.

### Register the new definitions in ADO

1. Merge/push the generated YAML and supporting files to GitHub.
2. In Azure DevOps **Pipelines > New pipeline**, select the connected GitHub repository and **Existing Azure Pipelines YAML file** on `main`.
3. Create the two **Discover** definitions first, using the paths and exact names above. Authorize their existing discovery service connection when ADO requests it.
4. Create the two **Deploy** definitions from the corresponding YAML files. Their pipeline-resource sources are `Discover - Blob copy` and `Discover - Event flow`. Authorize access to that discovery pipeline/artifact for each Deploy definition, and configure protected-resource permissions/checks as required by onboarding.
5. Run the chosen Discover definition successfully on `main`. Reopen its matching Deploy definition's Run pipeline menu and select that run under **Resources > discovery**. Leave **Run stages = Preview only** to inspect planned changes; real environment configuration and Azure validation permissions are required, even while the target is disabled.

Register or verify these definitions in your ADO project. YAML file names do not set ADO definition names automatically: rename each definition to match this table. If names or ADO folders differ, set the exact discovery definition paths in `self-service/pipeline-settings.json` under `workloadDiscoveryPipelineNames`, regenerate with `Update-ServiceCatalog.ps1`, update the source manifest and merge. This prevents a Deploy menu from accidentally selecting another workload's discovery pipeline. Existing runs from the generic discovery definition do not appear in the new dedicated resource pickers; run each new Discover definition once.

The existing generic roots (`azure-pipelines-self-service.yml`, `azure-pipelines-self-service-deploy.yml`) remain compatible with their existing definitions and generic discovery source `Enetact.Bicep`. They no longer show both workload resource panels. Use the dedicated definitions above for the requested workload-only summaries. Do not repoint the generic discovery definition to one workload while its legacy generic Deploy still serves both.

ADO parameter declarations have static labels/allowed values and no visibility rule; conditional execution affects the expanded pipeline, not a reactive form. See the [parameter schema](https://learn.microsoft.com/en-us/azure/devops/pipelines/yaml-schema/parameters-parameter?view=azure-pipelines) and [runtime parameter processing](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/runtime-parameters?view=azure-devops). Selecting an ADO definition before opening Run pipeline provides the workload-specific native experience. No extension or custom portal is required.

## Developer access and exact workflow

1. Open **Discover - Blob copy** or **Discover - Event flow** and run it successfully on `main` with the approved instance/environment.
2. Open the matching **Deploy** definition on `main`. Select the same instance/environment and approved region; choose that successful run under **Resources > discovery**.
3. Leave **Run stages = Preview only**. The Preview stage verifies discovery provenance, compiles the full stack, validates it and obtains the Azure resource/property changes. No application build, Template Spec publication or workload apply runs in this mode.
4. Open **Summary / Extensions > Bicep deployment preview**, or download **deployment-preview/README.md**. Inspect creates, modifications, removals, unchanged resources, certainty, property deltas, diagnostics and costs. A failed report includes blockers; it never represents a zero-change approval.
5. When ready and the target is enabled, queue a fresh run with **Preview and deploy**. Review that run's Preview report before granting the configured ADO Deploy environment approval. Preview-only runs cannot be switched into deployment after queueing.
6. Deploy qualifies the application, verifies its infrastructure matches Preview, publishes the pinned Template Spec, rechecks the full preview for drift, then runs the existing Foundation/Release apply and readiness checks.
7. Inspect **deployment-result/receipt.json**: readiness requires `status: Ready` and `ready: true`. Phase-specific plans/results remain inside this artifact. A failed deployment can leave resources; there is no automatic rollback.
8. For Blob copy, use the source account/container from the Release outputs with a separately authorized uploader and private connectivity. Live smoke retains synthetic records for audit.

Developers need project access, pipeline view/queue permissions, and artifact access. Deployment permissions remain with the selected service connection. Queue permission does not grant recovery, administration, or production approval rights.

## Stages and exact entry points

The dedicated pipelines expose only **Preview** and **Deploy**. [Preview methods and exact job sequencing](deployment-preview.md#method-and-command-map) document their implementation. Foundation/Release remain internal operations of Deploy, preserving workload lifecycle and runtime checks.

### Legacy generic pipeline stage map

| Stage | Calls | Result and effects |
|---|---|---|
| SetupOnly (disabled targets only) | `Read-ServiceTarget -AllowDisabled`; `Update-ServiceCatalog.ps1 -Check` | Hosted Windows configuration check and setup summary. No discovery download/validation, Azure tasks, private pool, release bundle or deployment readiness result. Enabled targets use the stages below instead. |
| Qualify | `Read-ServiceTarget`; `Test-Project.ps1`; `Test-Recovery.ps1`; `Test-LocalTooling.ps1`; `Run-Local.ps1` → `Test-Local.ps1` → `Stop-Local.ps1`; `Build-Package.ps1`; `New-SelfServiceBundle.ps1` | Rejects disabled targets, validates manifest, compiles four environments, tests and builds. Freezes selected target/template/parameters/package. No Azure deployment. |
| PublishTemplate | `Publish-WorkloadTemplate.ps1` → `Publish-StackTemplate` | Protected publishing environment; creates or reuses the content-hashed Template Spec and verifies the stored content. No workload deployment. |
| PlanFoundation | `Invoke-SelfService.ps1 -Action PlanFoundation` → `New-ServicePlan` | Checks destination and owned stack state; validates the pinned Template Spec and runs native stack What-If. Skips Foundation after the stack has reached Release. Publishes preview. |
| ApplyFoundation | `Invoke-SelfService.ps1 -Action ApplyFoundation` → `Invoke-ServiceApply` | Rechecks approved preview; provisions infrastructure when needed; verifies output contract and private storage access. |
| PlanRelease | `Invoke-SelfService.ps1 -Action PlanRelease` → `New-ServicePlan` | Previews frozen infrastructure with Function App enabled and the exact release package path. |
| ApplyRelease | `Invoke-SelfService.ps1 -Action ApplyRelease` → `Invoke-ServiceApply` | Rechecks preview → private connectivity → immutable package upload/reuse → ARM deployment → connectivity → five indexed Functions → `Smoke-Test.ps1` → Ready receipt. |

`self-service-common.ps1` implements the shared contract:

| Methods | Responsibility |
|---|---|
| `Read-ServiceTarget`, `Assert-ServiceTarget`, `Resolve-ServicePath` | Allowlisted selection, exact target fields, enabled flag, approved relative parameter paths, traversal/reparse-point rejection. |
| `Assert-ServiceParameters`, `Get-ServiceParameter` | Explicit ownership/destination/network settings; environment match; placeholder rejection; valid source, ledger and queue names; mapped synthetic prefix; source-version reconciliation; production action groups. |
| `Read-ServiceBundle`, `Get-ServiceHash` | Verify SHA-256 of the frozen template, parameters, target, package, Function metadata and included discovery/cost evidence before Azure calls. |
| `ConvertTo-Canonical`, `Get-ValueHash` | Order-independent object hashing; preserve array ordering and values for preview comparisons. |
| `Invoke-ServiceJson` | Checked Azure CLI invocation and JSON parsing. Blob copy uses identity-based data access; Event flow has the separately documented runtime-storage credential exception. |
| `Test-ServiceDestination`, `Get-ServiceState`, `Get-ServiceOutputs` | Existing same-tenant destination/container, HNS agreement, owned stack lifecycle/phase/inventory, stack output contract and unchanged container/queue names. Legacy bundles retain RG/app/deployment lookups. |
| `Get-ServiceChanges`, `New-ServicePlan`, `Assert-ServicePlan` | Successful analyzed what-if, frozen effective parameters, 24-hour preview lifetime, state/output/change comparison immediately before apply. Delete/unknown changes, missing property deltas and high-risk topology, identity, RBAC, SKU and network-security modifications are rejected. |
| `Test-ServicePrivateAddress`, `Wait-ServiceConnectivity` | Approved owned private endpoints, RFC1918 IPv4 DNS and TCP 443 for host Blob, solution Blob/Queue, destination Blob; identity-based container/queue access. Retries propagation failures. |
| `Publish-ServicePackage` | Upload without overwrite; reuse an existing release path only after downloading it and verifying identical bytes. |
| `Wait-ServiceFunctions` | Synchronize triggers and require exactly the expected five indexed Function names. |
| `Invoke-ServiceSmoke` | Calls the live smoke with the target's scope map and synthetic prefix; reads its evidence. |
| `Invoke-ServiceApply` | Orders mutations and checks; only returns Ready after successful smoke with three distinct request IDs. |

`Invoke-SelfService.ps1` additionally requires a manual `main` run, exact YAML-bound resources, and source-commit agreement. Its `finally` block retains a failed receipt when script execution fails. An agent outage, checkout failure, or service-connection login failure before the script starts can prevent receipt generation; the Azure DevOps task log remains the evidence in that case.

The existing [dispatcher reference](dispatcher/README.md) documents the application methods called after upload. This guide describes provisioning orchestration, not another runtime dispatcher.

## Platform onboarding: required once per target

### 1. Prepare the Azure scope

- Select an actual Azure public-cloud subscription and reserve a fresh target resource-group name; **do not precreate the workload RG**. The new subscription-scoped stack wrapper owns and creates it. Existing unowned RGs require separate adoption review. Confirm resource providers, policy, regional plan/zone availability and quotas. Precreate the separate Template Spec catalog RG and configure [stack publication and permissions](deployment-stacks-upgrade.md).
- Obtain the existing destination storage account and container from their owner. Cross-subscription within the same Entra tenant is supported; cross-tenant is rejected. Destination lifecycle, firewall policy and data ownership remain external.
- Fill `workloads/blob-transfer/environments/main.<environment>.bicepparam`: owner, cost center, destination identifiers/HNS flag, non-overlapping VNet/subnet ranges, scope map, alert action groups, optional uploader/operator groups, capacity and retention settings. Production requires action groups. Do not put secrets in parameters.
- Provision private routing and DNS for the agent, external uploader and operators. The template supports a new workload VNet or an [approved existing VNet/subnet/DNS profile](subscription-discovery.md). It does not establish hub peering, VPN, agent connectivity or DNS resolvers. Existing mode validates and reuses IDs without redeploying the shared network.
- Platform networking and the agent's routes must exist before self-service. Use existing central subnets/DNS for enterprise targets. Do not use the legacy manual Bootstrap command against a stack-managed target; new-network exceptions need their own connectivity acceptance plan.

### 2. Prepare identity and a private agent

The eight disabled target profiles bind the user-supplied Azure Resource Manager service connection `SC-AZ-A-Bicep`. Authorize the self-service pipeline to use this exact connection in its Azure DevOps project. Supplied logs show federated authentication and subscription selection for discovery, but full publishing/deployment rights are not established; verify the intended scope before enabling deployment. Discovery-generated profiles can instead bind a selected existing connection by its exact Azure DevOps endpoint ID. Do not embed client secrets in YAML.

The deployment identity needs subscription-scoped stack/preview operations, RG creation and deny-setting management, plus resource deployment and role-assignment permissions for the workload. It also needs Template Spec read access. Contributor alone cannot create role assignments or satisfy all deny-management requirements. The destination module performs a nested deployment in the destination RG and grants at the existing destination container. Have each owner approve scoped permissions. Configure publishing separately and use constrained roles where available; this repository does not provision service-connection permissions. See the [complete stack runbook](deployment-stacks-upgrade.md).

Data-plane permissions for the deployment/smoke identity are separate from ARM access:

| Scope | Needed by pipeline |
|---|---|
| Host package container | Blob read/write for immutable upload and existing-package hash comparison. |
| Solution source container | Blob write/read for synthetic source uploads and metadata/version checks. |
| Solution ledger container | Blob read for request completion checks. |
| Solution work queue | Queue metadata read for readiness. |
| Existing destination container | Blob read for verified destination bytes. |

Set the optional `deploymentPrincipalObjectId` to let Bicep assign these container/queue roles to the approved pipeline identity. The deployment identity still needs separately granted authority to create role assignments; Contributor alone is insufficient. Alternatively, the platform can preassign data permissions and leave this parameter empty. Avoid broad subscription-wide data access. Runtime identity roles are separately defined by Bicep; see [security and RBAC](security-and-rbac.md).

Create a trusted Windows private agent pool `blob-transfer-private`. Install PowerShell 7.4+, Git and Azure CLI 2.89.1 or newer with stack What-If support; allow outbound access to Azure DevOps, Entra and Azure management. Publishing/plan/apply use compiled artifacts. The Microsoft-hosted Windows qualification job installs the pinned SDK, Bicep and Node and downloads NuGet/npm tools; its agent capacity and package-feed access must also be available.

Ensure the private agent resolves/reaches the actual private storage endpoints. The automated probe covers four storage endpoints, not every host Queue/Table, DFS, app/SCM, uploader or telemetry path. Qualify those remaining paths during platform acceptance.

### 3. Configure Azure DevOps controls

1. Connect the Azure DevOps project to the GitHub repository. Keep existing `Enetact.Bicep` (definition ID 1) on `/azure-pipelines-self-service.yml` for Discover. Register a second definition, suggested name **BlobTransfer - Deploy**, using **New pipeline > GitHub > Enetact/Bicep > Existing Azure Pipelines YAML file**, branch `main`, path `/azure-pipelines-self-service-deploy.yml`. Use **Save** from the Run/Save and run split-button menu to register without executing. Keep `/azure-pipelines.yml` as the separate build/test entry point. Publish the source changes before expecting the updated forms.
2. Precreate the exact deployment environment named by each target profile. Baseline examples use `blobcopy-dev`, `blobcopy-qa`, `blobcopy-uat`, and `blobcopy-prod`; newly generated profiles include their naming suffix. Authorize only the intended pipeline and approvers. Do not depend on implicit environment creation.
3. Configure environment approvals, main-branch control and an **exclusive lock check**. YAML sets `lockBehavior: sequential`, but this only works when the resource has an exclusive lock configured. Approvals run for each apply stage.
4. Restrict each service connection and the private agent pool to the trusted deployment pipeline; add branch control/checks as appropriate. Do not enable access for all pipelines or permit untrusted PR jobs on the private pool. Service-connection checks can also pause the preview stages.
5. Protect GitHub `main` and require platform review for pipeline YAML/templates, target profiles, parameters, deployment scripts and Bicep. Restrict pipeline editing and queue-time variable overrides. A script branch check is not a security boundary against someone who can edit the scripts or resource permissions.
6. Set retention/access for deployment artifacts and logs. These contain infrastructure identifiers, configuration and synthetic transfer evidence; they are not public deliverables.

Microsoft documents [resource checks and approvals](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops) outside YAML and [AzureCLI task configuration](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-cli-v2?view=azure-pipelines). The repository cannot enforce missing organization settings by itself.

Each dedicated Deploy definition declares `discovery` from its workload's exact ADO name under `workloadDiscoveryPipelineNames` in `self-service/pipeline-settings.json`. The generic compatibility pair uses `discoveryPipelineName` (`Enetact.Bicep`). If renaming/moving Discover, update that setting and regenerate the catalog. The resource has completion triggers disabled. Its selected `pipelineID` and `runID` metadata feed the existing exact-run artifact download and handoff validation. Grant the intended deployment pipeline resource authorization where required, and give its project Build Service read access to the discovery builds/artifacts. An administrator may need to authorize the protected resources on first use; avoid granting all pipelines access. The [native run picker](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/resources?view=azure-devops#manual-resource-version-picker) is available before queueing, not between stages.

### 4. Enable the registered target

Update a schemaVersion 2 profile under `self-service/targets/` with the real subscription/RG and parameter path, or generate one from the [discovery report](subscription-discovery.md). Its subscription alias/network key are internal platform bindings. Set `enabled: true` only after onboarding and review, using existing central networking or a documented exception in `config/platform.json`. Run locally from the repo root:

```powershell
./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

Review and merge the changes through the protected branch. Queue a dev run and complete live acceptance before enabling higher environments. Disabled targets run only the hosted setup check through the Deploy menu; actual deployment scripts still reject them.

For another registered instance of the blob-transfer pattern, add target JSON profiles and parameter files, then regenerate the catalog. Create the corresponding external resources/controls first. Each workload/environment/region intent must resolve to exactly one target; ambiguous subscription/network choices fail generation. The catalog contains `blobcopy` and `eventflow`, each mapped to its own implemented pattern. Additional patterns require a real composition, package adapter, contract and qualification tests before registration.

## Evidence, failures and reruns

| Artifact | Contents |
|---|---|
| `self-service-bundle` | `main.json`, `parameters.json`, `target.json`, `application.zip`, `functions.metadata`, `cost-estimate.json`, `discovery/` evidence, and `bundle.json` with file hashes, release ID and source commit. |
| `self-service-tests` | Project test/advisory output, offline deployment contract results, local Function host logs when those steps ran. VSTest results also appear in the Tests tab. |
| `plan-Foundation`, `plan-Release` | `summary.md`, `what-if.json`, `effective.parameters.json`, `plan.json`, and preview receipt. |
| `result-Foundation`, `result-Release` | Recheck preview, deployment outputs, success/failure receipt; release smoke evidence when completed. |

Hashes detect changes relative to the receipt; they are not signatures. Artifact access, the protected checkout and Azure DevOps authorization establish trust. What-if is a preview, not a guarantee of runtime success or a substitute for resource locks. State may change after the recheck; exclusive target ownership is still required.

- Drift, an expired preview, unknown what-if changes, altered package bytes or failed readiness stops the run. Inspect evidence and queue a fresh reviewed run after correction; do not bypass the guard.
- Failed applies can leave infrastructure or a deployed app in place. Failed stacks require explicit platform recovery; there is no automatic rollback, adoption, destructive teardown or cost-expiry job. Stack ownership does not make every allowed Modify harmless; approvers must read the full preview. Preview metadata cleanup is separate from workload lifecycle.
- Existing stacks must have a valid workload phase parameter, healthy managed-resource inventory within the approved scope, and the selected adapter's output contract. An existing resource group without its expected stack is blocked for explicit adoption review. Changed source/ledger/queue contracts require a separate platform migration; arbitrary resource import is not implemented.
- Every run builds one release, frozen across its Foundation/Release stages. Selecting QA or Prod starts another build. **Promotion of the same package across separate environment runs is not implemented.**
- Automated smoke verifies ordinary uploads, overwritten source revisions, three completed request records and a shared hash-verified destination. It does not qualify complete poison routing, restart/load behavior, notification delivery, backups or all version-reconciliation scenarios.

For full production acceptance use [validation](validation.md), [operations](operations.md), and [completion status](completion-status.md).
