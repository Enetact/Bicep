# Microsoft Fabric premium analytics workspace

Fabric joins the existing Platform Studio product flow. Select **Fabric Premium Workspace** and an environment, collect saved discovery, then run the dedicated Deploy definition in **Preview only** mode. Azure resources use the existing Bicep, Template Spec, Deployment Stack, ownership, drift and protected deployment controls. All four Azure targets and all four Fabric API manifests ship disabled. No tenant, capacity, workspace or connection was changed during authoring.

## What this delivers

| Layer | Implemented source | Ownership and evidence |
|---|---|---|
| Azure foundation | `azure-pipelines-fabric-discover.yml`, `azure-pipelines-fabric-deploy.yml`; `main.bicep`, `stack.bicep` | Workload-owned F64 capacity by default, explicit capacity administrators and platform tags. Existing monitoring destination remains a reference. |
| Fabric control plane | `azure-pipelines-fabric-workspace.yml`; `scripts/Invoke-FabricWorkspace.ps1` | Separate typed API Preview, protected Apply and read-back. Workspace, lakehouse, workspace identity, admin group, reviewed ADLS/SQL connection, managed endpoint, native deployment pipeline, stage assignment and selected-item promotion operations. |
| Private inbound access | `modules/fabric/workspace-private-link/main.bicep` | Optional second ARM pass for an **existing** Fabric workspace, using an approved endpoint subnet and existing Fabric private DNS zone. |
| Private source example | `modules/fabric/private-adls-source/main.bicep` | Separately deployed private ADLS Gen2 source with Blob/DFS endpoints and container-scoped reader permission for an observed workspace identity. No business data is created. |
| Agent assistance | Connections → Fabric → MCP orchestration | Official remote Fabric Core MCP, alongside Bicep and Microsoft Learn MCP. Uses host-held identity, discovered schemas and exact approval for changing, sensitive or unclassified calls. |

Bicep manages Azure resources; workspace/items/connections and native ALM use the [Fabric REST APIs](https://learn.microsoft.com/en-us/rest/api/fabric/core/workspaces/create-workspace). They are deliberately separate lifecycle/receipt domains. A capacity Azure resource ID is **not** the Fabric capacity UUID returned by the [capacities API](https://learn.microsoft.com/en-us/rest/api/fabric/core/capacities/list-capacities).

## Premium capacity and licensing

The initial reviewed profile accepts F64, F128, F256, F512, F1024 and F2048, with F64 as its default. Review regional availability, subscription quota and measured concurrency before enabling a target. This is an F-capacity-backed workspace, not a Premium Per User purchase. F64 is a profile choice for premium distribution requirements; smaller F SKUs can support Fabric workloads, and authoring/sharing still has separate Power BI user licensing requirements. See [Fabric licenses](https://learn.microsoft.com/en-us/fabric/enterprise/licenses).

Capacity compute, OneLake storage, networking, source systems, gateways, monitoring and license charges require a reviewed estimate. The menu reports unavailable pricing rather than a zero estimate. Dev/test can use a separately reviewed smaller-capacity profile later; this implementation does not automatically suspend, scale, reserve or share production capacity.

## Standard deployment sequence

1. Complete `workloads/fabric/environments/main.<env>.bicepparam` and `self-service/targets/fabric.<env>.json`: subscription, owner, cost center, explicit capacity administrator user identities, deployment principal and existing Log Analytics reference. `capacityAdministrators` is the ARM capacity administration contract; workspace access is managed separately with Entra groups. Enable only after platform qualification.
2. Register the three Fabric YAML roots through the existing **ADO setup** workflow. Configure WIF service connection, the approved private agent pool, protected environments, branch checks and exclusive lock externally. Register `Microsoft.Fabric` and any network/storage providers through platform administration. Registration and administrator consent are not performed by discovery.
3. Run **Discover - Fabric Premium Workspace**, then **Deploy - Fabric Premium Workspace → Preview only** against that saved successful run. Review Azure What-If, cost uncertainty, scope, retained shared resources and ownership before protected deployment.
4. Resolve the actual active Fabric capacity UUID with the Fabric API. Review the matching capacity display name, SKU and region against the ARM output; do not derive a UUID from an ARM ID. Complete `self-service/fabric/workspace.<env>.json` with the intended tenant, subscription, execution principal object ID, capacity UUID and exact capacity display name.
5. Run **Fabric - Workspace Preview and Apply → Preview only**. It collects API preconditions and saves a 30-minute plan bound to the source manifest bytes, tenant, principal and observed state. After reviewing the concrete plan, use the protected Apply path. The API plan is distinct from ARM What-If. Source `enabled: false` blocks Apply.
6. Record returned IDs from the receipt in source review, then replace the manifest's `operation` with one reviewed operation from `examples/`. Run a fresh Preview/Apply/Verify for each step: admin group, identity, lakehouse, optional source connections/endpoints, native deployment pipeline and explicit stage assignment. No guessed IDs or substitutions from previous write results enter an approved plan.
7. For inbound private access, set `enableWorkspacePrivateLink: true`, the observed `existingFabricWorkspaceId`, approved subnet and `privateDnsZoneIds.fabric` in the Azure source parameters. Re-run saved discovery/Preview and the protected Azure deployment. The ARM adapter verifies this explicit external workspace ID; it does not independently map its Fabric capacity UUID back to the workload ARM capacity. Retain the separate Fabric workspace Verify receipt and review that binding. Validate endpoint approval, DNS and actual client connectivity before changing public communication policy through a separately reviewed Fabric action.

The API script exposes a small code-allowlisted operation set. It does not accept arbitrary URLs, scripts, bodies, secret values, delete operations or deploy-all flags. Apply re-collects evidence, rejects drift/expiry/identity changes and creates attempt markers before its single write. Source and paired target item definition hashes bind promotion content; raw definitions are not retained in plans. Plans also bind the wrapper/common source bytes and ADO build/commit when present; stage/job retries are blocked before authentication. Receipts start as `UnknownOutcome`; a timeout or transport failure requires manual state reconciliation and a fresh Preview. A cancelled job is not rollback. Keep failed-run artifacts and Fabric operation history before another attempt. Markers are retained with receipts, but this is not an exactly-once guarantee across fresh agents/runs: reconcile the previous operation before approving another Preview.

For an already reviewed manifest, the equivalent CLI sequence is:

```powershell
./scripts/Invoke-FabricWorkspace.ps1 -Mode Preview -ManifestPath self-service/fabric/workspace.dev.json -PlanPath artifacts/fabric-api/plan.json -ReceiptPath artifacts/fabric-api/receipt.json
$digest = (Get-FileHash artifacts/fabric-api/plan.json -Algorithm SHA256).Hash.ToLowerInvariant()
# Run through the protected ADO Apply path for governed environments.
./scripts/Invoke-FabricWorkspace.ps1 -Mode Apply -ManifestPath self-service/fabric/workspace.dev.json -PlanPath artifacts/fabric-api/plan.json -ReceiptPath artifacts/fabric-api/receipt.json -ExpectedPlanSha256 $digest
./scripts/Invoke-FabricWorkspace.ps1 -Mode Verify -ManifestPath self-service/fabric/workspace.dev.json -PlanPath artifacts/fabric-api/plan.json -ReceiptPath artifacts/fabric-api/receipt.json -ExpectedPlanSha256 $digest
```

The checked-in manifests contain placeholders and intentionally fail qualification. Local CLI execution does not configure or prove ADO environment approval.

## Private networking: choose the release profile first

**Native deployment pipelines currently cannot connect to a stage workspace with public access denied.** This is a documented Fabric limitation, even with a workspace private endpoint. The API adapter rejects assigning or promoting restricted workspaces and never changes a firewall to make deployment pass. Recheck [supported private-link scenarios and limitations](https://learn.microsoft.com/en-us/fabric/security/security-workspace-level-private-links-support) before platform acceptance.

| Profile | Release route | Required qualification |
|---|---|---|
| Native Fabric ALM | Separate Dev/Test/Prod workspaces on reviewed capacities; explicit selected-item promotion | Public access policy must allow native pipeline connectivity. Entra authorization remains required; `isPublic: false` on a deployment stage is not a network firewall. Validate item and identity support. |
| Workspace restricted inbound | Workspace-level Private Link, approved private agent/client DNS, independently reviewed deny-public policy | Native pipeline promotion is blocked. Qualify a supported private-network source/item release method separately; this increment does not claim such a replacement executor. |
| Private outbound sources | Fabric managed private endpoints where supported, or qualified VNet/on-premises data gateways | Source-side approval, Fabric workload/connector support, source RBAC and successful reads. An Azure endpoint in the client's VNet alone does not give Fabric compute access to that source. |

The optional ARM service follows Microsoft's [workspace-level Private Link setup](https://learn.microsoft.com/en-us/fabric/security/security-workspace-level-private-links-set-up): global `Microsoft.Fabric/privateLinkServicesForFabric@2024-06-01`, workspace association, endpoint group `workspace`, existing `privatelink.fabric.microsoft.com` zone. The Bicep MCP bundled schema currently lacks this resource (`BCP081`); the exact Learn ARM example is the source reference. No warning suppression or fabricated ARM workspace resource is used.

Inbound Private Link does not automatically deny public access, isolate outbound Spark/Data Factory traffic, configure tenant-wide policies or make every item private-link compatible. Use the workspace-specific endpoint returned by supported APIs for qualified private clients. The initial API executor deliberately uses the global Fabric endpoint and blocks restricted native promotion; private-endpoint API execution remains a separately qualified profile. Do not enable tenant-wide Private Link from a workload stack.

## Connectors and lakehouse example

`examples/` contains operation fragments; copy one into the reviewed environment manifest after replacing every placeholder with discovered IDs. ADLS Gen2 and Azure SQL show **WorkspaceIdentity**, encrypted transport and organizational privacy; no keys, SAS, passwords or connection strings belong in source, model prompts or artifacts. The executor checks the live [supported connection types](https://learn.microsoft.com/en-us/rest/api/fabric/core/connections/list-supported-connection-types), creation method, parameter names and identity support, then uses [Create Connection](https://learn.microsoft.com/en-us/rest/api/fabric/core/connections/create-connection). Unsupported combinations fail closed.

Provision the [workspace identity](https://learn.microsoft.com/en-us/rest/api/fabric/core/workspaces/provision-identity) first. Separately grant least-privilege data access: ADLS landing-container reader plus required filesystem/path ACLs; Azure SQL contained identity/user and narrowly scoped grants. The optional ADLS Bicep sample grants container-level RBAC after the identity's service principal object ID is observed. It does not create Fabric connectors or grant OneLake/workspace access.

Managed endpoint examples request `dfs` access to reviewed Storage or `sqlServer` access to Azure SQL; add Blob access where the selected workload requires it. Source owners must approve the endpoint. A successful request is not approved connectivity, and managed endpoints are workload-specific; a Spark endpoint does not establish gateway/pipeline/shortcut reachability. On-premises SQL Server/Oracle need a qualified gateway, driver, network path and secure credential lifecycle; those are documented extension points, not silently provisioned connectors.

The lakehouse example creates an empty `claims_bronze` item. Add separately reviewed silver/gold transformation code, data quality, schema evolution, retention and lineage before using real claims data. No claims data, notebook, schedule or ingestion job runs automatically.

## CI/CD and governance

Keep definition source in Git, separate Dev/Test/Prod workspaces, review item dependencies, and promote an explicit item list. The [Deploy Stage Content API](https://learn.microsoft.com/en-us/rest/api/fabric/core/deployment-pipelines/deploy-stage-content) supports service principals only when every involved item does; it also has item-count and stage constraints. Credential bindings, connection permissions, environment-specific settings and data movement require independent qualification. Native pipelines deploy supported definitions; do not treat promotion as a data backup or as proof that source data was copied.

Use separate platform/workspace administrator groups, development contributors and production readers. Limit Fabric tenant settings permitting service principals to approved security groups. Azure Contributor does not confer Fabric workspace, connection, capacity assignment or pipeline roles. Capture service/principal identity, exact source commit and manifest, API plan hash, operation ID, before/after evidence and verification receipts in restricted artifacts. Apply data classification, sensitivity labels, Purview lineage, audit retention and exfiltration controls through tenant governance; Bicep capacity creation alone does not configure them.

## Fabric, Learn and Bicep MCP

Connect Fabric explicitly in **Connections**, then connect its MCP server. The official [Fabric Core remote MCP](https://learn.microsoft.com/en-us/rest/api/fabric/articles/mcp-servers/core-remote/get-started-core) endpoint is `https://api.fabric.microsoft.com/v1/mcp/core`, with its own Fabric audience. It does not inherit Azure ARM authorization. Reconnect Fabric after portal restart; this addition does not silently request a new audience from old sign-in caches.

Use **MCP orchestration** to discover current Fabric schemas and gather scoped evidence alongside Microsoft Learn and Bicep guidance. Follow the existing exact-plan approval/receipt route; MCP advice does not enable targets or replace protected ADO deployment. During this implementation, official local Fabric MCP 1.6.0 lakehouse API examples, Learn MCP documentation search and Bicep MCP best-practice/capacity-schema calls succeeded. Fabric documentation tools ran with an isolated empty Azure cache; no tenant operations were invoked. No authenticated Fabric MCP call or tenant deployment was made; the provider is implemented for the user's explicit connection and acceptance.

## Acceptance checklist

- Compile all four environment parameters, run product/naming/catalog and Fabric API negative tests, and verify generated guidance/source manifest.
- Complete real ADO expansion, WIF authorization, protected approvals, saved discovery, What-If and ownership/drift rejection.
- Verify active capacity assignment, explicit group permissions, lakehouse read/write boundaries, identity source grants and unauthorized-principal denial.
- Validate each connector's actual source reads, endpoint approval, DNS, private-path enforcement and negative public access test for the chosen profile.
- Test supported-item promotion and environmental connection bindings on the native ALM profile, or qualify a private release method separately for restricted workspaces.
- Record throttling, cancellation/unknown-outcome reconciliation, audit delivery, capacity metrics/cost monitoring and operator recovery drills. Retain data independently; no automated Fabric inverse/delete/rollback is delivered.

Local verification and current gaps are recorded in [validation](../../docs/validation.md) and [completion status](../../docs/completion-status.md). Live cloud and Fabric acceptance remain required before production use.
