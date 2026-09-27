# Self-service expansion and enhancement plan

**Network delivery update, 27 September 2026:** scoped tenant/management-group inventory, evidence-bound rendered agent diagrams, deterministic network findings and AVNM reservation/create-only spoke pipeline source are delivered. These are bounded capabilities, not completion of the enterprise networking roadmap. See [implemented scope and remaining gates](network-discovery-and-diagrams.md).

**27 September 2026 agent increment:** four [Codex/MCP evidence reviews](agent-workflows.md) now have explicit authentication/readiness and Run controls. Local checks are recorded in validation; live provider/model acceptance is pending. Next evidence adapters should add original discovery-pair auditing, policy/RBAC prerequisites, dated cost review and multi-subscription network coverage. Full AHP chat/action synchronization, `azmcp` hosting and write/IPAM workflows remain separately gated work. Earlier roadmap statements that all AI/MCP execution is future are superseded for these four bounded reviews only.

**27 September 2026 product delivery:** Private Storage Workspace, Key Vault, Observability, HTTP Functions API and Service Bus worker now have source compositions, typed adapters, dedicated Discover/Deploy menus, portal registration and disabled environment profiles. See [onboarding and remaining acceptance](workload-onboarding.md). This completes their initial source implementation, not price qualification, platform setup or Azure acceptance. Automatic networking allocation and the remaining candidate products stay proposed.

**Status: offline reporting, portal discovery and five additional workload implementations delivered; remaining roadmap proposed.** Reviewed 27 September 2026. Phased implementation is authorized by the repository operator; resource creation, deletion, new permissions, spending and live acceptance still follow their explicit target/profile gates. Priorities below are not delivery dates or claims of completed future work.

**Implementation readiness:** the [final design review and validation plan](plans/enhanced-self-service-validation.md) is the controlling implementation sequence. Offline contracts/reporting are ready to build; automatic allocation and live promotion require the listed ADO, IPAM and Azure acceptance gates. The review corrects approval-stage timing and consolidates evidence, compatibility and recovery requirements.

## Outcome and starting point

**27 September 2026 connected-diagram increment:** observed portal inventory, seven conceptual workload diagrams and saved ADO Preview resource-action diagrams are implemented with searchable/exportable views. The last same-subscription inventory can be compared in the configuration panel. This delivers the visual handoff described in [connected diagrams](portal-diagrams.md); executing Microsoft skills/MCP, automatic network selection, live connectivity validation and Azure/ADO acceptance remain separate work.

**27 September 2026 increment:** the local portal now includes all 42 pinned Microsoft skill definitions with **No pipeline associated yet** labels and browser-authenticated, read-only Azure resource inventory. The networking profile collects configured topology without requiring address inputs; occupancy, effective connectivity, IPAM authority and allocation remain unknown or proposed. This advances the discovery experience, not the number of registered deployment products. See the [implemented discovery contract](azure-skill-discovery.md).

**Implementation follow-up:** initial saved-evidence analysis/reporting and four local review skills are delivered; see [progress and boundaries](plans/implementation-progress.md). The five new product implementations are documented above; broader product, networking allocation and operational offerings remain proposed. User authorization to proceed with phased implementation does not establish completion of their technical/live acceptance gates.

Build a catalog where developers choose a supported product, understand its resources and costs, select an approved environment and review a plan before deployment. Each product must define ownership, dependencies, identity, application delivery, evidence, support and retirement.

Preserve GitHub source, ADO native menus, separate Discover runs, Preview/Deploy, input hashes, local Bicep modules, Template Specs, Deployment Stacks, conservative change gates and runtime-specific readiness. Keep module registry work out of the critical path. The [catalog](self-service-catalog.md) and [status matrix](completion-status.md) describe the shipped baseline.

The original two adapters are retained. Five new offerings use a shared, code-allowlisted product adapter, typed release bundles and explicit infrastructure-only versus Functions package lifecycles. A new `config/workloads.json` entry alone remains insufficient: composition, contracts, menus, tests, pricing and acceptance must be added together as described in the [onboarding guide](workload-onboarding.md).

## Analysis capabilities using Microsoft Azure skills

The [Azure skills assessment](azure-skills-assessment.md) reviews Microsoft's catalog at a pinned revision and proposes a curated analysis layer. Prioritize resource lookup, resource visualization and the enterprise infrastructure planner's research guidance, then add scoped compliance, quota and cost assessment. Preserve the existing Template Spec/Deployment Stack pipeline; upstream preparation, deployment and remediation workflows must not become an alternate apply path.

Start with **A0: workload-specific topology and analysis reports from saved discovery evidence**, without requiring AI or a live subscription scan. Follow with complete scoped collection, deterministic assessment, optional AI explanations and bounded operational analysis. These increments complement the networking N0–N6 work below; they do not replace authoritative IPAM, ownership or protected review. Skills are guidance and integration candidates, not newly implemented pipeline features.

The [platform-specific skills and MCP contracts](plans/platform-mcp-skills.md) design twelve focused skills for request design, discovery auditing, topology, private connectivity, identity, capacity, cost, change review, release readiness, runtime triage, lifecycle review and product onboarding. A shared typed facade exposes deterministic assessment results; pipeline and allocation writes stay behind the existing protected execution model. Skill entrypoints and server tools will be implemented incrementally when their contracts are ready, rather than installed as nonfunctional stubs.

## Candidate products

The Storage, secrets/Key Vault, Observability, HTTP Functions and Service Bus rows now have an initial implementation; their remaining acceptance criteria remain open. Other rows are proposed offerings. Cost drivers below are inputs to reviewed estimates, not price quotes.

| Priority / product | Proposed resources | Dependencies and boundaries | Acceptance / cost drivers |
|---|---|---|---|
| P1 foundation — Network analyzer and private connectivity | Read-only topology/capacity analysis, governed network profiles, authoritative address allocation, subnet/DNS/endpoint bindings and optional AI explanations. | Connectivity-owned IPAM and shared services; no developer CIDRs, arbitrary subnet adoption or AI write authority. See the [detailed networking plan](plans/private-networking-self-service.md). | Concurrency/recovery, scope coverage, DNS/routing and runtime/negative-path tests; networking, inventory/probes and optional AI usage. |
| P1 — Private storage workspace | Blob/queue storage, containers, scoped RBAC, private endpoints and diagnostics. | Explicit data owner/retention and reviewed networking/DNS; no implicit shared-account import. | Authorized access and unauthorized denial; capacity, operations, endpoints and logs. |
| P1 — Application secrets foundation | Key Vault, identity grants, private endpoint and diagnostics. | Reviewed recovery protections; secret values supplied through a separate secure channel, never YAML/runtime parameters. | Identity retrieves test secret; unauthorized identity fails; recovery review; operations/endpoints/logs. |
| P1 — Observability package | Dashboards, alerts and diagnostic settings with approved action-group references. | Explicit shared-workspace reuse or dedicated ownership; notification destinations reviewed separately. | Synthetic alert delivered/resolved and queries scoped to target; ingestion, retention and alerts. |
| P2 — HTTP Functions API | Function hosting, storage, identity, networking, telemetry and separate app package. | Supported hosting/network combination; authentication and ingress are product decisions. | Health/auth and version evidence; compute/executions/storage/egress. |
| P2 — Service Bus worker | Namespace, queue/topic, DLQ policy, worker hosting, identity and diagnostics. | Separate messaging infrastructure from code; research tier/network capabilities before admission. | Publish/consume, duplicate/retry/DLQ/replay tests; tier/capacity/worker/logs. |
| P2 — Web application | App Service plan/site, identity, networking, telemetry and versioned application delivery. | Approved runtime, ingress and external secret references. | Authenticated health and application smoke; compute/network/logs. |
| P2 — Azure SQL database | Reviewed database/server configuration, private access, Entra authorization, auditing and recovery policy. | Data classification, migrations and backup/restore need separate contracts. Destructive schema changes stay outside generic apply. | Identity access, migration validation, restore drill and audit evidence; compute/storage/backup. |
| P3 — Container application | Approved Container Apps environment/app pattern, identity, registry references and telemetry. | Image digest/provenance and shared-environment ownership defined. This is separate from Bicep module distribution. | Readiness, scaling, revision smoke and image verification; compute/network/logs. |
| P3 — Integration/data pipeline | ADF or another explicitly selected orchestration product with reviewed identities/networking. | Infrastructure, pipeline content and connector credentials are separate contracts. | Synthetic source-to-target run and retry/failure evidence; activities/runtime/data movement. |
| P3 — Event streaming | Event Hubs namespace/hub, identity, retention/capture policy and consumer references. | Capacity/data retention reviewed; no automatic transfer of an existing stream's ownership. | Producer/consumer lag and replay test; capacity/ingress/retention/capture. |
| P3 — AI application foundation | Approved model/project resources, identities, networking and observability for a separately defined AI application. | Quota, region/model availability, privacy, evaluation and token budgets researched at implementation time. Infrastructure alone delivers no agent or claims application. | Quota check, invocation, evaluation/security evidence and usage accounting; model usage/capacity/network. |

Avoid arbitrary “select any Azure resource” checkboxes. Product-level options must form a tested dependency graph. Disabling a private endpoint must not silently enable public ingress. Disabling a dedicated workspace must select an approved existing one or reject the request.

## Network intelligence and private connectivity workstream

**Proposed, not implemented.** Add a shared network planning service so developers select workload, environment, scale and named connectivity needs rather than subscription topology, subnet IDs or IP ranges. The [network analyzer, allocation and AI design](plans/private-networking-self-service.md) records the source audit, Microsoft Learn research, interfaces, pipeline changes, tests and staged implementation.

The current Event flow resolver uses reviewed fixed CIDRs, expected names and overlap checks against the selected subscription's visible VNets. It is a useful starting point, but cannot establish enterprise-wide address availability, allocate concurrently or infer hybrid reachability. Existing subnet flags are product-specific filters, not full network certification.

The proposed system combines:

1. **Coverage-aware discovery:** collect approved cross-subscription topology, authoritative external address allocations, subnet capacity, DNS, routing, security policy and ownership. Unknown required evidence blocks allocation.
2. **Deterministic analysis:** validate hosting-specific delegation, scale/headroom, address overlap, required private paths and ownership before ranking allowed candidates. Reuse stable assignments or request an approved new allocation.
3. **An authoritative allocator:** prefer Azure Virtual Network Manager IPAM where suitable, or integrate the existing enterprise IPAM. Add idempotency, concurrent reservation, reconciliation and safe retirement; neither a discovery artifact nor a Git JSON file is a reservation database.
4. **Complete private networking profiles:** central DNS/resolver reuse, endpoint placement, inspected or direct routing, explicit egress and connected private agents. Shared connectivity resources retain their own owner and lifecycle.
5. **Optional generative assistance:** translate natural language to a validated intent, explain selected/rejected options and suggest reviewed remediation. AI cannot choose unregistered ranges, bypass blockers, invent approvals or execute infrastructure changes.

Preview-only remains non-allocating. Where addresses are not yet bound, label its result provisional. An authorized deployment must acquire an assignment, resolve an exact plan, rerun validation/What-If and apply approval to that final plan before resource changes. Native allocation may require a separately approved network provisioning step; its semantics must be proved before integration. Existing frozen-input checks need an explicit versioned extension, not a bypass.

Platform onboarding still defines address authority, profiles, scopes and permissions once. Removing CIDR fields from the developer menu does not eliminate those responsibilities. The current one-subscription service connection cannot establish visibility of an entire enterprise network.

Deliver in order: **N0** authority/profile contract; **N1** read-only analyzer; **N2** allocation backend spike and transaction broker; **N3** one-region private networking pilot; **N4** qualified enterprise topology profiles; **N5** AI intent/explanation; **N6** capacity, drift and recovery operations. Start the analyzer alongside the product contract, and require allocation/concurrency acceptance before automatically placing new workloads.

## Phases and exit gates

| Phase | Deliverables / code area | Exit gate and owner |
|---|---|---|
| 0 — Accept existing products in Azure | Complete dev onboarding, real preview/apply, private connectivity, package indexing, smoke, failure/recovery and alerts. | Platform and workload owners accept retained evidence for both products; mocked tests alone cannot satisfy this gate. |
| 1 — Establish a product contract | Versioned metadata, typed adapter dispatch, dependency/ownership schema, menu/cost metadata and supported operations. Refactor `workload-common.ps1`, `platform-contract.ps1`, generator and qualification/preview dispatch. | Both existing products preserve inputs, protected bindings and rejection behavior; unknown IDs/paths cannot execute code. |
| 1N — Establish network intelligence | Networking phases N0–N2: authoritative scopes/profiles, read-only analyzer and qualified IPAM transaction backend. | Required coverage is explicit; subnet rules, concurrency, final-preview and crash recovery contracts pass before auto allocation. |
| 2 — Add foundation products | Private storage and secrets pilots, then observability; modules, wrappers, parameters, fixtures and docs. | Qualified estimates, create/reuse tests, real Azure readiness, disabled-by-default targets and a support owner for each. |
| 3 — Add application/data products | HTTP API, worker and web app before database, streaming and integration. Add package kinds and readiness contracts incrementally. | Immutable content and runtime failure paths verified; restore/migration controls accepted before data products launch. |
| 4 — Mature releases and operations | Promote existing qualified application artifacts by digest; upgrade previews; inventory/drift reports; explicit recovery/adoption/retirement. | Same approved application bytes reach QA/prod; tampering fails; operations preserve retention/approval boundaries and receipts. |
| 5 — Improve request experience | A local [Platform Studio portal](local-portal.md) now implements catalog/skills/analysis and reviewed ADO requests. Entra and live pipeline acceptance remain pending. Shared hosting and richer runtime MCP/AI features remain proposed. | Developers understand requests without reading Bicep. The portal uses the same registered targets and protected pipeline; wider rollout requires the documented live acceptance checks. |
| 6 — Scale governance/support | Version/deprecation policy, least-privilege roles, quota/admission checks, audit retention, cost allocation and dashboards. | Named owners, supported versions, incident/recovery procedures and measured adoption. |

These are capability gates, not a strict serial schedule or calendar promises. The [integrated V0–V6 sequence](plans/enhanced-self-service-validation.md#integrated-implementation-sequence) permits offline reporting/contracts while existing-product Azure acceptance proceeds separately. Live acceptance remains a prerequisite for enabling the enhanced apply path. A third product must not weaken existing checks to accommodate an unmodeled lifecycle.

## Proposed product contract

A future `config/products/<product-id>.json` can hold versioned metadata; this directory does not exist yet. Executable adapter registration remains in reviewed code, never user input or an artifact. The schema should include:

- Identity: product ID, display name, semantic version, lifecycle status and support owner.
- Composition: approved main/stack/parameter/schema paths and package kind (`none`, Functions ZIP, Logic App ZIP or another qualified kind).
- Inputs: required/optional fields, defaults, types, allowed values and the team owning each setting. No secret values in native runtime parameters.
- Dependencies: required discovery evidence, scope/region compatibility, permitted create/reuse/manage behavior and ownership boundary.
- Network requirements: hosting-specific subnet roles/delegations, capacity/headroom, private dependency flows, DNS/egress profile and versioned network binding contract; no arbitrary developer-supplied resource IDs or prefixes.
- Lifecycle: operations/phases, readiness contract, migration/retirement policy and application-delivery requirements.
- Cost: meter sources, retrieval/expiry time, fixed versus usage charges, exclusions and an Unavailable state.
- Evidence: artifact/schema versions, provenance, hashes, approval expiry and drift rules.

Define a stable adapter interface for request validation, prerequisite resolution, compilation/freezing, estimates, preview, apply and readiness. Use explicit typed dispatch. Infrastructure-only products need an intentional no-package path; do not fake an application ZIP or smoke result. Products may have different phase contracts; retain the current adapters as compatibility cases.

## Repository placement

```text
modules/<resource-type>/<implementation>/    reusable Bicep and contract README
workloads/<product-id>/main.bicep            product composition and outputs
workloads/<product-id>/stack.bicep           owned resource-group wrapper
workloads/<product-id>/modules/               product-specific helpers
workloads/<product-id>/environments/          reviewed environment parameters
workloads/<product-id>/request.schema.json    developer intent contract
workloads/<product-id>/README.md              resources, costs, methods, acceptance
self-service/targets/<instance>.<env>.json    initially disabled bindings
scripts/<product>-service-common.ps1          when a distinct adapter is needed
src/<Application>/                           application content when required
```

Update the allowlist, intent resolver, catalog generator, pipeline-resource names, qualification templates and infrastructure checks together. Reuse modules where their contracts fit. Keep shared platform foundations separately owned. Continue embedding local modules in compiled Template Specs; no module registry is required.

## Additional self-service operations

These proposed operations need their own request/authorization contracts:

| Operation | Required behavior and guard |
|---|---|
| Inspect / refresh inventory | Scoped read-only resources/readiness; no grants or provider registration. |
| Analyze private connectivity | Coverage, capacity and DNS/routing findings with candidate explanations; no address reservation in read-only analysis. |
| Request private network attachment | Authoritative allocation, final exact plan, connectivity-owner changes and runtime acceptance; follow the networking workstream rather than granting workload jobs shared-network ownership. |
| Compare drift | Desired-versus-observed report without automatic repair. Label temporary What-If metadata effects if native stack preview is used. |
| Promote release | Approved package digest and compatible infrastructure version; environment-specific parameters and new previews/approvals. |
| Scale within policy | Qualified SKU/capacity transitions with cost/quota checks. Deliberately extend and test current sensitive-SKU gates rather than bypassing them. |
| Recover failed workload | Typed operator actions, bounded retries and receipts; reuse existing Blob copy recovery contracts where suitable. |
| Extend/retire sandbox | Expiry metadata and reminders first; deletion requires ownership/retention checks and a reviewed deletion plan. Never recursively remove shared resources. |
| Adopt/migrate | Explicit mapping, ownership review, backup/recovery plan and resource-by-resource consent. Implicit adoption remains rejected. |

## Quality and publication requirements

Each product requires an input schema, inventory, sample request, readiness definition and owner. Test empty/failed discovery, reuse, managed resources, wrong scope/region, partial resources, stale/tampered evidence, topology/security changes and partial deployments. Add product-specific live tests: database restore, queue DLQ/replay, or API authentication as appropriate.

Validate generated YAML with ADO as well as the local expression checker. Exercise Preview-only and enabled Deploy, least-privilege publication/deployment, retained hashes and smoke results. Verify current service/API/SKU availability and prices when implementing each product; do not reuse another product's hosting rates or claim free Azure resources.

For a public template distribution, separate organization-specific targets, identity IDs and authorization records from reusable examples. Public examples should have deployment disabled and approvals false. Scan files/history, regenerate the manifest and document private bindings. This is planned packaging work, not sanitization performed by this document.

Measure time to a valid preview, time to Ready, failure causes, stale-discovery rejection rate, recovery time, artifact promotion coverage and estimate freshness. Establish baselines from real runs first; no performance targets are claimed as achieved.

## First implementation backlog

1. Characterize current routes, define versioned evidence/product contracts and compatibility tests, and generate workload-specific reports from saved discovery fixtures (V0/V1). Include an infrastructure-only fake adapter without registering a new live product.
2. Collect missing existing-workload Azure acceptance separately; it need not block offline development. Preserve reproducible receipts before enabling an enhanced apply pilot.
3. Establish network authority/profile contracts, complete scoped discovery and deterministic analysis; move existing workloads behind typed registration while preserving current behavior (V2).
4. Prove one IPAM backend's reservation, fencing, recovery and native-allocation behavior in an approved sandbox (V3).
5. Refactor the automatic-allocation route into separate planning and protected apply stages; qualify one-region/profile new-instance acceptance before opt-in activation (V4). Follow the final validation matrix, not a two-stage appearance requirement.
6. Implement private storage, then secrets/observability using accepted bindings; add immutable application promotion before expanding production applications (V5).
7. Add operational requests and optional AI explanations after deterministic controls work; assess a portal from native-menu usability evidence (V6).

Foundation references: [ADO runtime parameters](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/runtime-parameters?view=azure-devops), [Template Specs](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/template-specs), [Deployment Stacks](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks). Proposed products need service-specific research during implementation.
