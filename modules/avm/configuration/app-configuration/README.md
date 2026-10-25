# AVM private App Configuration

[main.bicep](main.bicep) wraps official `avm/res/app-configuration/configuration-store:0.10.1` from [pinned upstream source](https://github.com/Azure/bicep-registry-modules/tree/f1f4ba28698437d9a683d68bf181341ad3547e48/avm/res/app-configuration/configuration-store). Exact compiled ARM and the original MIT license are preserved in [the verified bundle](../../../../vendor/azure-verified-modules/bundle.json). This constrained wrapper is locally maintained and is not independently AVM certified.

| Input | Contract |
|---|---|
| `name`, `location`, `tags` | Required explicit globally unique name, approved region and ownership tags. Draft names stay unresolved; validate naming and availability. |
| `skuName` | `Standard` by default or reviewed `Premium`; no Free/Developer downgrade. |
| `softDeleteRetentionInDays` | 1-7, default 7, with mandatory purge protection. |
| `keyValues` | Up to 100 sealed non-secret children; default `[]`. Each requires `name`, `value` and `contentType` (`text/plain` or `application/json`). Tags inherit the parent. No arbitrary extra properties. |
| `replicaLocations` | Up to 10 sealed replicas; default `[]`. Each requires explicit `name` and `replicaLocation`; review tier/region compatibility. |

Owns the new store and requested children; outputs `id`, `name` and `endpoint`. Local/key authentication and public network access are disabled. ARM data access uses `Pass-through` authentication with private-link delegation enabled. Telemetry is disabled. No credentials, arbitrary roles, private endpoints or DNS are created. Key-values are visible in source, parameter and deployment artifacts: never supply secrets. Secret references require a separately reviewed runtime integration.

The upstream parameter description still mentions local authentication for key-values. The wrapper instead follows Microsoft's [current pass-through deployment guidance](https://learn.microsoft.com/en-us/azure/azure-app-configuration/quickstart-deployment-overview): nonempty `keyValues` require both management permissions and App Configuration data-plane write permission. A private store additionally needs Azure Resource Manager Private Link, deployment through that link, and qualified store endpoint/DNS connectivity. Enabling delegation alone does not establish that network path. Missing prerequisites block qualification; do not enable public/local authentication as a fallback.

Review unique keys/replica names, value syntax, authorization propagation, replicas, throughput and paid tier/replication charges. Existing stores are not adopted by supplying their names. Follow [qualification and protected Preview/Deploy](../../../../docs/bicep-source-drafts.md#qualification-and-promotion); local compilation does not establish Azure data-plane, private connectivity or runtime acceptance.
