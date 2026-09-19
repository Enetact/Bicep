# Private storage account

Entry: [main.bicep](main.bicep), resource-group scope. Creates StorageV2 with identity access, public networking/shared keys disabled, Blob/Queue/Table service children, specified private containers/queues, versioning/retention and Blob/Queue diagnostics.

| Input | Type | Contract |
|---|---|---|
| `name`, `location` | string | Globally unique account name and region |
| `tags` | object | Workload ownership tags |
| `skuName` | string | `Standard_LRS` or `Standard_ZRS` |
| `containers` | array | Private Blob container names |
| `queueNames` | array | Queue names; default `[]` |
| `workspaceId` | string | Existing/previously composed Log Analytics workspace ID |

The fixed baseline is non-HNS, TLS 1.2+, HTTPS only, infrastructure encryption, no firewall bypass and 14-day Blob/container soft deletion. The workload provisions private endpoints and RBAC separately. This module does not create the external destination account.

Outputs are strings: `id`, `name`, `blobEndpoint`, `queueEndpoint`, and `tableEndpoint`, taken from the created account. `Test-Project.ps1` compiles this module through every environment and verifies wrapper parity. Runtime storage semantics also have separately run emulator tests; neither proves actual Azure networking or permissions.
