# Workload catalog

[Wiki home](README.md) · [Get started](getting-started.md) · [Onboarding](workload-onboarding.md) · [Status](completion-status.md)

Seven workload types have compositions, Deployment Stack wrappers, environment parameters, request contracts, dedicated pipeline pairs and portal configuration. Four environments per named instance produce 28 registered targets. All remain disabled pending onboarding and live acceptance.

## Offerings

| Workload | Resources and purpose | Product boundary |
|---|---|---|
| [Blob copy](../workloads/blob-transfer/README.md) | Function App, polling dispatcher and queue worker; host/solution storage, transfer ledger, identity, private access and monitoring. Copies uploads to an existing destination data lake. | Complete transfer application with recorded local Functions/Azurite evidence; Azure acceptance outstanding. |
| [Event flow](../workloads/logic-app-event-grid/README.md) | Logic App Standard, Event Grid, two storage accounts, private endpoints, identities and monitoring. | Packaged stateful workflow; supports owned prerequisite creation or approved shared-resource reuse. Hosted delivery remains to be accepted. |
| [Private Storage Workspace](../workloads/private-storage/README.md) | Identity-only StorageV2 account, workspace container, work queue, two private endpoints, consumer RBAC and diagnostics. | Infrastructure only; reuses approved network, DNS and monitoring. |
| [Key Vault](../workloads/key-vault/README.md) | RBAC vault with soft delete and purge protection, private endpoint, reader grant and audit diagnostics. | Infrastructure only; creates no secret values. |
| [Observability](../workloads/observability/README.md) | Azure Monitor workbook and missing-heartbeat query alert referencing existing Log Analytics and action groups. | Infrastructure only; does not create notification destinations or reconfigure shared workspace networking. |
| [HTTP Functions API](../workloads/http-functions/README.md) | Private .NET 10 Function App on Linux B1, identity, host/package storage, four private endpoints, diagnostics and an Entra-protected health endpoint. | Health API starter; developers add application-specific endpoints. |
| [Service Bus worker](../workloads/service-bus-worker/README.md) | Premium Service Bus namespace/queue, DLQ and duplicate detection, Linux B1 Functions worker, identity, receipt storage, five private endpoints and diagnostics. | Idempotent receipt-processing starter; developers add business processing. |

The [onboarding guide](workload-onboarding.md) lists identities, shared resources, providers, permissions and product acceptance tests. Event flow can plan missing owned networking/DNS prerequisites; the five newer offerings require their declared shared dependencies to exist. Failed or partial discovery does not mean a dependency is absent and safe to create.

## Pipeline entry points

| Workload | Discover | Deploy: Preview then optional Apply |
|---|---|---|
| Blob copy | [azure-pipelines-blobcopy-discover.yml](../azure-pipelines-blobcopy-discover.yml) | [azure-pipelines-blobcopy-deploy.yml](../azure-pipelines-blobcopy-deploy.yml) |
| Event flow | [azure-pipelines-eventflow-discover.yml](../azure-pipelines-eventflow-discover.yml) | [azure-pipelines-eventflow-deploy.yml](../azure-pipelines-eventflow-deploy.yml) |
| Private Storage Workspace | [azure-pipelines-storage-discover.yml](../azure-pipelines-storage-discover.yml) | [azure-pipelines-storage-deploy.yml](../azure-pipelines-storage-deploy.yml) |
| Key Vault | [azure-pipelines-keyvault-discover.yml](../azure-pipelines-keyvault-discover.yml) | [azure-pipelines-keyvault-deploy.yml](../azure-pipelines-keyvault-deploy.yml) |
| Observability | [azure-pipelines-observe-discover.yml](../azure-pipelines-observe-discover.yml) | [azure-pipelines-observe-deploy.yml](../azure-pipelines-observe-deploy.yml) |
| HTTP Functions API | [azure-pipelines-httpapi-discover.yml](../azure-pipelines-httpapi-discover.yml) | [azure-pipelines-httpapi-deploy.yml](../azure-pipelines-httpapi-deploy.yml) |
| Service Bus worker | [azure-pipelines-busworker-discover.yml](../azure-pipelines-busworker-discover.yml) | [azure-pipelines-busworker-deploy.yml](../azure-pipelines-busworker-deploy.yml) |

All entry points are manual. Push, PR and discovery-completion triggers are disabled. [azure-pipelines.yml](../azure-pipelines.yml) is the build/test/package entry. The generic self-service pair remains a compatibility route with its older stage structure. Tagging and AVNM have separate operations pipelines; they are not additional workload products. Use [ADO registration](ado-pipeline-registration.md) for the full 20-entry catalog and exact definition names.

## Delivery and evidence

1. **Discover:** collect the reviewed subscription/profile and publish `subscription-discovery` with inventory, manifest and provenance.
2. **Select evidence:** open the matching Deploy menu on `main`. Choose a successful matching run under **Resources → discovery**, no older than seven days; the portal provides a reviewed picker.
3. **Preview only:** verify discovery/inputs, compile Bicep, and run Azure validation/What-If. Read **Summary / Extensions** or `deployment-preview/README.md` for resource/property changes, blockers, costs and recovery policy.
4. **Preview and deploy:** after target enablement, a new run performs a fresh Preview, qualifies the bundle, publishes a versioned Template Spec, rechecks drift and applies the stack. Application workloads use Foundation/package/Release sequencing; infrastructure-only offerings have no application ZIP.
5. **Verify:** inspect `deployment-result/receipt.json` and product-specific checks. `InfrastructureReady` and runtime `Ready` represent different evidence; neither guarantees recovery or production acceptance.

ADO fields are generated from reviewed configuration. A discovery artifact cannot add new queue-time dropdowns midway through a run. Approval checks, exclusive locks, service connections and private-agent connectivity require external platform configuration. See [pipeline flow](pipeline-flow.md), [Preview](deployment-preview.md) and [stack lifecycle](deployment-stacks-upgrade.md).

## Costs, ownership and recovery

Dedicated hosting, Premium Service Bus, private endpoints, storage operations and monitoring can incur charges. The five newer offerings show **estimate unavailable**, not zero. Prices, assumptions and exclusions belong to the [cost guide](self-service-costs.md).

Shared modules are compiled into the Template Spec; a separate module registry is not required. Workload compositions own their resources, while target profiles bind the approved environment, identity and shared dependencies. See [source conventions](repository-structure.md) and [detailed method map](self-service-catalog.md).

Each offering has a [recovery policy](recovery-rules.md). Rules and offline assessment are implemented, but retained-baseline verification and a restore executor are not. A deployment may leave partial resources after failure; do not infer an automatic undo from stack ownership.
