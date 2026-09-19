# Reusable resource modules

These are locally maintained, private-by-default modules, not Azure Verified Modules or published registry packages. They receive explicit inputs and contain no environment files or target catalog lookups.

| Module | Owns | References only |
|---|---|---|
| [Private endpoint](network/private-endpoint/README.md) | Endpoint and DNS zone group | Target resource, subnet, private DNS zones |
| [Storage account](storage/storage-account/README.md) | Storage account, service/container/queue children, diagnostics | Log Analytics workspace |

Blob-transfer-specific hosting, alerts, isolated networking and role assignments belong in [the workload](../workloads/blob-transfer/README.md). See the [repository conventions](../docs/repository-structure.md) before adding a module. Each shared module needs an explicit interface, documentation and compilation/behavior coverage; adopting an AVM is a separately reviewed implementation change.
