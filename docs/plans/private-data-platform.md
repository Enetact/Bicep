# Private Data Platform delivery plan

Status: source implementation complete, 4 October 2026; live Azure acceptance remains outstanding. No Azure execution or target enablement is authorized by this plan.

1. Add leaf modules for Databricks Premium (`2026-01-01`), ADLS Gen2 and Data Factory v2, then compose an owned resource group through the existing subscription stack/Template Spec contract.
2. Reuse approved same-region subnets, DNS and monitoring. Validate separate Databricks host/container delegations, NSGs, explicit egress and a shared regional browser-authentication endpoint. Do not invent free address space or create another shared auth endpoint.
3. Register four disabled environment targets, dedicated Discover/Deploy menus, portal description/proposed diagram, provider inventory, immutable artifact routing and a conservative recovery policy.
4. Validate saved discovery and recheck live prerequisites during Preview and Deploy. Verify actual private endpoint approval and service configuration after deployment; report missing approvals explicitly. Do not silently approve external endpoint requests.
5. Provide a manual-only notebook pipeline and linked services using the factory identity. Document separate notebook import, workspace identity/compute permissions, Unity Catalog metastore/storage credentials/external locations/grants and controlled first-run acceptance. No PATs, automatic compute runs, or fabricated account-level permissions.
6. Compile modules/compositions/environment parameters, test malformed/scope/drift cases and run existing catalog, pipeline, portal and product regressions. Update source contracts, wiki and manifest. Preserve a clear distinction between local verification and live acceptance.

Initial supported topology: one approved Azure-provided-DNS VNet, two dedicated Databricks subnets, one endpoint subnet, existing NAT gateway on both compute subnets, direct private DNS links and existing browser-auth endpoint in that VNet/region. This intentionally blocks unqualified hub/custom-DNS/firewall profiles rather than inferring connectivity. Cross-subscription networking, serverless NCC, CMK, regulated compliance add-ons and runtime/UC automation are future profiles requiring their own qualification.

References reviewed: [workspace API](https://learn.microsoft.com/en-us/azure/templates/microsoft.databricks/2026-01-01/workspaces), [VNet injection](https://learn.microsoft.com/en-us/azure/databricks/security/network/classic/vnet-inject), [inbound private connectivity](https://learn.microsoft.com/en-us/azure/databricks/security/network/front-end/front-end-private-connect), [ADF managed networking](https://learn.microsoft.com/en-us/azure/data-factory/managed-virtual-network-private-endpoint), [ADF compute linked services](https://learn.microsoft.com/en-us/azure/data-factory/compute-linked-services), [Unity Catalog managed identity](https://learn.microsoft.com/en-us/azure/databricks/connect/unity-catalog/cloud-storage/azure-managed-identities).

## Delivered boundary

Steps 1–6 are implemented locally; see the [workload guide](../../workloads/private-data-platform/README.md) and [validation log](../validation.md) for exact checks. The composition includes root-storage firewall and seven workload endpoints in addition to three managed ADF endpoints. The existing workload advisor and factual/proposed/What-If diagram paths are reused; no new autonomous agent or unrestricted MCP write tool is introduced. The three new module contracts are inventoried but excluded from generic source drafts pending separate qualification.

## Next qualification phases

1. Execute the documented manual Azure acceptance: dependencies, Preview, protected deployment, exact endpoint approvals, workspace identity, UC, imported notebook and controlled data-flow test.
2. Design a separately governed data-plane bootstrap for workspace identities, metastore/storage credentials/external locations, grants, compute policies and notebook artifacts. Bind receipts to the same reviewed scope and preserve unrelated objects.
3. Add qualified hub/firewall/custom DNS and cross-subscription profiles, shared ADF portal endpoint reuse, CMK/compliance, serverless NCC and regional cost snapshots.
4. Add operational backup/recovery and production SLO qualification; never infer data rollback from stack deployment.

## Manual recipe increment

Implemented manual ADF staging/native Job launch and a parameterized Databricks validate/curate/verify bundle with a local setup exporter. See [recipes](../../workloads/private-data-platform/recipes.md) and the dated validation log. Workspace publication is still an explicit operator action; a protected ADO data-plane publishing adapter, native bundle/live execution acceptance and business-specific incremental ingestion remain future phases.
