# Subscription discovery, dropdowns and standard names

The self-service pipeline now has five selections: **operation**, **workload**, **environment**, **subscription**, and **network profile**. Operation defaults to `discover`, which only reads inventory. Select `deploy` explicitly to enter the existing qualification/preview/approval/apply workflow.

The subscription dropdown uses a platform-owned friendly alias mapped to one exact subscription ID. Network profiles map to exact existing subnet and private DNS zone IDs, or the original new-network mode. Service connection, agent pool, deployment environment, naming and pipeline data permissions follow that approved combination.

The checked-in alias `unconfigured` is a placeholder, not a discovered subscription. All four baseline profiles bind the user-supplied service connection `SC-AZ-A-Bicep`; its actual subscription and permissions have not been queried here.

To see the options, push this source to GitHub and register/select the Azure DevOps pipeline using `/azure-pipelines-self-service.yml`, then open **Run pipeline** on a branch containing these files. The build/test pipeline `/azure-pipelines.yml` does not expose self-service parameters. Authorize this pipeline to use `SC-AZ-A-Bicep`. The options appear from YAML before Azure authentication; the connection name does not populate real subscription or network choices. Read-only discovery permits manually queued repository branches, including feature branches. Deployment still requires a manually queued `main` run. Deployment profiles remain disabled pending onboarding.

If Inventory reports **Condition was not met** on a feature branch using the earlier YAML, its parent Discover stage was restricted to `refs/heads/main`; neither login nor inventory ran. Push the updated template to that feature branch and start a **new Run pipeline** using the same discovery parameters. Retrying an old run uses its original source revision. Debug/diagnostic variables do not change the stage condition. Service-connection resource checks remain separate and may impose their own branch restrictions.

For the first run, select `discover`, `blobcopy`, `dev`, `unconfigured`, and `new-private`. The Azure CLI task signs in through `SC-AZ-A-Bicep`; discovery reads `az account show` to obtain that connection's selected subscription. No manual subscription ID is needed for this disabled placeholder. It then scopes network queries explicitly to the discovered ID and publishes the subscription name/ID and network inventory in `subscription-discovery` and the run summary. It does not alter the catalog or enable deployment. A profile with a real subscription ID must match the connection's current subscription; mismatches fail instead of switching subscriptions.

Read-only discovery runs on `windows-latest` and requires available Microsoft-hosted agent capacity. It uses management APIs, so the private deployment pool is not needed for this step. Deployment still uses the target's private pool. Azure DevOps service-connection listing is optional and may report a warning if the hosted image lacks the azure-devops CLI extension or the Build Service lacks endpoint-read access; subscription/network discovery does not depend on that listing.

## Can Contributor or Owner refresh the dropdowns?

They can authorize Azure resource discovery. **They cannot change the already displayed Azure DevOps Run Pipeline form.** YAML parameter choices are resolved before jobs execute, and service connections are authorized before the stage starts. Running a script cannot populate a cascading dropdown in that submitted form. See Microsoft's [runtime parameter timing](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/runtime-parameters?view=azure-devops) and [pipeline processing order](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/runs?view=azure-devops).

This implementation uses two steps:

1. **Discover:** the selected, already authorized service connection reads only the selected subscription's networks/subnets/DNS zones. Download `subscription-discovery/inventory.json`.
2. **Register and refresh:** the platform owner chooses exact resources from the report, generates a disabled target profile, reviews it, regenerates the YAML dropdowns/bindings and merges the change. The next Run Pipeline form shows the new choices.

There is no automatic push, self-modifying pipeline, service connection creation, role grant or deployment in discovery. True live cascading selectors require a portal or Azure DevOps extension querying Azure before queueing a deployment; that UI is not implemented.

Subscription Owner does not grant Azure DevOps project permissions. Listing project service connections requires separate endpoint-read access, and authorizing this pipeline to use a connection is another platform action. The runtime connection cannot discover the credential it needs to authenticate its own first Azure task: at least one approved connection mapping is a prerequisite.

## First discovery without an Azure DevOps project

Use an existing Azure CLI login with read access. From the repository root:

```powershell
az login
az account list --query "[?state=='Enabled'].{Name:name,Id:id}" --output table

./scripts/Export-DeploymentInventory.ps1 `
  -SubscriptionName 'Your exact subscription name' `
  -OutputDirectory './artifacts/discovery/selected-subscription'
```

Name matching is exact. Duplicate names are rejected; use `-SubscriptionId '<actual-guid>'` instead. The script does not change your default subscription and supplies the selected ID on resource-list calls. Account-name lookup reads accessible subscription metadata; network enumeration is confined to the chosen subscription. Reports contain IDs/configuration but no storage keys, access tokens or endpoint credential payloads.

Once an Azure DevOps project exists, install/authenticate its CLI extension separately and optionally discover matching service connections:

```powershell
az extension add --name azure-devops
# Authenticate using your organization's approved Azure DevOps CLI method.
./scripts/Export-DeploymentInventory.ps1 `
  -SubscriptionId '<actual-subscription-guid>' `
  -OrganizationUrl 'https://dev.azure.com/<organization>' `
  -Project '<project-name>' `
  -OutputDirectory './artifacts/discovery/with-devops'
```

The discovery script itself never installs an extension. In the pipeline, `System.AccessToken` is passed only to the Azure DevOps CLI through its environment; Azure authentication comes from the selected service connection. Endpoint enumeration requires the extension to be present on the agent and the project Build Service to have endpoint-read permission. Dynamic extension installation is disabled in the discovery job; unavailable endpoint enumeration becomes a report warning.

When endpoint or directory discovery is unavailable, the report records a warning and does not invent a connection/principal. The tenant and subscription must match. The exported endpoint fields are projected to ID, name, ready state, subscription, scheme, application ID and principal object ID. An application/client ID must never be substituted for the principal object ID used by role assignments.

## Register an existing network

Read `inventory.json` and select two distinct subnets from the same VNet. Then run:

```powershell
./scripts/New-ServiceTarget.ps1 `
  -InventoryPath './artifacts/discovery/with-devops/inventory.json' `
  -Workload blobcopy -EnvironmentName dev `
  -SubscriptionAlias engineering-dev -NetworkProfile shared-eastus2 `
  -IntegrationSubnetId '<exact-integration-subnet-resource-id>' `
  -PrivateEndpointSubnetId '<exact-private-endpoint-subnet-resource-id>' `
  -ServiceConnectionId '<exact-azure-devops-endpoint-guid>' `
  -OrganizationCode acme -RegionCode eus2 -Instance 001 `
  -AgentPool blob-transfer-private

./scripts/Update-ServiceCatalog.ps1
./scripts/Test-Project.ps1
./scripts/Update-Manifest.ps1
./scripts/Update-Manifest.ps1 -Check
```

This writes `self-service/targets/blobcopy.dev.engineering-dev.shared-eastus2.json` with `enabled: false`. It refuses to overwrite an existing profile. The selected connection must be ready and use workload identity federation. If directory lookup could not resolve its identity, the platform owner can supply the independently verified `-DeploymentPrincipalObjectId`. A mismatch with a discovered object ID is rejected.

DNS zones are matched by their full Azure public-cloud names. Missing or ambiguous zones require an explicit `-PrivateDnsZoneIds` hashtable with `blob`, `queue`, `table`, `dfs`, and `web` IDs; use this for approved centralized zones in another subscription. The subnet pair must be in the selected application subscription; cross-subscription VNet integration is outside this blueprint's current contract.

Review and fill the workload parameter file, precreate or select the target resource group, set up approvals/locks and permissions, and confirm agent connectivity. Set `enabled: true` only for deployment readiness. Disabled profiles can run read-only discovery. Only the explicit service-connection discovery path permits the disabled `unconfigured` placeholder; deployment and ordinary profile validation still reject it.

Run `Update-ServiceCatalog.ps1` again after profile changes, review all generated diffs and merge them through protected `main`. `Test-Project.ps1` fails if generated files are stale. Invalid combinations of subscription/environment/network are not silently redirected: they fail lookup or protected-resource authorization. The built-in form shows independent dropdowns, not filtered cascading lists.

The catalog generator produces:

- `azure-pipelines-self-service.yml`: parameter values and a call to the generated stage router.
- `pipelines/catalog-bindings.yml`: conditional stage routing that passes literal service connection/pool/environment values as explicit template parameters. Nested templates forward these parameters; they do not read implicit parent variables. Invalid combinations produce a failing validation stage before Azure access.

Reusable deployment steps live in `pipelines/templates/self-service-stages.yml`; edit that template rather than the generated root YAML. The old per-workload binding file is superseded by the generated catalog.

## Naming contract

New registered profiles derive names from these tokens:

| Token | Rule / example |
|---|---|
| Workload | 3–10 lowercase letters/digits: `blobcopy` |
| Environment | `dev`, `qa`, `uat`, `prod` |
| Organization | 2–4 lowercase letters/digits, starts with a letter: `acme` |
| Region code | 2–5 lowercase letters/digits, starts with a letter: `eus2` |
| Instance | Three digits: `001` |

`namingSuffix = acme-eus2-001` is capped at 14 characters. The selected VNet supplies the actual Azure `location`; region codes are organizational labels, not an automatic region-name resolver.

| Resource | Derived name |
|---|---|
| Name stem / deployment environment | `blobcopy-dev-acme-eus2-001` |
| Default resource group | `rg-blobcopy-dev-acme-eus2-001` (must already exist; override with `-ResourceGroup` when approved) |
| Function App | `func-blobcopy-dev-acme-eus2-001-<unique>` |
| App Service plan | `asp-blobcopy-dev-acme-eus2-001` |
| Managed identity | `id-blobcopy-dev-acme-eus2-001-<unique>` |
| New VNet, when using new-network mode | `vnet-blobcopy-dev-acme-eus2-001` |
| Host and solution storage | `sthdev<unique>` and `studev<unique>`; compact lowercase names satisfy storage limits. |
| Existing service connection and network | Reused by exact ID; never renamed or guessed from a similar display name. |

The uniqueness token is deterministic from subscription, RG, workload, environment and optional naming suffix. Existing deployments with an empty suffix preserve their original name expressions. Changing or removing a suffix on an existing stack is rejected by self-service and requires a migration decision. Naming does not discover resources: resource IDs and deployment outputs are authoritative.

## Network checks and permissions

Before each plan/apply, existing-network mode checks:

- Same-region VNet; separate subnets in that VNet and in the selected subscription.
- Integration subnet delegated only to `Microsoft.Web/serverFarms`, IPv4 `/26` or larger under this blueprint's standard, with no private endpoints.
- Private endpoint subnet has no delegation and private endpoint network policies are Disabled under this blueprint's standard.
- All five existing DNS zones are readable and have successful links to the selected VNet.

The template reuses those IDs and does not redeploy the shared VNet/subnets/zones or alter their delegation. Private endpoint creation still consumes subnet IPs and updates records through zone groups. Inventory candidate flags do not prove free capacity, permitted NSGs/routes, service endpoint policy compatibility, egress or live DNS. Platform approval and live checks remain required. Microsoft's [VNet integration requirements](https://learn.microsoft.com/en-us/azure/app-service/overview-vnet-integration) describe the Azure prerequisites; this blueprint intentionally uses a conservative `/26` minimum.

Permissions follow `deploymentPrincipalObjectId` and the selected storage resources. Optional Bicep assignments grant package/source Blob Data Contributor, ledger/destination Blob Data Reader and work-queue Storage Queue Data Reader at their individual container/queue scopes. These are separate from runtime identity assignments. See Microsoft's [storage role definitions](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage).

These assignments do **not** bootstrap their own authority. Contributor can read/provision many resources but cannot create role assignments; the deployer needs separately approved role-assignment rights, or the platform must preassign data roles and leave `deploymentPrincipalObjectId` empty. Owner can grant roles within its scope, subject to policy/deny constraints. Cross-subscription destination/DNS resources need their own permissions. Shared-network subnet join and DNS zone access also must be granted separately. Azure DevOps connection use, endpoint discovery, approvals and Git changes use Azure DevOps/GitHub permissions, not Azure RBAC.

Discovery includes available subscription-level ARM permission evidence, but makes no claim to calculate effective access across inherited/conditional grants, deny assignments or data-plane operations. The deployment's what-if and actual readiness probes remain the execution checks.

## Verification

`Test-ServiceDiscovery.ps1` has 34 offline cases for scoped inventory, service-connection subscription lookup, binding/subscription mismatch rejection, disabled-placeholder isolation, duplicate-name rejection, filtered endpoint discovery, profile/name/identity derivation, catalog freshness/ambiguity, explicit template bindings and relative paths, and subnet/region/delegation/DNS failures. Azure CLI calls are mocked with a strict read-only allowlist. The existing 37 deployment cases still pass. All four environment templates previously compiled with the new parameters. A first live discovery attempt reached the Azure CLI task but failed before authentication because its expanded connection input was empty; the explicit-parameter fix awaits a new live run. See [validation](validation.md).
