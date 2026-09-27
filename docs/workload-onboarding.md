# Adding and operating self-service workload products

## Current catalog and delivery status

The catalog contains seven workload types: Blob copy, Event flow, Private Storage Workspace, Key Vault, Observability, HTTP Functions API and Service Bus worker. Each has four disabled environment profiles and dedicated Discover/Deploy YAML roots. The five additions are implemented in source; platform onboarding, price qualification, ADO definition registration and live Azure acceptance remain required. A menu or successful local compilation is not production acceptance.

| Offering | Instance / YAML slug | Application delivery | What it owns |
|---|---|---|---|
| Private Storage Workspace | `storage` | Infrastructure only | StorageV2 account, workspace container, work queue, Blob/Queue private endpoints, consumer RBAC and diagnostics. |
| Key Vault | `keyvault` | Infrastructure only | Standard RBAC vault, private endpoint, reader grant and audit diagnostics. Soft delete and 90-day purge protection; no secret values. |
| Observability | `observe` | Infrastructure only | Workbook and missing-heartbeat query alert. References an existing workspace/action group; does not create notification destinations or change shared diagnostics/networking. |
| HTTP Functions API | `httpapi` | `ProductFunctions` ZIP | Linux B1 Functions hosting, user identity, host/package storage, four endpoints, diagnostics and Entra-protected health API. |
| Service Bus worker | `busworker` | `ProductFunctions` ZIP | Premium namespace/work queue with DLQ and duplicate detection, Linux B1 Functions host, identity, host/package/receipt storage, five endpoints and diagnostics. |

All five use workload-owned resource groups, versioned Template Specs and Deployment Stacks with the existing conservative change gates. Shared dependencies are referenced, never adopted into a workload stack. Module registry adoption remains deferred.

## Developer and platform setup

Developers select an offering in Platform Studio or its dedicated ADO definition. The menus contain only that offering's resource summary, dependency/cost description, environment and approved region. Deploy defaults to **Preview only**. There are no arbitrary resource checkboxes or developer-entered address ranges.

Create the following ADO definitions from GitHub after merging the source changes:

| Definition | YAML file |
|---|---|
| Discover - Private Storage Workspace | `azure-pipelines-storage-discover.yml` |
| Deploy - Private Storage Workspace | `azure-pipelines-storage-deploy.yml` |
| Discover - Key Vault | `azure-pipelines-keyvault-discover.yml` |
| Deploy - Key Vault | `azure-pipelines-keyvault-deploy.yml` |
| Discover - Observability | `azure-pipelines-observe-discover.yml` |
| Deploy - Observability | `azure-pipelines-observe-deploy.yml` |
| Discover - HTTP Functions API | `azure-pipelines-httpapi-discover.yml` |
| Deploy - HTTP Functions API | `azure-pipelines-httpapi-deploy.yml` |
| Discover - Service Bus worker | `azure-pipelines-busworker-discover.yml` |
| Deploy - Service Bus worker | `azure-pipelines-busworker-deploy.yml` |

The platform owner must complete each workload's `environments/main.<env>.bicepparam` or approved target overrides. Required common settings are owner, cost center, an existing same-subscription Log Analytics workspace, and the deployment service principal object ID. The new files deliberately contain empty/onboarding values; no existing networking or identity relationship is guessed.

- Storage: reviewed consumer **service principal object ID**, with Blob and Queue data access.
- Key Vault: reviewed reader **service principal object ID**. Secret population and secret-write permission are separate secure operations.
- Observability: existing same-subscription action group IDs, approved recipients and Heartbeat data ingestion. The alert detects formerly reporting machines; it cannot detect machines never inventoried by the workspace.
- Both Functions products: distinct existing integration and endpoint subnets in one VNet/region. The integration subnet must be delegated to `Microsoft.Web/serverFarms` and /26 or larger. These checks do not prove current free-IP capacity.
- HTTP API: a **separate API Entra application registration**, allowed client application IDs and the required consent/application authorization for its callers and pipeline smoke identity. This is distinct from both the local portal registration and ADO service connection. No app registration or consent is created by Bicep.
- Private products: exact DNS zone IDs. Blob/Queue for Storage; Vault for Key Vault; Blob/Queue/Table/Sites for HTTP; those four plus Service Bus for the worker. The initial profile requires direct VNet links and Azure-provided DNS. Custom DNS/hub-resolver topologies fail closed until a qualified profile is added.

Discovery inventories resources and required providers without registering providers or changing Azure. Required providers must already be registered. Shared subnets, DNS and monitoring that are absent block Preview; the platform team must provision or approve them separately. New workload-owned resources appear as Create in Azure What-If once inputs are complete. An existing unrelated workload resource group is not silently adopted.

The pipeline identity needs control-plane deployment rights and permission to create the declared scoped role assignments; Contributor alone does not grant role-assignment write. Publisher permissions, the Template Spec resource group, private agent pool, protected deployment environments and their approvals must be configured. Runtime jobs require DNS and network connectivity to all private dependencies, package Blob endpoints, Entra and the required management endpoints. Follow the existing [security guide](security-and-rbac.md) and [networking plan](plans/private-networking-self-service.md).

Keep `enabled: false` until platform onboarding and the product's acceptance checklist are approved. Hosted Discover and Preview do not require the private pool merely to parse the YAML. Actual deployment still requires that pool and environment authorization.

## Pipeline and method flow

```mermaid
flowchart LR
  A[Select registered offering] --> B[Discover artifact]
  B --> C[Validate intent and shared dependencies]
  C --> D[Compile full stack and Azure What-If]
  D --> E[Preview README and frozen inputs]
  E --> F[Protected Deploy approval]
  F --> G[Qualify source and typed release]
  G --> H[Publish immutable Template Spec]
  H --> I[Recheck approved preview and drift]
  I --> J[Apply owned Deployment Stack]
  J --> K[Configuration or runtime verification]
```

| Entry / method | Responsibility |
|---|---|
| `Get-WorkloadDefinition` | Code-owned allowlist validates composition/stack paths, adapter, package kind, lifecycle parameter and menu slug against `config/workloads.json`. Configuration cannot name a script to execute. |
| `Resolve-PlatformRequest` | Matches workload type, registered instance, environment and approved region; rejects extra implementation fields and ambiguous targets. |
| `Export-DeploymentInventory.ps1` / `Read-DiscoveryManifest` | Produce/consume workload discovery v2 with provider/resource evidence, exact selection, freshness and inventory hash. ADO handoff separately authenticates producer/run/main provenance. |
| `Assert-ProductParameters` / `Assert-ProductDiscovery` | Validate typed settings, same-subscription references, correct DNS names and successful prerequisite evidence. Failed or missing reads do not become Create permission. |
| `New-WorkloadPreviewInputs` / `Invoke-WorkloadInfrastructurePreview` | Compile main/stack/parameters, freeze discovery and costs, inspect current ownership/shared dependencies and invoke stack validation/What-If. Upload the existing detailed README. |
| `Build-ProductPackage.ps1` | Creates a typed release receipt. Infrastructure products have **no application ZIP**. Functions products publish the locked .NET project and validate both trigger definitions. Dirty source is recorded and rejected by deployment bundle qualification. |
| `New-ProductBundle` / `Read-ProductBundle` | Bundle schema v3 records exact file hashes, product definition, discovery provenance and optional application. Rejects tampering, changed registered targets, cross-product content and unexpected file sets. |
| `New-ProductPlan` / `Invoke-ProductApply` | Revalidate shared dependencies and drift. Infrastructure-only products apply Release once through the dedicated two-stage flow; Functions use Foundation → immutable package upload → Release. Legacy generic flow retains its Foundation/Release compatibility. |
| `Test-ProductInfrastructure` / `Invoke-ProductSmoke` | Check deployed configuration and private DNS; HTTP requires authenticated health plus unauthenticated 401; worker sends a synthetic message and verifies the matching durable receipt. |

Stage 2 requires the same run's successful Preview and matching source/input hashes. Existing Delete/Detach, uncertain changes, security changes and ownership gates still apply. Publication and deployment use separately bound protected resources. Application package content is outside Bicep What-If but is hash-bound in the qualified bundle. Automatic rollback is not implemented.

`InfrastructureReady` means resource configuration/private DNS checks passed. It does not claim consumer data access, negative authorization, vault recovery or notification delivery were tested. Those are explicit onboarding acceptance drills. Runtime `Ready` additionally requires the implemented smoke test; it does not replace load, failure, DLQ/replay or production acceptance.

## Runtime samples and dependencies

`src/ProductFunctions` targets .NET 10 / Functions v4 isolated, with the existing SDK 10.0.300 convention. Dependencies are locked: Worker 2.52.0, Worker SDK 2.1.0, HTTP extension 3.3.0, Service Bus extension 5.24.0, Azure Identity 1.21.0, Blob SDK 12.29.2 and Hosting 10.0.12. New extension versions were checked against NuGet before implementation. Tests use xUnit 2.9.3 and Microsoft.NET.Test.Sdk 18.10.1. No new frontend or AI packages are required.

The same ZIP contains `Health` and `ProcessWork`; product app settings disable the unrelated function. `Health` returns a minimal status response; Azure Easy Auth enforces tenant/audience/client restrictions. An anonymous local Functions host is not an Entra authentication emulator and must remain loopback-only during development.

`ProcessWork` accepts only `{ "id": "<nonzero-guid>", "operation": "record" }` up to 16 KiB. It creates a receipt with a conditional Blob write, completes exact duplicate deliveries, dead-letters invalid/conflicting input and propagates storage failures for retry. Body-byte hashing deliberately treats changed serialization with the same ID as a conflict. No business-side effects are implemented. Queue duplicate detection is ten minutes, max delivery count ten and TTL fourteen days; durable receipt deduplication persists beyond the queue window. The worker's Service Bus role is documented in the module README. Application logs never record message bodies or credentials.

## Costs and acceptance

New product menus intentionally say **estimate unavailable, not zero/free** until complete reviewed price snapshots exist. Drivers include private endpoint hours, storage operations/capacity, monitoring ingestion/retention, B1 hosting and Premium Service Bus capacity. Do not describe a partial endpoint-only figure as the total. Pricing qualification is an onboarding obligation before target enablement.

Required live acceptance includes:

- All products: real ADO YAML expansion, producer selection, What-If, initial deployment/update, wrong-scope rejection, private DNS/access, ownership retention and disabled-target behavior.
- Storage: authorized Blob/Queue operations, unauthorized identity denial, version/retention recovery.
- Key Vault: authorized secret read, unauthorized denial, secure secret population procedure and recovery/purge-protection review. No secrets in YAML or evidence logs.
- Observability: synthetic heartbeat gap, delivered/resolved alert and correct recipient/workspace isolation.
- HTTP API: successful intended caller, anonymous 401, wrong tenant/audience/client rejection and immutable application version evidence.
- Worker: send/receive receipt, repeat delivery, conflict/invalid message DLQ, transient failure, retry exhaustion, operator-reviewed replay and recovery.

Run local checks from the repository root:

```powershell
./scripts/Stop-Portal.ps1
./scripts/Test-Products.ps1
./scripts/Test-Portal.ps1
./scripts/Update-ServiceCatalog.ps1 -Check
# Full existing/new regression suite after reviewing and refreshing the source manifest:
./scripts/Update-Manifest.ps1
./scripts/Test-Project.ps1
./scripts/Start-Portal.ps1
```

The product suite uses synthetic saved Azure evidence; no Azure deployment occurs. Do not use locally fabricated test receipts as pipeline artifacts. The live path retains authenticated ADO producer checks.

## Expansion rules

Keep reusable resource contracts under `modules/`, product compositions/parameters/schemas under `workloads/`, application code under `src/`, and reviewed bindings under `self-service/targets/`. Keep independent shared foundations under `platform/`. A new product must update the allowlisted adapter, metadata, request schema, discovery coverage, readiness, portal/menu contracts, tests and documentation together. Extract a shared module only when its ownership and lifecycle contract are genuinely reusable.

Microsoft references: [Bicep modules](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/modules), [Functions identity connections](https://learn.microsoft.com/en-us/azure/azure-functions/manage-connections), [Service Bus trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-service-bus-trigger), [disable Service Bus local authentication](https://learn.microsoft.com/en-us/azure/service-bus-messaging/disable-local-authentication), [Key Vault template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.keyvault/vaults). These explain platform contracts; they do not establish this repository's Azure acceptance.
