# Blob-transfer workload composition

Status: implemented with local Functions/Azurite evidence; all four Blob copy targets remain disabled and Azure acceptance is outstanding in reviewed evidence. See the [catalog](../../docs/self-service-catalog.md) and [current status](../../docs/completion-status.md).

The resource-group implementation entry point is [main.bicep](main.bicep). [stack.bicep](stack.bicep) is the subscription entry point used by the self-service Deployment Stack and owns the dedicated workload RG. Environment values live in `environments/main.{dev,qa,uat,prod}.bicepparam`; each file uses `../main.bicep`. This folder also owns the developer request schema/example and pattern-specific module helpers.

The supported pattern is `blob-transfer`: two private storage accounts, a dedicated Linux Function App/plan, a user-assigned identity, scoped storage roles, private endpoints, diagnostics and three alerts. The destination storage/container already exists. Storage and observability are required capabilities. No SQL, Key Vault, generic API, web application or Cosmos DB provisioning is implied.

Dedicated-menu inputs: registered instance, environment, approved region, Run stages and saved discovery run. Workload type is fixed by the chosen definition; JSON intent and generic compatibility menus still carry it explicitly. The platform chooses subscription, topology, central zones/resolver, monitoring workspace, service connection, agent pool, destination and implementation options. `request.schema.json` describes the JSON shape; `Resolve-WorkloadRequest.ps1` additionally enforces the registered catalog and approved regions.

Shared [storage](../../modules/storage/storage-account/README.md) and [private endpoint](../../modules/network/private-endpoint/README.md) modules live under the repository's resource-oriented `modules/` tree. [Internal helpers](modules/README.md) own the blob-transfer Function settings, alerts, isolated-network exception and access grants. The workload's output contract exposes storage, identity, Insights and PE IDs without secrets. Names, deployment scopes and role GUID inputs are preserved by the source relocation.

Platform configuration lives in `config/`; reviewed workload instances and their bindings live in `self-service/targets/`. Scripts overlay validated profile settings on the environment parameters and freeze the resolved values in the release bundle. Developers do not edit lifecycle or network topology flags. See the [repository conventions and path migration](../../docs/repository-structure.md).

See [enterprise architecture, governance and gaps](../../docs/enterprise-platform.md), [developer steps](../../docs/self-service.md), and [runtime dispatcher methods](../../docs/dispatcher/README.md).
