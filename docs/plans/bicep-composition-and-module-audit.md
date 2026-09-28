# Bicep composition and module audit

Reviewed 27 September 2026 against the current source. This records implemented behavior, the source-draft increment and the remaining expansion plan. It does not qualify any Azure deployment. The user selected reviewable source drafts before deployment qualification; module registry adoption remains deferred.

## Audit conclusion

The platform now generates **new local Bicep compositions from existing modules** through a structured AI proposal and deterministic emitter. It does not let a model directly deploy code or dynamically choose Bicep import paths during ARM execution. The current registered deployment system remains seven fixed workload compositions and 28 disabled profiles. The new source-draft workflow has its own evidence/contract validation and cannot enter Deploy until promoted and qualified.

Microsoft describes local relative module references and compilation into nested ARM templates. This supports the chosen design: decide the graph first, then emit literal module paths and compile the complete artifact. Conditions/loops can control resources in that graph; runtime strings are not a module loader. [Bicep modules](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/modules).

## Existing flow and concrete gaps

| Source | Verified responsibility | Gap for fully dynamic self-service |
|---|---|---|
| `config/workloads.json`, `Get-WorkloadDefinition` | Seven exact composition/stack paths, package kinds and adapters | Adding JSON alone cannot create a supported product. New adapter, source and tests are required. |
| `Resolve-PlatformRequest` | Exactly one target for workloadName/environment/region/workloadType | No arbitrary capability graph in the deploy request. Preserve this boundary; introduce a separately versioned composition contract. |
| `New-WorkloadPreviewInputs` | Compile main, stack and environment params; apply target overrides; resolve supported prerequisites; freeze inputs | Production parameter resolution is PowerShell and differs across adapters. The new portal draft does not silently reimplement it. |
| `Get-LogicPrerequisitePlan` | Event flow Create/Reuse/Manage/Blocked from complete discovery plus ownership/policy | Generalizing this requires per-resource policy and ownership, not just a missing ID check. |
| `Assert-ProductDiscovery`, `Test-ProductPrerequisites` | Five new products require existing approved shared network/DNS/workspace; direct DNS-link profile | No general shared-dependency create path, custom-DNS profile or IPAM-to-workload automatic binding. |
| `New-StackContract`, `Read-ProductBundle`, Preview/Deploy gates | Immutable compiled content, exact target/source, ownership and drift checks | New compositions need qualification before using these adapters. Never replace compiled content after Preview. |
| `AgentWorkflows`, new `BicepDrafts` | Original workload agent remains advice; new draft agent validates and emits source | Live model acceptance and automatic parameter resolver handoff remain outstanding. |

The audit generated [machine-readable interfaces](../../config/bicep-module-contracts.json) from the compiler rather than guessing input names. Regenerate with `./scripts/Update-BicepModuleContracts.ps1`; check with `-Check`. All 13 shared modules are accounted for. Workload-local modules remain workload-specific and are not exposed as interchangeable graph nodes.

## Module inventory and required changes

| Existing module | What can vary today | Constraint / next change |
|---|---|---|
| `storage/storage-account` | Name, region, tags, LRS/ZRS, containers, queues, workspace | Untyped container/queue arrays; diagnostics and table service bundled. Add typed collections and reviewed retention/diagnostic profiles. Do not expose public access or key auth as casual checkboxes. |
| `network/private-endpoint` | Name, region, tags, subnet, target ID, groups, DNS IDs | Good reusable leaf. Add service-specific group/DNS compatibility rules; normalize `privateEndpointId` output with a compatible `id` alias. Subnet eligibility is external. |
| `network/workload-vnet` | Name, region, tags and three prefixes | Fixed two-subnet layout/delegation and integration NSG. New VNet ownership only; not a safe writer for arbitrary existing VNets. Return typed subnet IDs; split reviewed topology profiles, with IPAM bindings. |
| `network/private-dns-zone` | Zone/link names, tags, VNet ID | Creates zone and link together. Add an existing-zone link module to avoid adopting a central zone. DNS resolver/forwarding is a different topology. |
| `network/ipam-reservation` | Manager/pool/allocation/description; addressCount allowed only 256 | Already exists, excluded from AI composition drafts. Preserve connectivity-owned allocation workflow and durable reservation; expand sizes only with AVNM evidence/tests. |
| `monitoring/log-analytics` | Name, region, tags | Fixed PerGB2018 and 30 days. Add policy-approved retention/daily-cap profile and outputs; cost/cap effects must be disclosed. |
| `monitoring/observability` | Name, region, tags, workspace, action groups | Embeds a heartbeat workbook/query. Split generic reviewed workbook and scheduled-query-alert modules; only qualified query/threshold profiles, not arbitrary agent KQL execution. |
| `event-grid/topic` | Name, region, tags | Identity/private defaults included. Add name output to simplify typed binding; topic type/event schema changes require compatibility review. |
| `event-grid/event-subscription` | Existing topic name, storage ID, queue and dead-letter container | Hardcoded `document-received`, Document.Received filter, subject prefix, retention/retry. Add constrained name/filter/retry parameters with current defaults; identity/grant/trusted-delivery dependencies remain mandatory. |
| `logic-app/standard` | Names/storage/topic/subnet/monitoring, activation, WS1/2/3 | Runtime settings tied to Event flow; credential exception and Azure Files/runtime storage are external dependencies. Separate hosting profile from application settings/activation before treating this as generic Logic Apps. |
| `compute/private-functions` | Names, identity/client IDs, subnet/storage/package/workspace, http/worker mode and API settings | B1/.NET 10 and sample function names/bindings embedded. Separate plan/hosting profile from runtime configuration. SKU/version changes need validated combinations and application-package tests. |
| `security/key-vault` | Name, region, tags, workspace, one reader principal | RBAC role, Standard, 90-day purge protection fixed. Keep protection mandatory; use reviewed optional reader grants rather than inventing principals. Secret population stays outside the source generator. |
| `messaging/service-bus` | Name, region, tags, workspace, worker/sender principals | Premium capacity 1, work queue, TTL/DLQ/retry/duplicate profile embedded. Add constrained queue/namespace profiles and outputs, keeping Premium for the qualified private topology. |

These are locally maintained modules, not Azure Verified Modules. Follow explicit interfaces and constraints, secure defaults and environment separation; use versioned user-defined types for complex inputs as interfaces mature. [Microsoft Bicep best practices](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/best-practices), [user-defined types](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/user-defined-data-types).

## Additions, in priority order

| Priority | Proposed source | Why needed / ownership |
|---|---|---|
| P0 | `modules/identity/user-assigned-identity/` | HTTP and worker duplicate inline identity resources. Explicit name/location/tags; output id/principalId/clientId. Workload-owned. |
| P0 | Resource-specific `access` modules under storage, messaging and security | Reuse least-privilege grants now embedded across compositions. Approved role sets, principal **object** ID/type, deterministic assignment name and exact resource scope. Do not implement a universal arbitrary-scope Owner grant module. |
| P0 | `modules/network/private-dns-link/` | Link an approved VNet to an existing platform-owned zone without declaring the zone as owned. Qualify zone-RG/identity permissions and link ownership. |
| P0 | `modules/monitoring/application-insights/` | Workspace-based telemetry is currently workload-specific/inline; Functions/Logic compositions need explicit reusable outputs and diagnostic compatibility. |
| P1 | `modules/monitoring/action-group/`, reviewed alert/workbook leaves | A new observability product cannot invent recipients. Keep existing action-group reuse; create only from an approved notification profile. |
| P1 | `modules/network/nsg/`, `route-table/`, new-owned-subnet adapter | Separate platform networking policies from app modules. Existing-VNet changes require one authoritative writer, explicit ownership and concurrency/drift controls to preserve sibling subnets. |
| P1 | `modules/network/private-dns-resolver/`, forwarding/ruleset links | Qualify hub/custom-DNS estates now blocked by direct-link pilot. Need hub permissions, routes, inbound/outbound dedicated subnets and resolution tests. |
| P1 | `modules/compute/app-service-plan/` plus generalized host profiles | Reuse an approved plan or create an owned plan without coupling plan count to app count. No cross-stack adoption of a shared plan. |
| P2 | Service-specific storage children, extra messaging destinations | Add when a real recipe needs them; do not expose every Azure property prematurely. |

These additions are **planned, not created by this increment**. The source-draft generator reports unavailable modules rather than synthesizing unreviewed implementations. General SQL, Container Apps, AKS and other new workloads require their own reviewed modules, collectors, constraints, runtime/package and verification contracts.

## Parameterization and provenance target

| Parameter class | Authority / resolution | Model responsibility |
|---|---|---|
| Workload, environment, region | Existing four-field request and approved target | Recommend a registered context; cannot change subscription/connection. |
| Names | Versioned naming policy; preserve existing resource names; provider-specific length/charset/global uniqueness checks | Explain names; never rename discovered resources implicitly. Current drafts leave names required. |
| owner, costCenter, customTags | Reviewed profile and existing custom-tag rules; required tags win | Suggest gaps; do not invent owner/finance values. Draft tags stay unresolved unless explicitly supplied through future typed rules. |
| IDs, subnet/DNS/workspace bindings | Approved source settings plus fresh discovery, type/scope/eligibility checks | Cite observed candidates; observation does not authorize reuse or adoption. |
| Address space | AVNM reservation and approved topology profile | Explain placement alternatives; cannot reserve/guess free space. |
| SKU, retention, retry, capacity | Environment/service policy with constrained allowed values and costs | Recommend among qualified profiles. No security-relaxation fallback. |
| Principal/RBAC/authentication | Reviewed identity bindings; distinguish app/client ID from principal object ID | Report missing role/consent checks. Never infer Owner permission or create consent. |
| Package and lifecycle | Qualified release ID/hash and adapter, including Foundation/Release where needed | No arbitrary URL, package path, executable settings or activation toggle. |
| Secrets | Secure references/runtime identity; no literal values in model projection/drafts | No collection or generation of credentials. |

Production precedence remains environment parameter file → allowlisted target overrides → adapter-specific discovery resolution with conflict checks → host-owned lifecycle values. The draft adapter currently uses intent plus selected overrides only; it labels all other values unresolved. The next resolver increment must call or extract the **same** deterministic production resolver, not parse Bicep text or invent a second interpretation.

Add versioned contracts for capability dependencies, input semantic types (`SubnetId`, `WorkspaceId`, `PrincipalObjectId`, etc.), create/reuse/managed ownership and output compatibility. Today the emitter checks primitive types and a narrow observed-ID mapping; semantic rules and graph completeness remain review responsibilities. Keep module parameters explicit, with typed objects for coherent settings, rather than a catch-all options dictionary. Add defaults only when they preserve existing behavior; introduce incompatible changes through qualified versions.

## Delivery phases and acceptance

| Phase | State | Acceptance |
|---|---|---|
| G0 audit/contracts | Implemented locally | Every shared module accounted for; compiler-derived catalog, source drift check and documentation. |
| G1 AI source drafts | Implemented locally | Typed workflow/skill/UI; bounded current-browser evidence; known modules/references; acyclic graph; parameter provenance; downloadable main/stack/modules/receipts. Negative tests and emitted-template compilation. Live Codex/Azure manual acceptance pending. |
| G2 reusable module/profile hardening | Planned | P0 modules, typed input/output semantics, compatibility-preserving extraction, compiler/tests and golden resource diffs. |
| G3 unified resolution | Planned | Shared resolver produces effective values and Create/Reuse/Manage/Blocked from qualified discovery. Same resolver used by portal and ADO; deterministic tests for missing/denied/stale/conflicting inputs. AVNM binding remains explicit. |
| G4 qualification pipeline | Planned | Validate draft archive provenance, normalize source locations, secret scan, module-source lock, compile/lint, dependency/ownership/security/cost tests, reviewed source promotion. No arbitrary executable steps from the draft. |
| G5 registered Preview/Deploy | Existing path, new-product integration required | Disabled target onboarding, immutable Template Spec/Stack contract, complete What-If, reviewed exact bundle, protected approvals, drift recheck and verification. Never regenerate with AI between Preview and Deploy. |

Fail closed for absent contracts, unknown references, cycles, unsupported secure/advanced types, changed module hashes, wrong browser/scope and expired evidence. Preserve partial discovery as partial. Missing optional modules/security dependencies may still yield a **draft**, never deployable status. Model prose cannot set readiness.

Suggested first qualification scenario: new storage-plus-dedicated-monitoring composition, reviewed consumer access, two endpoints and existing approved subnet/DNS. Test successful reuse, supported owned creation, wrong DNS group, no workspace, incomplete inventory, no identity/role permission, naming collision, source-hash change, overlapping address claim and changed Preview. Then extend hosting/messaging recipes. Existing deployments must show no unintended resource renames or ownership changes.

See [draft operation and method guide](../bicep-source-drafts.md), [workflow standard](../agent-workflow-standard.md), [private networking](private-networking-self-service.md) and [workload onboarding](../workload-onboarding.md).
