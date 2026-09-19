# Developer self-service deployment

Status: implemented in the repository and locally contract-tested. Run 8 on `feature/selfservice` completed discovery using the DNS inventory fallback and published its artifact; successful Azure deployment remains unverified. Deployment requires a verified manifest from a separate successful main-branch discovery run. All four checked-in targets remain disabled for deployment until platform onboarding is complete. See [completion status](completion-status.md).

This repository deploys the blob-transfer stack. It does not contain a claims UI, claims database, Semantic Kernel agents, or a COBOL gateway. Uploading files is its integration contract; downstream business workflows are separate solutions.

See the [full pipeline flow](pipeline-flow.md) for the entrypoint/stage diagram, script calls, artifact handoffs, shared qualification steps, failure evidence and remaining platform setup. Module registry setup is excluded; local modules are compiled into the published Template Spec.

### Temporary hosted setup check

While a selected target has `enabled: false`, the Deploy entry point now expands to **Hosted setup check - no Azure deployment**, using `pool: { vmImage: windows-latest }` in the Microsoft-hosted Azure Pipelines pool. It checks the target/catalog and publishes remaining setup requirements. It does not download/validate the discovery artifact, build a release, run what-if or deploy Azure resources. A successful setup check is not deployment readiness; platform-managed resource settings are reported but not applied.

The generated route omits private-pool jobs and deployment environments entirely, avoiding the reported missing/unauthorized `blob-transfer-private` lookup for these disabled targets. A skipped job condition alone would not fix queue-time resource validation. The discovery pipeline resource must still exist/be accessible, and executing the check requires hosted agent capacity. There is no built-in image called `windows-default`; the supported image here is `windows-latest`.

After onboarding, configure existing enterprise networking or an explicit isolated-network exception in `config/platform.json`, set the target's `enabled` flag to `true`, regenerate the catalog and manifest, and merge. Its reviewed private pool, environment checks, discovery validation and original deployment stages are then restored. The Azure deployment scripts continue rejecting disabled targets. See Microsoft's [hosted agent configuration](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/hosted?view=azure-devops).

## What developers select in Run pipeline

The developer contract is now **workload type**, **registered workload name**, **environment**, and **approved region**. The current pattern is `blob-transfer`, with registered name `blobcopy` and approved region `eastus2`. Resource and cost references remain visible below those choices.

The **Before running - discovery, approvals and readiness** information field explains the main-branch discovery selection, publication/preview/approval/deployment flow, and setup-only behavior for disabled targets. It is guidance, not an additional deployment option. To display an updated menu, publish the generated YAML and its supporting files to the branch selected in ADO, then reopen Run pipeline.

The platform resolves subscription, network/subnet/DNS, service connection, agent pool, deployment environment, destination endpoints and alert settings. Endpoint and alert checkboxes were removed from the developer menu because these are implementation/security decisions. Their underlying Bicep options remain platform-controlled in `config/platform.json`; storage and observability are mandatory capabilities. Unknown patterns, capabilities and ambiguous target mappings fail validation.

Discover remains a platform/operator menu with subscription/network selectors. Deploy uses **Resources > discovery** to select the saved successful main run; ADO supplies its IDs automatically. Saved inventory is validated for enabled deployments and never dynamically rewrites the form. See [enterprise ownership and configuration](enterprise-platform.md) for the exact request contract, centralized DNS/resolver model, monitoring reuse and known gaps.

Cost estimates remain dated USD retail references, not a spending cap. The selected frozen estimate and resource changes appear in previews. [Cost assumptions](self-service-costs.md) document usage exclusions. Platform changes that omit previously created endpoints do not delete those endpoints or eliminate their charges.

## Developer access and exact workflow

After platform onboarding:

1. Open the team's Azure DevOps project and select **Pipelines**.
2. Run Discover (`Enetact.Bicep`, `/azure-pipelines-self-service.yml`) on `main` with the approved target selections. Wait for success and inspect its discovery summary.
3. Open the pipeline registered from `/azure-pipelines-self-service-deploy.yml`, choose **Run pipeline** on `main`, then **Resources > discovery** and select that successful main-branch run. Select pattern `blob-transfer`, registered workload `blobcopy`, the matching environment and approved region. Platform configuration resolves the infrastructure bindings and options. Only enabled targets deploy. Naming and network configuration come from the reviewed profile. See [the handoff and new-network guide](subscription-discovery.md).
4. The deployment run downloads and verifies the exact manifest and originating run, then qualifies and freezes the application, infrastructure, parameters, target and discovery evidence into one artifact. Missing, stale, partial or mismatched evidence stops before deployment. The protected PublishTemplate stage publishes or verifies the content-addressed Template Spec; review its publication receipt.
5. Inspect the Foundation preview summary and `plan-Foundation` artifact. The platform approver authorizes the protected environment stage. Stacks that already reached Release skip Foundation changes but still check prerequisites.
6. Inspect the Release preview and `plan-Release` artifact. After approval, the pipeline rechecks the plan, publishes the package, deploys the Function App, synchronizes triggers, and uploads synthetic blobs through the real dispatcher.
7. Open `result-Release/receipt.json`. Success requires `status: Ready` and `ready: true`. `FoundationReady` only means infrastructure prerequisites passed; it does not mean the application is deployed.
8. Use the source account/container in `outputs.json`, with separately granted uploader identity and private connectivity. The live smoke leaves synthetic source, destination, and ledger records for audit.

Developers need project access, pipeline view/queue permissions, and artifact access. Deployment permissions remain with the selected service connection. Queue permission does not grant recovery, administration, or production approval rights.

## Stages and exact entry points

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
| `Invoke-ServiceJson` | Checked Azure CLI invocation and JSON parsing. No storage account keys are used. |
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

The four disabled baseline profiles now bind the user-supplied Azure Resource Manager service connection `SC-AZ-A-Bicep`. Authorize the self-service pipeline to use this exact connection in its Azure DevOps project. Its authentication scheme, subscription and permissions have not been inspected here; verify workload identity federation and the intended scope before enabling deployment. Discovery-generated profiles can instead bind a selected existing connection by its exact Azure DevOps endpoint ID. Do not embed client secrets in YAML.

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

The Deploy definition declares a pipeline resource named `discovery`, sourced from the exact ADO name in `self-service/pipeline-settings.json` (currently `Enetact.Bicep`). If renaming/moving Discover, update that setting and regenerate the catalog. The resource has completion triggers disabled. Its selected `pipelineID` and `runID` metadata feed the existing exact-run artifact download and handoff validation. Grant the intended deployment pipeline resource authorization where required, and give its project Build Service read access to the discovery builds/artifacts. An administrator may need to authorize the protected resources on first use; avoid granting all pipelines access. The [native run picker](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/resources?view=azure-devops#manual-resource-version-picker) is available before queueing, not between stages.

### 4. Enable the registered target

Update a schemaVersion 2 profile under `self-service/targets/` with the real subscription/RG and parameter path, or generate one from the [discovery report](subscription-discovery.md). Its subscription alias/network key are internal platform bindings. Set `enabled: true` only after onboarding and review, using existing central networking or a documented exception in `config/platform.json`. Run locally from the repo root:

```powershell
./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

Review and merge the changes through the protected branch. Queue a dev run and complete live acceptance before enabling higher environments. Disabled targets run only the hosted setup check through the Deploy menu; actual deployment scripts still reject them.

For another registered instance of the blob-transfer pattern, add target JSON profiles and parameter files, then regenerate the catalog. Create the corresponding external resources/controls first. Each workload/environment/region intent must resolve to exactly one target; ambiguous subscription/network choices fail generation. The current catalog contains only `blobcopy`. A different workload pattern requires its own real composition, contract and qualification tests before registration.

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
- Existing stacks must have the expected `<workload>-<environment>` deployment history and output contract from this template. Missing history and changed source/ledger/queue names require a separate platform migration; arbitrary resource import is not implemented.
- Every run builds one release, frozen across its Foundation/Release stages. Selecting QA or Prod starts another build. **Promotion of the same package across separate environment runs is not implemented.**
- Automated smoke verifies ordinary uploads, overwritten source revisions, three completed request records and a shared hash-verified destination. It does not qualify complete poison routing, restart/load behavior, notification delivery, backups or all version-reconciliation scenarios.

For full production acceptance use [validation](validation.md), [operations](operations.md), and [completion status](completion-status.md).
