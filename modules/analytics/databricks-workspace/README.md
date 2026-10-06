# Databricks Premium workspace

Optional `accessConnectorName` and `rootAccessConnectorName` bind the two connectors independently. Their original name-derived defaults remain.

Resource-group leaf module using `Microsoft.Databricks/workspaces@2026-01-01`. Creates a private Premium VNet-injected workspace, separate system-assigned root/lake access connectors and audit diagnostics. Enables root-storage firewall, no-public-IP connectivity and infrastructure encryption. The service owns its managed RG and root storage.

Inputs: resource name/location/tags, managed RG and root-storage names, existing VNet ID, host/container subnet names and Log Analytics ID. Outputs: workspace ID/name/URL and lake connector ID/principal ID. Dependencies, DNS, endpoint approvals, subnet NSGs/NAT, permissions and runtime qualification belong to the [Private Data Platform composition](../../../workloads/private-data-platform/README.md).

No clusters, account identities, Unity Catalog metastore/grants, browser-auth endpoint or data-plane calls are created here. The module is inventoried but excluded from generic AI source drafts until independently qualified. Shared-module compilation is local evidence only. Root-firewall changes to existing workspaces require migration review; this profile targets new owned workspaces.
