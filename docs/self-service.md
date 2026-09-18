# Developer self-service deployment

Status: implemented in the repository, locally contract-tested, **not yet run in Azure DevOps or against Azure**. All four checked-in targets are deliberately disabled. The platform onboarding below must be completed before a developer can provision a target. See [completion status](completion-status.md) for the evidence and remaining work.

This repository deploys the blob-transfer stack. It does not contain a claims UI, claims database, Semantic Kernel agents, or a COBOL gateway. Uploading files is its integration contract; downstream business workflows are separate solutions.

## Developer access and exact workflow

After platform onboarding:

1. Open the team's Azure DevOps project and select **Pipelines**.
2. Open the pipeline registered from `/azure-pipelines-self-service.yml` and select **Run pipeline**.
3. Select branch `main`, operation `deploy`, workload `blobcopy`, environment, approved subscription alias and network profile. The default operation is read-only `discover`; see [subscription discovery](subscription-discovery.md). Only enabled targets deploy. No secrets, resource groups, or package paths need to be entered by developers.
4. Run the pipeline. Qualification tests and freezes the application, infrastructure, parameters, and selected target into one run artifact.
5. Inspect the Foundation preview summary and `plan-Foundation` artifact. The platform approver authorizes the protected environment stage. Existing applications skip Foundation changes but still check prerequisites.
6. Inspect the Release preview and `plan-Release` artifact. After approval, the pipeline rechecks the plan, publishes the package, deploys the Function App, synchronizes triggers, and uploads synthetic blobs through the real dispatcher.
7. Open `result-Release/receipt.json`. Success requires `status: Ready` and `ready: true`. `FoundationReady` only means infrastructure prerequisites passed; it does not mean the application is deployed.
8. Use the source account/container in `outputs.json`, with separately granted uploader identity and private connectivity. The live smoke leaves synthetic source, destination, and ledger records for audit.

Developers need project access, pipeline view/queue permissions, and artifact access. Deployment permissions remain with the selected service connection. Queue permission does not grant recovery, administration, or production approval rights.

## Stages and exact entry points

| Stage | Calls | Result and effects |
|---|---|---|
| Qualify | `Read-ServiceTarget`; `Test-Project.ps1`; `Test-Recovery.ps1`; `Test-LocalTooling.ps1`; `Run-Local.ps1` → `Test-Local.ps1` → `Stop-Local.ps1`; `Build-Package.ps1`; `New-SelfServiceBundle.ps1` | Rejects disabled targets, validates manifest, compiles four environments, tests and builds. Freezes selected target/template/parameters/package. No Azure deployment. |
| PlanFoundation | `Invoke-SelfService.ps1 -Action PlanFoundation` → `New-ServicePlan` | Checks destination and existing application; runs ARM what-if unless an app already exists. Publishes preview. |
| ApplyFoundation | `Invoke-SelfService.ps1 -Action ApplyFoundation` → `Invoke-ServiceApply` | Rechecks approved preview; provisions infrastructure when needed; verifies output contract and private storage access. |
| PlanRelease | `Invoke-SelfService.ps1 -Action PlanRelease` → `New-ServicePlan` | Previews frozen infrastructure with Function App enabled and the exact release package path. |
| ApplyRelease | `Invoke-SelfService.ps1 -Action ApplyRelease` → `Invoke-ServiceApply` | Rechecks preview → private connectivity → immutable package upload/reuse → ARM deployment → connectivity → five indexed Functions → `Smoke-Test.ps1` → Ready receipt. |

`self-service-common.ps1` implements the shared contract:

| Methods | Responsibility |
|---|---|
| `Read-ServiceTarget`, `Assert-ServiceTarget`, `Resolve-ServicePath` | Allowlisted selection, exact target fields, enabled flag, approved relative parameter paths, traversal/reparse-point rejection. |
| `Assert-ServiceParameters`, `Get-ServiceParameter` | Explicit ownership/destination/network settings; environment match; placeholder rejection; valid source, ledger and queue names; mapped synthetic prefix; source-version reconciliation; production action groups. |
| `Read-ServiceBundle`, `Get-ServiceHash` | Verify SHA-256 of the exact five frozen files and validate Function metadata before Azure calls. |
| `ConvertTo-Canonical`, `Get-ValueHash` | Order-independent object hashing; preserve array ordering and values for preview comparisons. |
| `Invoke-ServiceJson` | Checked Azure CLI invocation and JSON parsing. No storage account keys are used. |
| `Test-ServiceDestination`, `Get-ServiceState`, `Get-ServiceOutputs` | Existing same-tenant destination/container, HNS agreement, target RG/app lookup, deployment output contract and unchanged container/queue names. |
| `Get-ServiceChanges`, `New-ServicePlan`, `Assert-ServicePlan` | Successful analyzed what-if, frozen effective parameters, 24-hour preview lifetime, state/output/change comparison immediately before apply. Delete and unanalyzed change types are rejected. |
| `Test-ServicePrivateAddress`, `Wait-ServiceConnectivity` | Approved owned private endpoints, RFC1918 IPv4 DNS and TCP 443 for host Blob, solution Blob/Queue, destination Blob; identity-based container/queue access. Retries propagation failures. |
| `Publish-ServicePackage` | Upload without overwrite; reuse an existing release path only after downloading it and verifying identical bytes. |
| `Wait-ServiceFunctions` | Synchronize triggers and require exactly the expected five indexed Function names. |
| `Invoke-ServiceSmoke` | Calls the live smoke with the target's scope map and synthetic prefix; reads its evidence. |
| `Invoke-ServiceApply` | Orders mutations and checks; only returns Ready after successful smoke with three distinct request IDs. |

`Invoke-SelfService.ps1` additionally requires a manual `main` run, exact YAML-bound resources, and source-commit agreement. Its `finally` block retains a failed receipt when script execution fails. An agent outage, checkout failure, or service-connection login failure before the script starts can prevent receipt generation; the Azure DevOps task log remains the evidence in that case.

The existing [dispatcher reference](dispatcher/README.md) documents the application methods called after upload. This guide describes provisioning orchestration, not another runtime dispatcher.

## Platform onboarding: required once per target

### 1. Prepare the Azure scope

- Select an actual Azure public-cloud subscription and precreate the target resource group. The Bicep entry point is resource-group scoped; it does not create the subscription or resource group. Confirm resource providers, policy, regional plan/zone availability and quotas.
- Obtain the existing destination storage account and container from their owner. Cross-subscription within the same Entra tenant is supported; cross-tenant is rejected. Destination lifecycle, firewall policy and data ownership remain external.
- Fill `environments/<environment>.bicepparam`: owner, cost center, destination identifiers/HNS flag, non-overlapping VNet/subnet ranges, scope map, alert action groups, optional uploader/operator groups, capacity and retention settings. Production requires action groups. Do not put secrets in parameters.
- Provision private routing and DNS for the agent, external uploader and operators. The template supports a new workload VNet or an [approved existing VNet/subnet/DNS profile](subscription-discovery.md). It does not establish hub peering, VPN, agent connectivity or DNS resolvers. Existing mode validates and reuses IDs without redeploying the shared network.
- If the new VNet must exist before platform networking can be connected, the platform team performs the reviewed manual Bootstrap sequence in the root README, connects routing/DNS, approves destination private endpoints and proves access before enabling the self-service target. Do not expect the developer's first run to solve this bootstrap dependency.

### 2. Prepare identity and a private agent

The four disabled baseline profiles now bind the user-supplied Azure Resource Manager service connection `SC-AZ-A-Bicep`. Authorize the self-service pipeline to use this exact connection in its Azure DevOps project. Its authentication scheme, subscription and permissions have not been inspected here; verify workload identity federation and the intended scope before enabling deployment. Discovery-generated profiles can instead bind a selected existing connection by its exact Azure DevOps endpoint ID. Do not embed client secrets in YAML.

The deployment identity needs resource deployment permissions in the target RG, including the Bicep resources and role assignments. Contributor alone cannot create role assignments. The destination module also performs a nested deployment in the destination RG and a runtime identity role assignment at the existing destination container. Have the destination owner approve appropriately scoped deployment, read and role-assignment permissions. Private endpoint approval rights may require a separate owner action. Use constrained custom roles or constrained RBAC delegation where available; this repository does not provision the service connection's permissions.

Data-plane permissions for the deployment/smoke identity are separate from ARM access:

| Scope | Needed by pipeline |
|---|---|
| Host package container | Blob read/write for immutable upload and existing-package hash comparison. |
| Solution source container | Blob write/read for synthetic source uploads and metadata/version checks. |
| Solution ledger container | Blob read for request completion checks. |
| Solution work queue | Queue metadata read for readiness. |
| Existing destination container | Blob read for verified destination bytes. |

Set the optional `deploymentPrincipalObjectId` to let Bicep assign these container/queue roles to the approved pipeline identity. The deployment identity still needs separately granted authority to create role assignments; Contributor alone is insufficient. Alternatively, the platform can preassign data permissions and leave this parameter empty. Avoid broad subscription-wide data access. Runtime identity roles are separately defined by Bicep; see [security and RBAC](security-and-rbac.md).

Create a trusted Windows private agent pool `blob-transfer-private`. Install PowerShell 7.4+, Git and a current Azure CLI; allow outbound access to Azure DevOps, Entra and Azure management. Plan/apply use compiled artifacts and do not need a local Bicep compiler or .NET SDK. The Microsoft-hosted Windows qualification job installs the pinned SDK, Bicep and Node and downloads NuGet/npm tools; its agent capacity and package-feed access must also be available.

Ensure the private agent resolves/reaches the actual private storage endpoints. The automated probe covers four storage endpoints, not every host Queue/Table, DFS, app/SCM, uploader or telemetry path. Qualify those remaining paths during platform acceptance.

### 3. Configure Azure DevOps controls

1. Connect the Azure DevOps project to the GitHub repository. Register a **separate pipeline** using Existing Azure Pipelines YAML → `/azure-pipelines-self-service.yml`. Keep `/azure-pipelines.yml` as build/test/package CI.
2. Precreate the exact deployment environment named by each target profile. Baseline examples use `blobcopy-dev`, `blobcopy-qa`, `blobcopy-uat`, and `blobcopy-prod`; newly generated profiles include their naming suffix. Authorize only the intended pipeline and approvers. Do not depend on implicit environment creation.
3. Configure environment approvals, main-branch control and an **exclusive lock check**. YAML sets `lockBehavior: sequential`, but this only works when the resource has an exclusive lock configured. Approvals run for each apply stage.
4. Restrict each service connection and the private agent pool to the trusted deployment pipeline; add branch control/checks as appropriate. Do not enable access for all pipelines or permit untrusted PR jobs on the private pool. Service-connection checks can also pause the preview stages.
5. Protect GitHub `main` and require platform review for pipeline YAML/templates, target profiles, parameters, deployment scripts and Bicep. Restrict pipeline editing and queue-time variable overrides. A script branch check is not a security boundary against someone who can edit the scripts or resource permissions.
6. Set retention/access for deployment artifacts and logs. These contain infrastructure identifiers, configuration and synthetic transfer evidence; they are not public deliverables.

Microsoft documents [resource checks and approvals](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops) outside YAML and [AzureCLI task configuration](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-cli-v2?view=azure-pipelines). The repository cannot enforce missing organization settings by itself.

### 4. Enable the registered target

Update a schemaVersion 2 profile under `self-service/targets/` with the real subscription/RG and parameter path, or generate one from the [discovery report](subscription-discovery.md). Its subscription alias/network key map the developer selection to exact protected resources. Set `enabled: true` only after onboarding and review. Run locally from the repo root:

```powershell
./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

Review and merge the changes through the protected branch. Queue a dev run and complete live acceptance before enabling higher environments. Disabled targets fail early by design; they are not a runnable Azure demo configuration.

For another workload, add target JSON profiles and parameter files, then regenerate the catalog to populate dropdown values and compile-time protected-resource bindings. Create the corresponding external resources/controls first. Each workload/environment/subscription/network combination must be unique. The current catalog contains only `blobcopy`.

## Evidence, failures and reruns

| Artifact | Contents |
|---|---|
| `self-service-bundle` | `main.json`, `parameters.json`, `target.json`, `application.zip`, `functions.metadata`, and `bundle.json` with five file hashes, release ID and source commit. |
| `self-service-tests` | Project test/advisory output, offline deployment contract results, local Function host logs when those steps ran. VSTest results also appear in the Tests tab. |
| `plan-Foundation`, `plan-Release` | `summary.md`, `what-if.json`, `effective.parameters.json`, `plan.json`, and preview receipt. |
| `result-Foundation`, `result-Release` | Recheck preview, deployment outputs, success/failure receipt; release smoke evidence when completed. |

Hashes detect changes relative to the receipt; they are not signatures. Artifact access, the protected checkout and Azure DevOps authorization establish trust. What-if is a preview, not a guarantee of runtime success or a substitute for resource locks. State may change after the recheck; exclusive target ownership is still required.

- Drift, an expired preview, unknown what-if changes, altered package bytes or failed readiness stops the run. Inspect evidence and queue a fresh reviewed run after correction; do not bypass the guard.
- Failed applies can leave infrastructure or a deployed app in place. There is no automatic rollback, teardown, retention cleanup or cost-expiry job. Incremental mode does not make every allowed Modify change harmless; approvers must read the full preview.
- Existing stacks must have the expected `<workload>-<environment>` deployment history and output contract from this template. Missing history and changed source/ledger/queue names require a separate platform migration; arbitrary resource import is not implemented.
- Every run builds one release, frozen across its Foundation/Release stages. Selecting QA or Prod starts another build. **Promotion of the same package across separate environment runs is not implemented.**
- Automated smoke verifies ordinary uploads, overwritten source revisions, three completed request records and a shared hash-verified destination. It does not qualify complete poison routing, restart/load behavior, notification delivery, backups or all version-reconciliation scenarios.

For full production acceptance use [validation](validation.md), [operations](operations.md), and [completion status](completion-status.md).
