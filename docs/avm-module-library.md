# Azure Verified Module building blocks

[Wiki home](README.md) · [Reusable modules](../modules/README.md) · [Source drafts](bicep-source-drafts.md) · [MCP providers](provider-mcp.md)

## What developers can compose

The 17 October 2026 source increment adds six project wrappers backed by pinned official Azure Verified Modules (AVM). The application combines **17 native contracts and six AVM contracts: 23 inventoried modules, 18 selectable for source drafts**. The five exclusions remain IPAM reservation, private AKS and the three Private Data Platform modules. These are building blocks for **Design Bicep source draft**, separate from the nine registered workload products and their protected delivery paths. Existing workload compositions and target enablement are unchanged.

The updated portal build exposes the combined inventory through `GET /api/bicep/modules` and module details in **Agent workflows**. Review the wrapper's actual parameters, defaults, constraints, outputs and provenance before requesting a composition. Source, a catalog entry and model advice do not establish Azure readiness. The primary port-5087 build was activated on 17 October through [the cache-preserving fresh restart](portal-fresh-restart.md); its actual combined inventory and restored provider catalogs passed [separate acceptance checks](validation.md#cache-preserving-primary-restart--17-october-2026). Existing running binaries still require rebuilding/restarting for subsequent code changes.

## Pinned library and exposed interfaces

All six wrappers require explicit `name`, `location` and `tags`. They do not read environment files, choose subscriptions, create role assignments or adopt discovered resources. Optional child arrays default to `[]`; leaving them omitted creates none of those optional children. Every wrapper explicitly disables AVM usage telemetry.

| Contract ID / project wrapper | Official AVM release and pinned source | Additional wrapper inputs | Safe wrapper outputs |
|---|---|---|---|
| `avm-identity-user-assigned-identity` · [identity](../modules/avm/identity/user-assigned-identity/main.bicep) | `avm/res/managed-identity/user-assigned-identity:0.6.0` · [source](https://github.com/Azure/bicep-registry-modules/blob/a9370ad48e31b2de5a679dd630da28612ad95471/avm/res/managed-identity/user-assigned-identity/main.bicep) | `federatedIdentityCredentials` | `id`, `name`, `principalId`, `clientId` |
| `avm-containers-container-registry` · [registry](../modules/avm/containers/container-registry/main.bicep) | `avm/res/container-registry/registry:0.13.1` · [source](https://github.com/Azure/bicep-registry-modules/blob/f1f4ba28698437d9a683d68bf181341ad3547e48/avm/res/container-registry/registry/main.bicep) | `zoneRedundancy` (`Disabled`), `retentionInDays` (30), `replications` | `id`, `name`, `loginServer` |
| `avm-configuration-app-configuration` · [configuration](../modules/avm/configuration/app-configuration/main.bicep) | `avm/res/app-configuration/configuration-store:0.10.1` · [source](https://github.com/Azure/bicep-registry-modules/blob/f1f4ba28698437d9a683d68bf181341ad3547e48/avm/res/app-configuration/configuration-store/main.bicep) | `skuName` (`Standard`), `softDeleteRetentionInDays` (7), `keyValues`, `replicaLocations` | `id`, `name`, `endpoint` |
| `avm-monitoring-application-insights` · [telemetry](../modules/avm/monitoring/application-insights/main.bicep) | `avm/res/insights/component:0.8.0` · [source](https://github.com/Azure/bicep-registry-modules/blob/4f750c70f333b2df6170e3b56ae90faa852361d2/avm/res/insights/component/main.bicep) | Required `workspaceId`; `applicationType` (`web`), `retentionInDays` (90), `samplingPercentage` (100) | `id`, `name`, `applicationId` |
| `avm-network-network-security-group` · [NSG](../modules/avm/network/network-security-group/main.bicep) | `avm/res/network/network-security-group:0.5.4` · [source](https://github.com/Azure/bicep-registry-modules/blob/99f7bd7e64b6067691330b63744a999269431d2c/avm/res/network/network-security-group/main.bicep) | `securityRules` | `id`, `name` |
| `avm-network-route-table` · [routing](../modules/avm/network/route-table/main.bicep) | `avm/res/network/route-table:0.5.0` · [source](https://github.com/Azure/bicep-registry-modules/blob/38e14b5cc5a386be8620c26c25d368e8aeaa0bf2/avm/res/network/route-table/main.bicep) | `routes`, `applianceRoutes`, `disableBgpRoutePropagation` (`false`) | `id`, `name` |

The wrappers deliberately expose a reviewed subset of each release. The complete official artifacts also contain optional roles, locks, diagnostics, private endpoints, keys and other service features. Their presence in a downloaded artifact or a resource-type inventory does not mean the wrapper accepts those inputs or creates those resources. For example, ACR tasks, tokens, webhooks and cache rules are not exposed; this increment does not add Cosmos DB, SQL, PostgreSQL or Container Apps.

### Parameterized children and bounds

| Wrapper | Accepted child structure and bounds | Remaining qualification |
|---|---|---|
| Identity | At most 20 federation entries: `{name, issuer, subject, audiences}`. Names are 3–120 characters, issuer 9–2,048, subject 1–600. `audiences` contains exactly one `api://AzureADTokenExchange` value. Identity name is 3–128 characters; isolation is fixed to `Regional`. | Verify exact HTTPS issuer, subject, tenant, intended workload and trust lifecycle. Length constraints do not prove a trustworthy issuer. No RBAC is granted. |
| Registry | Up to 10 `replications`: `{name, location, zoneRedundancy}`. Replica names and locations are 1–50 characters. `zoneRedundancy` is `Disabled` or `Enabled`; primary uses the same choices. Retention is 7–90 days. Registry name is 5–50 characters. | Check alphanumeric naming/availability, duplicate replicas, supported regions/zones, Premium cost and deletion of untagged manifests. |
| App Configuration | Up to 100 `keyValues`: `{name, value, contentType}`. Name is 1–512 characters, value at most 8,192; content type is `text/plain` or `application/json`. Up to 10 `replicaLocations`: `{name, replicaLocation}`, both 1–50 characters. Store name is 5–50; SKU is `Standard` or `Premium`; soft-delete retention is 1–7 days. | Validate key naming/labels, JSON content where selected, uniqueness, tier/region compatibility, data ownership and replica cost. Values are ordinary visible configuration, never credentials or secrets. |
| NSG | Up to 100 `securityRules`: `{name, properties:{priority, access, direction, protocol, sourceAddressPrefix, sourcePortRange, destinationAddressPrefix, destinationPortRange, description?}}`. Priority is 100–4,096; access `Allow`/`Deny`; direction `Inbound`/`Outbound`; protocol `Tcp`, `Udp`, `Icmp`, `Esp`, `Ah` or `*`. Names 1–80, address prefixes 1–100, ports 1–11, description at most 140 characters. | Validate unique names/priorities, actual address/port syntax, service tags and effective traffic. Empty rules retain Azure's default NSG rules. No subnet attachment occurs. |
| Route table | Up to 100 `routes`: `{name, properties:{addressPrefix,nextHopType}}`, with `VnetLocal`, `VirtualNetworkGateway` or `None`. Separately, up to 100 `applianceRoutes`: `{name, properties:{addressPrefix,nextHopIpAddress}}`; the wrapper supplies `VirtualAppliance`. Names 1–80, prefixes 1–100, appliance IP strings 7–45 characters. | Validate unique names across both arrays, prefixes, next-hop syntax/reachability, routing symmetry and combined service limits. No appliance or subnet association is created. |

NSG and route-table names are 1–80 characters; the Application Insights name is 1–260. Application Insights accepts `web`/`other`, retention from `30, 60, 90, 120, 180, 270, 365, 550, 730`, and sampling 1–100 percent. These source constraints are not complete Azure naming, quota, topology or runtime validation.

## Networking, identity, cost and ownership

**Registry:** the wrapper fixes Premium, disables the admin account and anonymous pulls, disables public access, denies network rules and sets trusted-service/task bypass to `None`/`false`. It does not create a private endpoint, DNS zone or pull/push grant. Compose the existing private-endpoint module with reviewed subnet/DNS inputs and the registry's `id`, then qualify private agent and runtime connectivity. Disabling ARM-audience authentication also requires compatibility testing with clients that expect it. Premium, storage, replicas and private connectivity are billable; no price estimate is included. See [ACR private connectivity](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-private-endpoints) and [registry security](https://learn.microsoft.com/en-us/azure/container-registry/secure-container-registry).

**App Configuration:** local authentication and public access are disabled; purge protection is enabled. The data-plane proxy uses `Pass-through` authentication and enabled private-link delegation. Creating nonempty key-value children requires the deployment identity's management permissions and appropriate data-plane write permissions, such as reviewed App Configuration Data Owner access, plus a qualified ARM Private Link deployment route. The wrapper grants neither. A private endpoint/DNS path for application clients is a separate dependency. An ordinary internet-connected ARM deployment or a private agent by itself does not establish the required ARM path. Keep `keyValues` empty until those prerequisites are qualified; do not enable public access to make a failed deployment pass. See [App Configuration deployment and authorization](https://learn.microsoft.com/en-us/azure/azure-app-configuration/quickstart-deployment-overview) and [ARM private network access requirements](https://learn.microsoft.com/cli/azure/appconfig/). Standard/Premium, replicas and endpoints require cost review; protected deletion affects lifecycle planning.

**Application Insights:** the wrapper requires a reviewed Log Analytics workspace, disables local authentication and public ingestion/query, preserves IP masking and omits instrumentation keys/connection strings from its public outputs. Qualify Azure Monitor Private Link Scope (AMPLS), component/workspace associations, private DNS, network access and Entra-enabled instrumentation separately. The wrapper does not provision AMPLS or change workspace security. Review sampling behavior, ingestion volume, workspace retention and cost. See [Azure Monitor private-link design](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-design).

**Identity and networking:** a federation entry creates trust, while roles remain separately reviewed scope-bound changes. NSGs and route tables are for new workload-owned resources; an existing shared name is not a reuse instruction. Neither wrapper writes subnet associations. Validate network ownership and traffic effects before integrating them into a composition; discovering an ID does not authorize management. Region/SKU availability, provider registration, policy, quotas, deletion behavior and RBAC remain promotion prerequisites for all six modules.

## Skills, MCP evidence and parameter files

Use [the Bicep composition skill](../.agents/skills/platform-bicep-composition/SKILL.md) for typed proposals and [provider orchestration](../.agents/skills/platform-mcp-orchestration/SKILL.md) for current Bicep/AVM schemas, GitHub sources, Learn guidance and authorized Azure evidence. Preserve the [workflow standard](agent-workflow-standard.md): catalog lookup, read-only evidence, source drafting, approval and protected execution are distinct steps. Skills do not grant cloud permissions or silently upgrade a module version.

The deterministic emitter supports bounded object/array schemas, primitive constraints, sealed child types and local compiled type references. It renders Bicep user-defined types in both main and stack templates. Schema references are resolved from the selected reviewed contract; unsupported, cyclic, advanced or secure types fail rather than becoming arbitrary code. Structured output bindings still need a separate adapter.

The model chooses `Input`, `Setting`, `Resource` or primitive `Output` bindings. It cannot inject raw Bicep expressions, a module path, a literal configuration value or an arbitrary schema. Selecting an optional child parameter as `Input` makes the whole typed array a required human input; omitting it preserves `[]`. Fill reviewed values in the downloaded `.bicepparam` files. A synthetic example for a composition node named `settings` is:

```bicep
param p_settings_keyValues = [
  {
    name: 'FeatureFlags:ExampleFeature'
    value: 'false'
    contentType: 'text/plain'
  }
]
```

The node name determines the generated parameter prefix. This is configuration data, not a secret-binding facility. Source, parameter files, rendered details, archives, deployment history and ordinary resolved-value JSON can expose their contents. Supply no passwords, tokens, connection secrets or sensitive rendered values. See [draft provenance and promotion](bicep-source-drafts.md#source-and-parameter-contract).

## Source ownership and maintenance

`modules/avm/**/main.bicep` is project-owned reviewed source. `vendor/azure-verified-modules/<id>/main.json` is the exact published compiled ARM layer, with its full nested child templates; `manifest.json` is the exact OCI manifest. [bundle.json](../vendor/azure-verified-modules/bundle.json) records the official registry/version, immutable OCI manifest and layer hashes, pinned GitHub commit/source URL, byte counts and inventory. The complete [MIT license](../vendor/azure-verified-modules/LICENSE) is retained and verified against all five distinct source commits. The project wrappers are AVM-backed consumers, not newly certified or published AVM releases. See [the official AVM repository](https://github.com/Azure/bicep-registry-modules).

Keep upstream artifacts byte-for-byte. Do not edit their defaults, child templates or metadata to change application behavior; change and review a project wrapper instead. A version upgrade requires acquiring a new official manifest/layer, updating fixed verifier pins and provenance, reviewing the full upstream interface/security/cost change, regenerating contracts and running qualification. No startup fetch or implicit latest-version upgrade occurs.

The native inventory remains [bicep-module-contracts.json](../config/bicep-module-contracts.json), and the six reviewed wrappers have a separate [avm-module-contracts.json](../config/avm-module-contracts.json). The application merges them after validating source and dependency closures. Draft archives include each selected wrapper and its required local compiled artifact, OCI manifest, shared bundle provenance and license; generated receipts hash that closure. They can be compiled without restoring these modules from MCR. Retain the relative paths and provenance when reviewing or promoting the draft.

Run these commands from the repository root with the existing prepared toolchain:

```powershell
# Prepare the pinned checkout-local Windows compiler; no global tool changes.
./scripts/Setup-AvmTooling.ps1
./scripts/Setup-AvmTooling.ps1 -VerifyOnly

# Verify exact upstream artifacts without network access.
./scripts/Verify-AvmSources.ps1

# Regenerate and review both the 17 native and six AVM contracts.
./scripts/Update-BicepModuleContracts.ps1
./scripts/Update-BicepModuleContracts.ps1 -Check

# Draft contract tests use a separate artifacts build directory.
./scripts/Test-BicepDrafts.ps1
./scripts/Test-AvmModules.ps1
```

The local compiler is Bicep 0.48.1, checked against its official Windows ARM64/x64 release SHA-256. Add its `.tools/bicep/0.48.1` directory to this shell's PATH when invoking the general contract/draft scripts, and use a writable checkout-local `DOTNET_BUNDLE_EXTRACT_BASE_DIR` in restricted environments. `Test-AvmModules.ps1` sets these values for its own process and restores them afterward. The tool preparation script verifies the already-vendored AVM files; it does not upgrade their release pins. [Official compiler release assets](https://github.com/Azure/bicep/releases/expanded_assets/v0.48.1).

The contract updater invokes `Update-AvmModuleContracts.ps1`, which first runs `Verify-AvmSources.ps1`. The source verifier rejects changed bytes, unexpected files, links, wrong pins and external deployment-template links. Generated AVM contracts include expanded child schemas and exact source closures; the application rejects drift during catalog reading and emission. Test output and dated validation belong in [validation history](validation.md), separate from Azure or ADO acceptance. Follow the [documentation and source-manifest workflow](../.agents/skills/self-service-docs/SKILL.md) after final edits.

The [real AHP/Codex end-to-end record](avm-end-to-end-validation.md) verifies composition of all six wrappers plus a workspace, seven typed child inputs and the actual 31-file source closure with offline compilation. It includes repeatable directory/ZIP checks and preserved failing evidence. These modules have no new workload YAML root, target profile or deployment permission. Promotion still requires the adapter, stable parameter/naming interface, private networking and identity configuration, cost review, protected Preview/Deploy bindings and actual acceptance described in [workload onboarding](workload-onboarding.md).
