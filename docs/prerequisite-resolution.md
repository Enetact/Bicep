# Discover, reuse or create Event flow prerequisites

Event flow now resolves networking, private DNS and Log Analytics from the selected subscription's saved discovery. Discovery reads Azure; the conditional Bicep modules create missing prerequisites only when Deploy applies the workload Deployment Stack. This applies to `logic-app-event-grid`; Blob copy retains its existing network-profile behavior.

## Decisions saved with discovery

| Decision | Meaning | Deployment behavior |
|---|---|---|
| Create | A successful, complete lookup found no matching standard resource. | Declare the resource in the workload RG and Deployment Stack. |
| Reuse | A compatible matching or explicitly selected resource exists and is not owned by this stack. | Reference its ID without adopting or updating it. |
| Manage | The resource already belongs to this stack. | Keep its Bicep declaration on repeat runs; do not accidentally detach it. |
| Blocked | A listing failed, evidence is incomplete, selection is ambiguous, or configuration is incompatible. | Stop resolution/Preview. A failed lookup never means an empty subscription. |

Resolution checks the expected name and resource group. Other networks, zones and workspaces appear as candidates in the summary, not automatic substitutions. An existing workload RG still needs the established stack ownership checks; discovery does not authorize adopting an unrelated RG.

The `subscription-discovery` artifact contains `inventory.json`, the provenance manifest, `prerequisite-plan.json` and its summary. The plan is also embedded in `inventory.json`, whose hash is protected by the manifest. Preview and bundle creation recompute it against the selected target and current policy. Changed selections or policy require a fresh Discover run. Older artifacts retain existing-only behavior and cannot establish that missing resources should be created.

## What an empty subscription plans

For `eventflow/dev`, the new resources use `rg-eventflow-dev`:

| Resource | Name / settings |
|---|---|
| VNet | `vnet-eventflow-dev`, `10.70.0.0/16` |
| Integration subnet | `snet-integration`, `10.70.0.0/26`, delegated to `Microsoft.Web/serverFarms` |
| Endpoint subnet | `snet-private-endpoints`, `10.70.1.0/26`, private endpoint network policies disabled |
| Integration NSG | `nsg-vnet-eventflow-dev-integration`, Azure default rules; no custom rules |
| Private DNS zones | `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net`, `privatelink.table.core.windows.net`, `privatelink.file.core.windows.net`, `privatelink.azurewebsites.net`, `privatelink.eventgrid.azure.net` |
| Six DNS links | One `link-eventflow-dev` in each owned zone; registration disabled |
| Log Analytics | `log-eventflow-dev`, PerGB2018, 30-day retention |

These are **17 prerequisite decision rows**, including the inline subnets, in addition to the Logic App, plan, storage, Event Grid, endpoints, access and monitoring resources in the [workload runbook](../workloads/logic-app-event-grid/README.md). They are not 17 separate ARM deployment operations. Actual Create/Modify/NoChange details come from Azure What-If.

Review [config/logic-prerequisites.json](../config/logic-prerequisites.json) before discovery. QA, UAT and prod use `10.71`, `10.72` and `10.73` respectively with the same masks. Local validation rejects overlap within the proposed network and against inventoried VNets. It cannot establish absence of overlap with networks outside this subscription, on-premises networks or future peering. Those address allocations remain a platform choice.

The proposed [private networking self-service design](plans/private-networking-self-service.md) would replace repeated manual address selection with governed profiles, authoritative IPAM and a coverage-aware analyzer. It includes concurrency, DNS/routing, shared ownership and optional AI assistance. This is future work; the current resolver does not allocate from a pool or automatically select any available subnet.

## Select existing shared infrastructure

Keep the target's `networkProfile` selector as `central-private`; it is the registered routing key, not a promise that shared resources already exist. Put approved shared IDs in `parameterOverrides` in `self-service/targets/eventflow.<env>.json` and rerun Discover:

- Select `integrationSubnetId` and `privateEndpointSubnetId` together, in one compatible VNet in the scoped subscription and region.
- Set any selected `privateDnsZoneIds` keys (`blob`, `queue`, `table`, `file`, `sites`, `topic`) to actual discovered IDs. Unspecified keys use workload-owned standard names.
- Set `existingLogAnalyticsWorkspaceId` to an actual discovered workspace ID in the region.

Explicit shared IDs that are missing are **Blocked**, never automatically created at those IDs. An explicit value in the environment parameter file that differs from the saved plan is also rejected, not silently overwritten. Move that selection into the target overrides so discovery can evaluate it.

The current automatic resolver is scoped to one subscription. Cross-subscription resources require the older explicitly configured existing-only path and live access checks; they are not auto-resolved. Shared subnets must already be compatible. Reused zones must already have working links to the selected VNet; the apply precheck verifies this. Reusing a zone with a newly planned VNet is blocked because creating a link in that shared zone needs separate ownership review. Either select compatible existing networking or use the workload-owned zones.

## Run the two stages

Dev's owner/cost-center labels, deployment principal and exception decisions are now recorded in the [dev review document](reviews/eventflow-dev-exceptions.md). The following onboarding steps still apply to other environments and to any subsequent change in dev's authorization or identity.

1. Review names/CIDRs and any explicit shared selections. Keep the target disabled while configuring it.
2. Regenerate `MANIFEST.sha256`, commit the reviewed source and manifest, and run **Discover - Event flow** on the deployment branch using `/azure-pipelines-eventflow-discover.yml`.
3. Open its summary or artifact and review Reuse / Create / Manage / Blocked and the candidate list.
4. Run **Deploy - Event flow** using `/azure-pipelines-eventflow-deploy.yml`. Select that Discover run under **Resources > discovery** and leave **Run stages = Preview only**.
5. Review **Summary / Extensions > Bicep deployment preview**, or `deployment-preview/README.md`. It shows the prerequisite plan even if remaining onboarding fields block Azure What-If. A discovery decision is not an Azure validation result.
6. Supply real owner, cost center, deployment principal object ID and the two platform exception approvals/review references. Enable Deploy only after platform onboarding. Rerun Discover after any target change, including enablement, so the saved selection hash matches. Queue **Preview and deploy** with that fresh discovery for a new preview and protected deployment.

Deploy publishes the compiled Template Spec and applies it through the existing stack command. Foundation declares prerequisites before dependent workload modules. Its precheck permits only the saved planned creations to be absent. After Foundation, networking and private DNS must exist and work before Release proceeds. A live precheck rejects disappeared reused resources or a newly appeared resource at a planned Create ID without this stack's ownership. Stack preview, drift checks and deletion/security gates still apply.

This does not create ADO agents, environments, approvals, service connections, hub peering/routes, action groups or provider registrations. Private agents still need a network path and DNS resolution to the new VNet. Identity, ownership tags and platform review decisions cannot be inferred from an empty inventory.

## Implementation and evidence

| Method / file | Responsibility |
|---|---|
| `Export-DeploymentInventory.ps1` | Read scoped networks/DNS/resource catalog and stack ownership; save inventory, plan and summary. |
| `Get-LogicPrerequisitePlan` | Resolve names/IDs and lifecycle from successful evidence and reviewed policy; no Azure writes. |
| `Set-LogicDiscoveredPrerequisites` | Recheck saved decisions; populate effective IDs and creation flags; retain the plan report. |
| `Assert-LogicResolvedPrerequisites` | Reject altered policy, selection, flags or IDs in frozen inputs. |
| `Assert-LogicPrerequisiteLiveState` | Recheck existence and ownership immediately before preview/apply planning. |
| `Test-LogicPrerequisites -AllowPlannedCreates` | Permit planned absences for a fresh Foundation; retain provider checks and subsequent live checks. |
| `workloads/logic-app-event-grid/modules/prerequisites.bicep` | Compose conditional local network, DNS and workspace modules within the workload stack. |
| `Test-LogicPrerequisites.ps1` | Test empty/partial/failed inventories, reuse/ownership, stale and tampered plans, collisions and actual discovery entrypoint with mocked Azure. |

The Bicep composition follows [Microsoft's conditional deployment model](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/conditional-resource-deployment). Conditions retain stack-owned declarations on repeat runs; changing ownership or deleting resources remains a separately reviewed migration.

New resources are not free. The frozen cost estimate includes owned private DNS zone fixed charges in addition to the workload's WS1 and eight-endpoint base estimate. Log ingestion, storage, DNS queries, Event Grid, transfer, alerts and agents remain usage-dependent or excluded; consult the dated pricing reports. Local compilation and mocked contracts do not establish live Azure provisioning or connectivity. See [validation evidence](validation.md).
