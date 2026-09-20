# Workload private DNS zone

Creates a zone and a nonregistering VNet link in the current resource group. Inputs: `name`, `tags`, `virtualNetworkId`, `linkName`. Output: zone `id`. Event flow composes six instances for Blob, Queue, Table, File, sites and Event Grid. The selected VNet must exist or be a deployment dependency.

Use only for a new or already stack-owned zone. This module does not modify shared zones or infer links for them. Private endpoint DNS zone groups populate endpoint records. See [prerequisite resolution](../../../docs/prerequisite-resolution.md).
