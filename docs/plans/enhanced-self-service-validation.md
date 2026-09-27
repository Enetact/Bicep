# Enhanced self-service: implementation readiness and validation plan

**Review date: 26 September 2026. Status: design reviewed; initial offline implementation delivered; later increments and Azure acceptance pending.** This document consolidates the [expansion roadmap](../self-service-expansion-plan.md), [networking design](private-networking-self-service.md) and [Azure skills assessment](../azure-skills-assessment.md). It is the controlling sequence and validation checklist for implementation. Product-specific details remain in those documents. Targets, permissions and cloud resources remain unchanged.

## Readiness decision

**Implementation follow-up:** V0/V1's offline analysis contract/readers/reporting and four local review skills are now implemented. The Discover integration is authored; live rendering and later phase acceptance remain pending. Use [implementation progress](implementation-progress.md) for delivered scope rather than treating the design-review snapshot above as current implementation status.

The [platform skills and MCP design](platform-mcp-skills.md) specifies twelve focused analysis skills and sixteen proposed typed tools for this sequence. V0/V1 starts with saved-artifact request/discovery/topology/change-review workflows; deterministic libraries remain usable without MCP or a model. No proposed skill grants allocation, pipeline submission or deployment authority.

**Ready to implement offline contracts, reporting and compatibility tests. Not ready to enable automatic network allocation or production deployment.** Start with saved discovery fixtures and deterministic reports. In parallel, platform owners can establish the external prerequisites and existing-product Azure acceptance needed for a later live pilot.

Do not interpret “final validation” as a successful Azure deployment or an approval from an owner. This review validates the design against the current source and Microsoft documentation; execution gates below still require their own evidence. Unknown organizational inputs block only the increments that depend on them, not all development.

## Findings and decisions from the review

| ID / priority | Finding | Decision for implementation |
|---|---|---|
| F01 / critical | The networking proposal suggested separate planning and approval jobs while retaining two stages. ADO protected-resource checks run before a stage, not between its jobs. | A newly generated final plan must exist in an earlier stage than the stage whose protected resource approves it. Use the stage topology below for automatic allocation. |
| F02 / high | “Read-only Preview” conflates inventory with native stack What-If. `New-StackPreview` creates and deletes a preview-result resource. | Inventory/reporting are read-only. Azure Preview is non-deploying but needs narrowly scoped preview-metadata operations and a cleanup receipt. Show this distinction in the menu and report. |
| F03 / high | Current preview requests `P1D` retention and deletes in `finally`; cancellation can interrupt cleanup. Microsoft documents no automatic deletion for retention longer than three hours. | Record metadata resource IDs; test interrupted cleanup; add a bounded reconciler for platform-owned preview results. Evaluate a supported short retention in the compatibility spike. Do not assume retention is a cleanup guarantee. |
| F04 / high | Expansion phase 0 appeared to block all engineering on live acceptance, while the skills plan starts offline reporting immediately. | Establish one dependency sequence: offline reports/contracts now; live acceptance before enabling new apply/allocation paths. |
| F05 / high | Network and analysis plans proposed overlapping graphs and findings without a shared schema boundary. | Use one versioned evidence envelope and resource graph; network inventory and rule results are typed projections. Do not collect or resolve the same facts independently for AI and deployment. |
| F06 / high | Address assignment may not be known until native network creation. A provisional plan cannot approve an exact unknown prefix. | Qualify a reservation-capable backend first. Native-create allocation is a separate explicitly authorized network operation; only afterward can dependent workload validation be exact. |
| F07 / high | Hashes, runtime variables and a “reviewed” JSON flag do not establish approval or trusted origin. | Authenticate producer/run provenance and binding issuer; protected checks approve a run/stage whose immutable plan already exists. Apply independently verifies those same inputs and live eligibility. |
| F08 / high | Existing workloads combine some workload-owned network declarations with application composition. Moving those resources into a connectivity stack can detach or change ownership. | No automatic migration/adoption. Add an opt-in new-instance path first; maintain existing managed declarations. Existing-instance migration requires a separate reviewed ownership plan. |
| F09 / medium | A0 includes rendering in ADO, but a local fixture test cannot prove the ADO renderer or authorization behavior. | Split offline artifact acceptance from a read-only ADO integration pilot. Verify downloaded diagram rendering and summary fallback explicitly. |
| F10 / medium | The generic/legacy deployment entry remains reachable alongside dedicated menus. | Test every reachable route against the same admission gates, or explicitly disable unsupported operations there. A safer dedicated menu must not leave a weaker legacy path. |

ADO checks are managed outside YAML and evaluated for protected resources before their consuming stage starts. Job dependencies do not defer those checks. [Microsoft approvals and checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops)

Stack What-If stores its result as an Azure resource and has retention/cleanup behavior distinct from ordinary deployment What-If. [Microsoft stack What-If](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks-what-if)

## Source baseline and extension points

The following was inspected for this review; it is not evidence that the future behavior already exists.

| Current source | Verified behavior / extension responsibility |
|---|---|
| `config/workloads.json`, `scripts/workload-common.ps1` and `scripts/platform-contract.ps1` | Two explicitly supported workload adapters. Introduce a versioned product contract behind compatible typed dispatch; never execute a path provided in a request or artifact. |
| `scripts/Update-ServiceCatalog.ps1`, `pipelines/deploy-entry.yml`, dedicated root YAML | Generated, statically bound menus; Preview only default; target enablement controls compiled deployment jobs. Add profile/capacity choices through the generator and schema together. |
| `pipelines/templates/self-service-discover.yml`, `scripts/Export-DeploymentInventory.ps1` | Existing discovery/artifact producer. Extend via bounded collectors and explicit scope/query coverage. |
| `scripts/discovery-manifest-common.ps1` | Current workload-specific manifest versions, successful protected-main provenance, hashes, target checks and seven-day discovery limit. Keep compatibility readers; newer volatile allocation facts need shorter policy limits and live verification. |
| `pipelines/templates/self-service-two-stage.yml` | Preview followed by Deploy; BuildBundle, PublishTemplateSpec and ApplyStack share Deploy. Existing approval can review prior Preview; it cannot review a new artifact created later inside Deploy. |
| `scripts/workload-preview-common.ps1` | Same-run/source/input verification, 24-hour preview limit and pre-apply fingerprint comparison. Extend through a versioned contract, not by weakening comparisons or rewriting hashes. |
| `scripts/stack-service-common.ps1`, `config/deployment-stack.json` | Native stack validation/What-If; content-addressed Template Spec binding; `detachAll` policy. Preserve conservative destructive-change and ownership checks. |
| `scripts/logic-prerequisites-common.ps1` and `config/logic-prerequisites.json` | Current named/explicit prerequisite selection and fixed addresses. Keep as the legacy profile during the opt-in rollout; do not advertise it as enterprise IPAM. |
| `scripts/Test-DiscoveryHandoff.ps1`, `scripts/Test-WorkloadPreview.ps1`, `scripts/Test-DeploymentStacks.ps1`, `tests/infrastructure/verify.mjs` | Existing contracts to extend with new fixtures. Local expression checks do not replace ADO compilation, protected checks or Azure acceptance. |

The 26 September baseline had two registered products and eight disabled targets. The 27 September product increment now has seven products and 28 disabled targets; see [onboarding](../workload-onboarding.md). Existing live acceptance gaps remain in the [completion matrix](../completion-status.md).

## Target developer experience

Keep the four workload-specific Discover/Deploy entrypoints initially. Developers select instance, environment, region and, when admitted, a stable approved connectivity/capacity profile. `Auto` means deterministic selection within that profile, not arbitrary subscription-wide placement. No CIDR, executable path, service-connection name or arbitrary resource ID becomes an ordinary developer input.

Discover publishes saved evidence. Deploy consumes a selected successful, matching discovery run and defaults to Preview only. The report shows only the selected product: existing versus planned topology, Create/Reuse/Manage, unknowns and blockers, cost assumptions and next actions. New-network results remain provisional until allocation is bound.

For the enhanced allocation path, “Preview and deploy” authorizes entering the reviewed workflow; protected stages still control the relevant operations. The [local portal](../local-portal.md) now submits existing workload requests; support for this proposed enhanced allocation contract remains future work. It does not bypass catalog authorization or regenerate fields during an ADO run. ADO parameters are evaluated before execution. [Runtime parameters](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/runtime-parameters?view=azure-devops)

## Stage and permission topology

The current two-stage route remains the compatibility path for reviewed existing bindings. **Automatic allocation uses additional stages.** Correct approval timing takes precedence over preserving a two-stage appearance. Stage names here are proposed, not present in today's YAML.

| Proposed stage | Inputs and outputs | Authority / gate |
|---|---|---|
| `Preview` | Validate discovery and request; produce analysis, diagrams, costs and provisional or already-bound What-If. | Read collector plus explicitly permitted preview metadata operations. No allocation or workload apply. Disabled targets can use this route. |
| `ResolveNetwork` | Qualify the application package, then acquire/reuse an assignment; publish exact network plan and binding, or an explicit native-allocation proposal. | Enabled profile/target and protected allocation authority. Backend selection must already have passed its spike. No workload deployment identity. |
| `ApplyNetwork` | Verify approved network plan and assignment; apply only connectivity-owned changes; publish resource/assignment receipt. | Network-owner approval/checks evaluate the preceding stage's published plan. Serialize shared writes in the broker. No implicit ownership transfer. |
| `FinalizeRelease` | Validate actual network receipt, compile/freeze complete workload inputs and immutable application package, run workload What-If and publish final release manifest. | Preview authority and required private connectivity; no workload apply. Unexpected state becomes a new plan or a blocker. |
| `PublishTemplate` | Publish verified templates by content identity; return publication evidence. | Protected publisher identity/scope. Source and final release hashes must match; no workload writes. |
| `DeployWorkload` | Verify final plan/package/publication, recheck live state and binding, apply Foundation/Release and run readiness. | Workload-owner check on the final plan from the earlier stage; approved private runner and workload-scoped permissions. Publish failure/Ready receipts. |

When all network prerequisites are reused, network changes are an explicit no-op with a validated binding receipt. Prefer the same declared stage sequence with no-op receipts over trying to remove stages after discovery. Any optional stage must be declared at template expansion and its conditions tested: skipped stages must neither block legitimate reuse nor allow downstream work after failure. Do not fabricate a successful allocation or deployment. Current existing-instance flows must not be silently switched to this new topology. Reuse the qualified application package by digest in `FinalizeRelease`; do not rebuild it after qualification or review.

If native allocation chooses addresses during `ApplyNetwork`, the review approves a bounded pool/capacity/profile operation rather than a claimed exact prefix. `FinalizeRelease` must wait for the actual result. If policy requires an exact prefix before *any* network write, this backend mode is unavailable. AVNM offers pool allocation; its documentation does not establish the reservation-transfer transaction our service needs. [AVNM IPAM](https://learn.microsoft.com/en-us/azure/virtual-network-manager/concept-ip-address-management)

Run stage conditions with success dependencies, protected branch/manual intent and enabled-target checks. Avoid using `always()` on mutation jobs; reserve it for bounded evidence capture. Test failed, skipped, rejected, timed-out, rerun and canceled predecessors. Do not fetch “latest” discovery or silently select another run during retry.

## Evidence, compatibility and approval contracts

Define schemas and fixtures before orchestration changes:

1. **Common envelope:** schema/kind, organization/project/repository and producer definition/run, source commit, workload/instance/target, permitted scopes, collection interval, policy/collector versions and content hashes. Keep collected facts distinct from the interpretation/rule version.
2. **Graph and findings:** one canonical resource graph with evidence-backed edges and coverage state. `network-inventory.json` is a documented projection of those facts; it is not a second authority. Findings distinguish pass/fail/unknown/not-applicable. Create/Reuse/Manage/Blocked describes an action decision, not a query outcome.
3. **Binding:** authenticated issuer, assignment ID and immutable generation, routing domain, authoritative allocation, owners and allowed target/operation. Lease renewal status is authenticated separately; renewal with unchanged assignment must not require rewriting an approved content hash. Reassignment changes generation and invalidates the plan.
4. **Final release:** canonical digests of templates, parameter documents, application package, product/profile/policy, assignment generation, evidence and approved change classification. Exclude volatile telemetry/lease heartbeat timestamps from semantic comparison without excluding security or resource changes.
5. **Receipt:** operation ID, inputs, actual resources, assignment state, Azure operation status, readiness and outstanding cleanup. A deployment can succeed while runtime readiness fails; do not label both Ready.

Specify canonical encoding/order, allowed relative paths, maximum artifact/query sizes and rejection of traversal, unexpected file names and unknown schema majors. Adapt old manifests explicitly; do not fill missing enterprise coverage fields with assumed success. Bind old-reader/new-reader fixtures and minimum producer versions to a compatibility matrix. Introduce typed product registration independently from allocation so failures can be isolated.

Hashes detect changes but cannot authenticate an attacker-controlled producer. Validate ADO run provenance through authenticated APIs and validate broker-issued bindings with trusted credentials/issuer checks. The approval UI links to immutable run artifacts and shows their digests. Never accept a request-provided approver, `approved: true`, service connection or unsigned binding as authority.

Keep service connections and environments resolved from protected platform configuration at template expansion. The production platform must protect template/generator ownership, restrict pipeline authorization to connections, and configure required-template/branch/resource checks outside developer-controlled YAML. If application contributors can edit the authoritative templates, move that authority to a separately protected repository before granting them the production write path. Audit administrator bypasses as exceptional operations, not ordinary self-service.

## Allocation, locks and failure recovery

Use broker/backend atomicity for the allocation domain and shared network objects; an ADO per-environment lock is insufficient across products and pipelines. Qualify generation/fencing tokens so a resumed old run cannot write after a newer assignment. Never hold only an in-memory agent lock across approval waits.

The broker maintains bounded lease renewal/reconciliation while ADO waits, or expires unused proposals safely. If the chosen backend lacks renewal semantics, document an equivalent durable-allocation ownership model in the spike. The apply stage verifies assignment state immediately before writes. Expired or reassigned inputs cannot be repaired by changing artifact timestamps; they require renewed planning/review.

Cancellation does not prove Azure stopped. Persist operation IDs before polling; reconcile long-running Azure work before releasing space. A failure after `ApplyNetwork` leaves an owned network allocation and failed workload receipt, not an automatically deletable orphan. Retrying uses the same idempotency/assignment identity. Quarantine uncertain occupancy, retain evidence and issue a reviewed repair/retirement request. There is no automatic cross-stack transaction or rollback promise.

Keep preview-metadata cleanup separate from workload/network cleanup. A reconciler may remove only verified platform-owned expired preview-result IDs under its allowed scope; it must never infer deletion authority from a resource name alone.

## Integrated implementation sequence

This sequence reconciles expansion phases with A0–A4 and N0–N6; it supersedes a strict reading that all local development must wait for phase 0 cloud acceptance.

| Increment | Build scope | Prerequisites / completion evidence |
|---|---|---|
| V0: baseline and contracts | Characterize current dedicated and legacy routes; schemas, trust boundaries, compatibility readers and fixtures. | Existing generated menus check clean; current rejection/ownership semantics captured; no target changes. Platform owners start collecting missing live baseline evidence independently. |
| V1: offline report pilot (A0) | Pure evidence-to-graph/findings/Markdown renderer for the two products. Use sanitized saved manifests; no model, Azure call or new infrastructure. | Deterministic reports for empty/partial/populated fixtures, redaction and parser/render checks; pipeline summary rendering checked separately in a non-deploying ADO pilot. |
| V2: discovery and product contracts (A1, N0/N1) | Typed registration; read-only coverage collectors; profile schema and network rules; feature flags disabled. | Preserve both adapters and generic routes; provenance/paging/unknown-scope tests; read-only pilot uses approved scopes. Product contract does not itself authorize new profiles. |
| V3: allocator feasibility (N2) | One backend/API spike with fake adapter parity; receipt, idempotency, concurrency, cancellation and final-plan binding. | Backend transaction semantics proven in an authorized sandbox; owner signs off profile and recovery model; no automatic fallback to guessed CIDRs. |
| V4: stage refactor and bounded pilot (N3, A2) | Protected stage topology above, authenticated bindings, final What-If, publication/apply and private runtime acceptance. | Existing-workload live baseline accepted; one region/profile, new dev instances only; all matrix gates below pass before opt-in enablement. |
| V5: product and operational expansion | Private storage product, then secrets/observability; additional profiles N4; immutable package promotion before production expansion. | Named owners, real acceptance, costs and recovery/retirement per product/profile. Production enablement is separate. |
| V6: optional assistance and support (A3/A4, N5/N6) | Restricted explanations/intent, diagnostics, actual costs, capacity and drift reports. | Deterministic decisions identical with AI disabled; scoped telemetry, model/security evaluations and reviewed remediation flow. |

V1 can proceed without an IPAM selection or full product-registry refactor. V2 can proceed with fixtures before cross-subscription access exists. V3/V4 cannot be called complete based on local mocks. Avoid expanding the product catalog and replacing allocation/orchestration in the same first change.

## Validation matrix and required receipts

All rows below are **required future evidence**, not tests reported as passed by this review.

| Gate | Cases / evidence | Acceptance |
|---|---|---|
| Local contract compatibility | Both workloads, all target selections, old manifests, unknown schema, generic/legacy routes, infrastructure-only fake adapter. | Stable current behavior; unsupported inputs fail; no untrusted path dispatch. |
| Discovery completeness | Successful empty, 403, missing provider, truncated/paged results, inaccessible external scope, stale/future times, repeated IDs, concurrent topology changes. | Unknown coverage never authorizes create/allocation; complete empty retains permitted create behavior. |
| Report fidelity | Observed/proposed/inferred/runtime edges, product-only summaries, hostile labels/tags, sensitive values, large graphs and no-model mode. | No invented relationships; safe output; decisions identical without AI; readable downloaded/static diagrams. |
| ADO compile/admission | Actual server validation for every generated menu, approved/rejected resource selection, missing pool/connection, disabled and preview-only expansion. | No unauthorized private-resource dependency in preview-only; no dynamic dropdown promise; wrong/failed discovery rejected even if selectable in the resource picker. |
| Approval ordering | Record timestamps for plan publication, checks, stage start and actual write; reject/cancel/timeout/retry each boundary. | The reviewed final plan exists before its apply-stage check; no later plan substitution; rejected checks produce no dependent writes. |
| Least privilege | Read collector, preview writer, allocator, network apply, publisher and workload apply tested under intended identities. | Each succeeds only for its operation/scope; unauthorized cross-scope/write/secret operations denied. Role assignment capabilities reviewed separately. |
| Allocation concurrency | Two products request same domain, duplicate request, stale fencing token, expiry during review, partial network apply and agent loss. | No conflicting assignment, duplicate apply or occupied-space release; recovery uses persisted receipts. |
| Native Azure preview/apply | Pinned CLI/Bicep/API support, unknown What-If values, Delete/Detach/security changes, template/package tampering and real pre-apply drift. | Unsupported or disallowed changes block; same approved content applied; preview-result cleanup or actionable cleanup receipt retained. |
| Private acceptance | Required DNS/ports/TLS/identity and application smoke from actual private runner/workload paths; forbidden source/identity probes. | Configuration diagrams alone cannot pass. Both product runtime contracts and negative-path checks must succeed. |
| Operations / cost | Partial failure after connectivity succeeds, retry, retention, restore of broker state, scope-limited cleanup, pricing expiry/exclusions. | No automatic destructive rollback; owned resources and allocations remain accountable; no “free resources” or exact bill claim. |

For every gate store source revision, tool/API versions, fixture or cloud scope, run IDs, timestamps, expected/actual outcomes, failures/skips, reviewer role and artifact links. Redacted diagnostics must remain useful; no secrets in evidence. Live results cannot be inferred from simulated tests. Retain evidence for the support lifetime required by platform policy and ensure referenced ADO artifacts remain accessible during approvals.

## External decisions and promotion gates

| Required decision | Accountable role | Needed before |
|---|---|---|
| Approved IPAM backend/API and transaction model; routing domains and imported address authority | Connectivity owner | V3 live spike completion / all allocation |
| Pilot region/profile, capacity rules, DNS/egress/inspection and shared ownership | Connectivity and workload owners | V4 pilot |
| Read/preview/allocator/publisher/apply identities, protected templates and checks; private runner bootstrap | Platform/security owners | Corresponding live operations; V4 activation |
| Broker hosting, durable store, issuer/fencing, lease/retention limits and recovery objectives | Platform operations owner | V3 acceptance / V4 activation |
| Budget, price sources, artifact retention and operational support | Product/platform owners | Live pilot and product admission |
| Model/data boundary and evaluation budget | Security/product owners | V6 AI integration only |

These are role assignments for planning, not recorded approvals. Use explicit per-target/profile enablement with default off. To stop rollout, disable new requests and reconcile in-flight work; do not delete live allocations or revert an owned-resource schema blindly. Schema migration rollback requires tested readers and retained receipts. End the pilot with a documented promote/hold decision rather than automatically enabling every environment.

## Review closeout

The immediate implementation boundary is **V0/V1: contracts and workload-specific reports from saved discovery evidence**. The plan now specifies stage timing, metadata writes, compatibility, source authority, failure recovery and measurable exit criteria. Live ADO approval behavior, allocation semantics, least-privilege Azure operations and workload readiness remain unverified until the corresponding pilots execute.

Review verification on 26 September 2026: `./scripts/Update-ServiceCatalog.ps1 -Check` passed for eight unique target selections; `node tests/infrastructure/verify.mjs` passed 46 existing contracts across 20 YAML files. The latter required local temporary-artifact write permission after an initial sandbox `EPERM`; it made no Azure calls or ADO server expansion. These are current-baseline checks, not tests of the unimplemented design or a new full application-suite result.
