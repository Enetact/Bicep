# Network modules and IPAM reservation

The existing private endpoint, workload VNet and private DNS modules retain their own subdirectory READMEs. `ipam-reservation.bicep` is a resource-group-scoped module for a single `Microsoft.Network/networkManagers/ipamPools/staticCidrs@2025-07-01` resource. It references an existing manager/pool; it does not create or choose the pool's root range.

Inputs are `networkManagerName`, `poolName`, `allocationName`, `ownerDescription` and `addressCount` (the current adapter supplies 256). Outputs are the resource ID and provider-assigned prefixes. The description stores the reviewed intent hash. The caller must validate provider completion and prefixes before using them. This module alone provides neither workflow locking nor authorization.

The controlled caller is `scripts/Invoke-NetworkAllocation.ps1`, reached through `azure-pipelines-network.yml`. It plans, reserves and reconciles one stable identity; failed or ambiguous operations keep space held. Pool-wide locking/approval checks are configured by the platform owner. No automatic release is implemented.

The separately owned composition in `platform/network/reserved-spoke.bicep` consumes the returned exact /24 and creates a VNet with integration /26 and private-endpoint /27 subnets. It is not a general reusable network module: it represents this specific reviewed layout and ownership boundary. Hub/DNS/routing integration and workload admission remain separately qualified.

See [setup, methods, safety boundaries and manual acceptance](../../docs/network-discovery-and-diagrams.md). Local module compilation and offline contracts do not establish Azure allocation acceptance.
