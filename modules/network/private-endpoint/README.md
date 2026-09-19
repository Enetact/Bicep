# Private endpoint

Entry: [main.bicep](main.bicep), resource-group scope. Creates a private endpoint and its `default` DNS zone group. Does not create or change the target service, subnet, zones, zone links or routing.

| Input | Type | Contract |
|---|---|---|
| `name`, `location` | string | Deterministic endpoint name and its region |
| `tags` | object | Optional, default `{}` |
| `subnetId` | string | Approved existing endpoint subnet |
| `targetResourceId` | string | Private Link target resource ID |
| `groupIds` | string[] | At least one target subresource |
| `privateDnsZoneIds` | string[] | At least one approved existing DNS zone ID |

Outputs: `privateEndpointId` (string) and `networkInterfaceIds` (array). The single-zone configuration name is preserved from `groupIds[0]`; multiple zones use deterministic IDs. Platform preflight validates supported subnet/zone combinations, ownership and effective connectivity separately. Compiling this module does not prove DNS works.

Consumed by `workloads/blob-transfer/main.bicep`. `Test-Project.ps1` compiles all environment compositions; stack governance tests reject shared-resource ownership and removal. This module has not been independently live-qualified as a general-purpose public package.
