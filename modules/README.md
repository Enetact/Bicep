# Reusable resource modules

**AKS/Helm increment — 4 October 2026:** The catalog now includes `compute/private-aks/main.bicep` (17 modules total). Its specialized network/identity profile is excluded from generic AI draft selection; 12 modules remain draft-selectable. See [the implementation and onboarding guide](../workloads/private-aks/README.md).

The [compiler-derived module contracts](../config/bicep-module-contracts.json) inventory all 16 shared Bicep files. The [source-draft workflow](../docs/bicep-source-drafts.md) can compose 12; IPAM reservation stays in its separate governed workflow; the three data-platform modules remain excluded from generic drafts until independently qualified. The [module audit](../docs/plans/bicep-composition-and-module-audit.md) lists current fixed settings, missing identity/access/DNS-link/monitoring modules and parameterization priorities. These planned additions are not implemented merely by appearing in the audit. Run `./scripts/Update-BicepModuleContracts.ps1 -Check` after module changes.

The five-product expansion also adds [Key Vault](security/key-vault/README.md), [Service Bus](messaging/service-bus/README.md), [Observability](monitoring/observability/README.md) and [private Functions hosting](compute/private-functions/README.md). Together with the existing modules these compose eight registered offerings. Shared networking/DNS/workspaces remain externally owned; [onboarding](../docs/workload-onboarding.md) documents limits and acceptance.

These modules compose the [registered products](../docs/self-service-catalog.md). They are not independently selectable catalog items; new products require adapter, configuration, test and readiness work described in the [expansion plan](../docs/self-service-expansion-plan.md).

These are locally maintained, private-by-default modules, not Azure Verified Modules or published registry packages. They receive explicit inputs and contain no environment files or target catalog lookups.

| Module | Owns | References only |
|---|---|---|
| [Databricks Premium](analytics/databricks-workspace/README.md) | Private workspace, root/lake connectors, diagnostics | VNet/subnets, monitoring, provider-managed RG |
| [Data Factory v2](analytics/data-factory/README.md) | Factory, managed VNet/IR/endpoints, linked services, manual notebook pipeline | Databricks, ADLS, monitoring, endpoint approvals |
| [ADLS Gen2](storage/data-lake/README.md) | Private HNS storage, landing/curated, diagnostics | Monitoring; composition supplies RBAC and endpoints |
| [AVNM IPAM reservation](network/README.md) | One durable static CIDR allocation | Existing network manager and IPAM pool |
| [Private endpoint](network/private-endpoint/README.md) | Endpoint and DNS zone group | Target resource, subnet, private DNS zones |
| [Storage account](storage/storage-account/README.md) | Storage account, service/container/queue children, diagnostics | Log Analytics workspace |
| [Workload VNet](network/workload-vnet/README.md) | VNet, two subnets and integration NSG | Reviewed address allocation |
| [Private DNS zone](network/private-dns-zone/README.md) | Zone and VNet link | Selected VNet |
| [Log Analytics](monitoring/log-analytics/README.md) | Dedicated workspace | Explicit region/name/tags |
| [Event Grid topic](event-grid/topic/README.md) | Topic and its identity | Reviewed resource inputs |
| [Event subscription](event-grid/event-subscription/README.md) | Delivery subscription | Topic, queue and dead-letter destinations |
| [Logic App Standard](logic-app/standard/README.md) | Plan/site and runtime configuration | Runtime storage, integration subnet and monitoring |

Blob-transfer-specific hosting, alerts, isolated networking and role assignments belong in [the workload](../workloads/blob-transfer/README.md). See the [repository conventions](../docs/repository-structure.md) before adding a module. Each shared module needs an explicit interface, documentation and compilation/behavior coverage; adopting an AVM is a separately reviewed implementation change.
