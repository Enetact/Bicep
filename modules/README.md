# Reusable resource modules

**Managed DevOps Pool module:** [the native pool resource](devops/managed-devops-pool/README.md) supports the eleventh workload through its reviewed product adapter. It is excluded from generic leaf-module source drafts, so the existing 23 inventoried/18 selectable draft contracts retain their scope. [Creation and onboarding](../workloads/managed-devops-pool/README.md).

**20 October 2026 — Fabric workload modules:** [capacity, workspace Private Link and private ADLS source](fabric/README.md) support the new tenth product. These workload-specific modules are not added to the generic AI source-draft selector; the 23 inventoried contracts below retain their existing scope.

The library contains **17 native resource contracts and six pinned AVM-backed wrappers: 23 inventoried modules, 18 selectable for source drafts**. The native and AVM inventories remain separate source-owned files. See [the AVM library](../docs/avm-module-library.md) for exact releases, child parameters, security settings and prerequisites.

The naming increment adds optional concrete names for derived plans, AKS identities/managed groups, Databricks connectors and workload networking. Legacy defaults remain. Workload compositions own the typed maps; generic modules do not recompute naming policy. See [naming](../docs/resource-naming.md).

The [native compiler-derived contracts](../config/bicep-module-contracts.json) inventory 17 shared Bicep files, with 12 draft-selectable. IPAM reservation stays in its separate governed workflow; private AKS and the three data-platform modules remain excluded from generic drafts until independently qualified. The [AVM contracts](../config/avm-module-contracts.json) add six selectable wrappers. The [source-draft workflow](../docs/bicep-source-drafts.md) merges both inventories and verifies their dependency closures. The [earlier module audit](../docs/plans/bicep-composition-and-module-audit.md) retains parameterization priorities; the six AVM wrappers implement only their documented subset. Run `./scripts/Update-BicepModuleContracts.ps1 -Check` after module changes; this also checks AVM provenance and contracts.

The native modules include [Key Vault](security/key-vault/README.md), [Service Bus](messaging/service-bus/README.md), [Observability](monitoring/observability/README.md), [private Functions hosting](compute/private-functions/README.md) and [private AKS](compute/private-aks/README.md). Workload compositions support eleven registered products. The AVM increment adds source-draft building blocks without changing those compositions, adding a product or enabling a target. Shared networking/DNS/workspaces remain externally owned; [onboarding](../docs/workload-onboarding.md) documents limits and acceptance.

The module browser and draft selector expose reusable source capabilities separately from [registered products](../docs/self-service-catalog.md). A new deployable product requires adapter, configuration, test and readiness work described in the [expansion plan](../docs/self-service-expansion-plan.md).

Native modules are locally maintained resource implementations. Project wrappers under `modules/avm/` consume exact official AVM compiled artifacts under `vendor/azure-verified-modules/`; the wrappers themselves are not certified AVM releases or published registry packages. Both receive explicit inputs and contain no environment files or target catalog lookups. Upstream files and licenses remain intact; project-owned wrappers constrain the exposed interface.

| AVM-backed wrapper | Exposed optional children | External dependencies |
|---|---|---|
| [User-assigned identity](avm/identity/user-assigned-identity/main.bicep) | Federated identity credentials | Reviewed issuer/subject trust; separate RBAC |
| [Container Registry](avm/containers/container-registry/main.bicep) | Geo-replications | Private endpoint/DNS, private clients and separate pull/push grants; Premium cost |
| [App Configuration](avm/configuration/app-configuration/main.bicep) | Non-secret key values and replicas | Client private endpoint/DNS; nonempty key values require data-plane write authorization and qualified ARM Private Link deployment access |
| [Application Insights](avm/monitoring/application-insights/main.bicep) | No optional service children exposed | Reviewed workspace, AMPLS/private DNS and Entra-enabled instrumentation |
| [Network security group](avm/network/network-security-group/main.bicep) | Typed custom security rules | New owned NSG; separate reviewed subnet attachment |
| [Route table](avm/network/route-table/main.bicep) | Typed local/gateway/drop routes and appliance routes | New owned table; separate subnet attachment and existing gateway/appliance qualification |

Child arrays are optional and empty by default. The bounded schema adapter retains child property/array constraints in generated Bicep types. It does not invent names, trust, rules, routes, secrets or deployment eligibility. See [all parameters and qualification requirements](../docs/avm-module-library.md#parameterized-children-and-bounds).

The native interfaces remain:

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

Blob-transfer-specific hosting, alerts, isolated networking and role assignments belong in [the workload](../workloads/blob-transfer/README.md). See the [repository conventions](../docs/repository-structure.md) before adding a module. Each shared module needs an explicit interface, documentation and compilation/behavior coverage. AVM version changes require reviewed upstream artifacts, provenance, dependency hashes and regenerated contracts; unused upstream capabilities are not automatically exposed.
