# Blob-transfer composition helpers

These modules are internal to [the workload](../README.md). Their contracts encode its runtime and operational requirements; they are not advertised as generic AVM modules.

| File | Responsibility | Ownership boundary |
|---|---|---|
| `function-app.bicep` | Linux plan/Function App, package identity and dispatcher/worker settings | Workload compute; references existing integration subnet, identity and storage |
| `monitoring.bicep` | Application Insights, workspace creation or reference, blob-transfer alerts and operational roles | Existing shared workspace settings/RBAC remain external |
| `network.bicep` | Isolated VNet, subnets, NSG, five service zones and links | Only approved new-network exception; never a shared-hub deployment |
| `storage-access.bicep` | Host, source, ledger, queue and package identity grants | Scoped workload storage access |
| `destination-access.bicep` | Cross-RG/subscription destination container grants and outputs | References the external account/container; never creates or reconfigures them |

The parent composition supplies every environment value. Keep stable deployment names and GUID seeds when refactoring. A folder move must not silently alter resource identities or grant scopes. Detailed runtime permission/configuration contracts are in the [dispatcher guide](../../../docs/dispatcher/README.md).
