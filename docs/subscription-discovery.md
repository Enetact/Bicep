# Subscription discovery, dropdowns and standard names

The self-service pipeline now has five selections: **operation**, **workload**, **environment**, **subscription**, and **network profile**. Operation defaults to `discover`, which only reads inventory. Select `deploy` explicitly to enter the existing qualification/preview/approval/apply workflow.

The subscription dropdown uses a platform-owned friendly alias mapped to one exact subscription ID. Network profiles map to exact existing subnet and private DNS zone IDs, or the original new-network mode. Service connection, agent pool, deployment environment, naming and pipeline data permissions follow that approved combination.

The checked-in alias `unconfigured` is a placeholder, not a discovered subscription. No Azure DevOps organization or live subscription has been supplied or queried during this implementation.

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

The discovery script itself never installs an extension. In the pipeline, `System.AccessToken` is passed only to the Azure DevOps CLI through its environment; Azure authentication comes from the selected service connection. Install the extension on that agent first and grant the project Build Service only the required endpoint-read permission. Dynamic extension installation is disabled in the discovery job.

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

Review and fill the workload parameter file, precreate or select the target resource group, set up approvals/locks and permissions, and confirm agent connectivity. Set `enabled: true` only for deployment readiness. A disabled profile with real subscription/binding values may be used for discovery. Placeholder subscriptions fail before network enumeration.

Run `Update-ServiceCatalog.ps1` again after profile changes, review all generated diffs and merge them through protected `main`. `Test-Project.ps1` fails if generated files are stale. Invalid combinations of subscription/environment/network are not silently redirected: they fail lookup or protected-resource authorization. The built-in form shows independent dropdowns, not filtered cascading lists.

The catalog generator produces:

- `azure-pipelines-self-service.yml`: parameter values and discovery/deployment routing.
- `pipelines/catalog-bindings.yml`: compile-time mapping to exact protected service connection/pool/environment.

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

`Test-ServiceDiscovery.ps1` has 24 offline cases for scoped inventory, duplicate-name rejection, filtered endpoint discovery, profile/name/identity derivation, catalog freshness/ambiguity, and subnet/region/delegation/DNS failures. Azure CLI calls are mocked with a strict read-only allowlist. The existing 37 deployment cases still pass. All four environment templates compile with the new parameters. Live Azure discovery, generated YAML expansion on Azure DevOps, and an existing-network deployment remain unverified until a real organization/target is configured.
