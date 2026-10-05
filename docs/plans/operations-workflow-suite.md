# Shared operations and drift workflow suite

**Status: implementation started, 4 October 2026.** The first O0–O1 source increment adds shared typed contracts/rules, dynamic Azure subscription selection, Private DNS collection/analysis, a DNS property comparison pilot, two AHP/Codex reviews and editable analysis proposals in the rebuilt portal. It does not complete all O0/O1 acceptance gates or implement O2–O6. No cloud execution, new pipeline roots, target enablement or accepted deployment baseline store is enabled. See [the implemented guide and limits](../operations-workflows.md).

## Delivery ledger

| Phase | Current delivery | Still required |
|---|---|---|
| O0 | Shared operations library/CLI; versioned typed evidence, assessment, advice, proposal and preview; scope/session/digest validation; browser ownership and explicit source authority; dynamic subscription picker using the authenticated tenant and Azure responses. | Qualified durable baseline storage, broader operation-specific contracts/receipts and live platform onboarding. |
| O1 | Fixed DNS ARM reader; bounded records/link/endpoint/resolver collection; candidate/conflict/unknown findings; observed graph/matrices; explicit desired TTL/registration comparisons; two pinned skills using the updated AHP/Codex/MCP runtime; local proposal previews/exports. | Live Azure and model acceptance; accepted-baseline promotion; additional record/property profiles, endpoint-managed ownership attribution and effective consumer/forwarding verification. Local references are not accepted deployment baselines. |
| O2–O6 | Planned; no additional handlers or execution capability advertised. | Implement and qualify each subsequent phase below. |

User clarification: subscription selection must use values returned by Azure authentication/discovery, not a hardcoded developer subscription or the workload catalog. The new operations adapter enforces this. Existing deployment catalog targets are a separate, unchanged governance surface.

**Local authentication increment (4 October 2026):** Connections now offers an Azure CLI browser sign-in button without a custom registration. The shared token provider supplies Azure/ADO audiences; network and tagging scope selection also use the authenticated tenant. Codex/AHP remains separate and explicitly invoked. Local isolation/cancellation tests pass; completed tenant login and authenticated O1 collection remain manual acceptance gates. See [authentication setup](../local-portal.md#configure-microsoft-browser-sign-in).

[Wiki](../README.md) · [Workflow standard](../agent-workflow-standard.md) · [Blueprint](../templates/agent-workflow-blueprint.md) · [Expansion roadmap](../self-service-expansion-plan.md)

## Scope and delivery decision

Implement the same experience for **Private DNS analysis/consolidation, identity/access review, private connectivity diagnostics, Databricks/ADF release promotion, AKS maintenance planning, overall pipeline analysis and drift detection**:

**Select scope → collect evidence → deterministic checks → optional Codex review → editable proposal → operation-specific Preview → protected ADO execution → verification.**

Analysis-only runs may stop at findings or an exported proposal. Active tests and cloud changes are separate, explicitly selected operations. A successful agent review never makes a proposal deployable. Each stage shows its own status; partial visibility is not an empty estate and a failed verification is not success.

Reuse **GPT-6.1 Sol / Ultra / Standard**, `AgentPolicy`, `AgentHostChannel`, `CodexAgentRuntime` and `AgentMcpBridge`. AHP coordinates the browser session; the native Codex runtime performs inference. Deterministic collectors use authorized Azure/ADO/service APIs and qualified Microsoft Azure MCP tools. The agent reads a frozen, sanitized evidence projection through `platform_evidence` and `platform_skill`; it does not receive arbitrary Azure, shell or write tools.

Cost analysis and new workload products are outside this increment. Cost impact remains a required field on proposals where applicable. No recurring scans or notifications are enabled merely by implementing these capabilities.

## Planning baseline and actual reuse

| Existing implementation | Reuse | Missing work for this suite |
|---|---|---|
| `WorkflowReviews.Definitions`: seven typed review/draft workflows | Registration, readiness, common agent execution and evidence/skill reads | Seven new domain adapters and validated recommendation contracts; descriptors alone are insufficient. |
| `NetworkDiscovery`, `NetworkAssessment`, `EvidenceDiagram` | Scope selection, collection outcomes, configured topology and restricted rendering | Detailed DNS records/ownership, resolver paths and diagnostics from the actual source network. |
| `scripts/iac/`, `IacAnalysis.cs`, dedicated Preview/Deploy integration | Run-bound before/What-If/after evidence, hashes and UI phase views | Durable accepted baselines, property-complete domain profiles and three-way drift reconciliation. Existing snapshots are bounded projections, not restore templates. |
| Tagging shared core and `WorkflowReviews.Tags` | Reference pattern for deterministic rules, validated advice and explicit draft selection | Domain-specific operation types; do not reuse tag key/value semantics for DNS, access or maintenance. |
| `PipelineRegistration`, `AdoGateway`, generated menus and shared templates | Exact repository/definition/artifact bindings and controlled queueing | Expanded YAML analysis, protected-resource checks and operational release adapters. |
| Data-platform Bicep, Python source, bundle template and recipe exporter | Source-controlled analytics assets and deployment output bindings | Qualified workspace publication, release promotion, trigger coordination and ownership transfer where necessary. |
| AKS foundation and separate Helm delivery | Cluster/profile bindings, private-agent boundary, namespace-scoped delivery evidence | Maintenance eligibility, supported upgrade planning, drain/capacity checks and maintenance execution. |
| Recovery policies and change guards | Exclusions, owner review and existing rejection rules | Per-operation recovery procedures. No generic rollback or automatic removal of existing guards. |

The current pipeline Azure MCP collector exposes only pinned `group_list` and `group_resource_list` tools, supplemented by scoped ARM GETs. It is not a DNS, Graph, ADO or Databricks collector. New integrations must qualify actual tool availability, input/output schemas and pagination; never invent Microsoft MCP tool names.

## Shared contracts to implement first

Use versioned, immutable artifacts and code-registered adapters. The first operations-specific records now exist in `src/SelfService.Operations`; the complete cross-domain contracts below remain the target design, not an existing universal engine.

| Contract | Required fields and authority |
|---|---|
| Workflow descriptor | ID/version, supported scopes, collector/rule/skill versions, available operations, per-operation connection requirements, profile, limits and implementation state. No executable paths from user input. |
| Scope selection | Tenant, selected subscription IDs or resolved management-group membership, exact resource targets, caller authorization, allowed cross-subscription dependencies and resolution time. Tenant scope means visible, authorized coverage. |
| Evidence envelope | Snapshot ID/digest, collection start/end, authenticated collector identity, tool/API versions, requested/observed scopes, per-collection pagination/completeness, denied/unsupported/excluded scopes and domain payload. |
| Assessment | Rule/version, severity, resource IDs, evidence references, Known/Conflict/Unknown/NotApplicable state, coverage needed and action eligibility. |
| Agent review | Workflow/assessment/projection digests, bounded recommendations, domain-typed operations, evidence references, rationale and uncertainty; host-owned `advisoryOnly`. Reject invented IDs or unsupported operations. |
| Editable proposal | ID/revision, selected recommendations and user edits, source owner, exact targets, desired changes, prerequisites, expected effects, rollback/compensation limits and validation errors. Editing invalidates previous previews. |
| Preview | Proposal/source/profile/tool hashes, effective inputs, exact operations, expected before-state versions, affected-resource/dependency scope, unknowns, cost/disruption implications and expiry. Preview type must identify ARM, DNS migration, bundle, maintenance or pipeline analysis. |
| Execution and verification | Bound ADO definition/run/artifact/stage, approval/check evidence where readable, lock identity, operation ID, per-step result, direct read-back and active-test evidence, preserved-state results and unknown outcomes. |

Share request validation, evidence storage/references, projection limits, cancellation, identity separation, safe download/rendering and receipt handling. Domain libraries own semantic checks. A generic JSON object must not bypass typed resource/scope checks.

Evidence storage must support immutable versions, authorization and configured retention. Browser-owned local evidence supports local review; accepted deployment baselines need platform-controlled retention beyond expiring ADO artifacts. Qualify an approved private evidence store and access/retention settings before claiming durable drift coverage. Store configuration projections and hashes, not tokens, secret values or unrestricted logs.

Preserve current runtime budgets until changed by a tested profile. Large inventories must be paginated/chunked and disclose truncation. Do not send the entire tenant, directory, YAML repository or raw logs to Codex. Each agent run discloses selected evidence and requires explicit Run.

## 1. Private DNS analysis and consolidation

**Proposed ID:** `private-dns`. **Skill:** `platform-private-dns-review` plus the existing visualizer for supported diagrams. **First release:** read-only analysis, editable migration proposals and exports. Cloud consolidation follows its own qualification gate.

### Discovery and deterministic checks

Collect selected zones and record sets, ETags, VNet links and registration settings, private endpoints and zone groups, endpoint FQDN/IP metadata, VNet DNS settings, private resolvers/endpoints, forwarding rulesets/links and approved source-network context. Preserve whether records are manually owned, auto-registered or endpoint-managed. Use provider-specific record pagination; a count alone is not a record inventory.

Representative rules:

- `DNS-001`: same-name zones across scopes are consolidation candidates, not proven duplicates. Compare service, consumer topology, ownership and intended isolation.
- `DNS-002`: conflicting name/type/record values or TTLs require an explicit resolution; no arbitrary winner.
- `DNS-003`: missing/stale endpoint-zone-group associations, missing expected records and known unmanaged records need separate findings.
- `DNS-004`: missing consumer links, registration conflicts and unsupported resolver/forwarding paths block migration eligibility.
- `DNS-005`: incomplete records, unreadable consumer scopes or unknown record ownership prohibit a claim that a zone is unused or safe to retire.

Portal views: zone/service/subscription matrix; record-conflict table; consumer and resolution-path diagram; candidate destination zone; preserve/create/rebind/manual-review operation list. Codex explains candidates and rejection reasons, but deterministic rules select which edits are even eligible.

### Proposal, Preview and execution

The user selects an approved destination and migration profile. Preview contains the exact source/target IDs, records and managed-record exclusions, consumer links, source changes, zone-group changes, resolver dependencies, TTL/disruption considerations and before-state versions. Existing central zones remain platform-owned; do not adopt them into an application stack.

Implement a resource-specific migration adapter: checkpoint → prepare destination → validate records → approved consumer/zone-group cutover → source-network DNS/application checks → stabilization review. Where Azure prevents coexistence of same-name zone links, report the required cutover gap; do not promise make-before-break or zero downtime. Do not edit provider-managed DNS records as ordinary user-owned records. Retiring the old zone is a separate approval after dependency and retention evidence, never an automatic consequence of finding duplicate names.

Verification must cover the approved source networks, expected private answers/CNAME chains, preserved records and affected application paths. Store the exact previous bindings for a qualified reversal; compensation must stop if current ETags/ownership no longer match. Rehearse in a disposable topology before any shared-zone migration.

Microsoft describes shared-zone hub/spoke patterns and also documents private-endpoint record lifecycle hazards. Consolidation must therefore be topology- and service-specific. [Private Endpoint DNS integration](https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-dns-integration), [VNet links](https://learn.microsoft.com/en-us/azure/dns/private-dns-virtual-network-links).

## 2. Identity and access review

**Proposed ID:** `identity-access`. **Skill:** `platform-identity-review`. Select resource/workload, principal and explicitly authorized subscription scopes; directory enrichment is optional and separately consented.

Collect role definitions/assignments and inherited scopes, deny assignments, role conditions, managed identities and federation metadata. Collect group membership and PIM eligibility/activation only when the authorized directory adapter can read them. Never collect credentials, tokens, secret values or private keys. Missing directory permissions produce Unknown, not an orphaned principal finding.

Deterministic checks identify broad scope/actions, grants outside the approved workload profile, missing expected assignments, federation mismatches and overlapping grants. Distinguish management-plane actions, data-plane actions, conditional grants, active versus eligible assignments and unresolved group paths. Do not claim full effective access from a role name or last-use date.

Show a principal → scope → role/action matrix and optional access-path graph. Codex may explain evidence and propose least-privilege alternatives from approved role sets. The editable proposal contains exact principal object ID/type, scope, role/condition, owner and reason. A scoped grant/removal preview includes inherited effects, dependency impact and self-lockout risks.

Initial execution supports only qualified workload-owned grants through their authoritative source. Privileged role administration, PIM changes, directory writes and broad removals remain separate platform operations. Apply uses a dedicated authorized identity; the reviewer must not grant itself permissions. Verify exact assignments and, where approved, a targeted positive/negative access test using the intended identity.

Azure authorization includes inheritance, deny assignments and conditions; incomplete evidence must stay explicit. [Azure RBAC evaluation](https://learn.microsoft.com/en-us/azure/role-based-access-control/overview).

## 3. Private connectivity diagnostics

**Proposed ID:** `private-connectivity`. **Skill:** `platform-private-connectivity`. Select a registered source workload/probe runner and an approved destination resource/FQDN/protocol/port, not arbitrary shell commands or target ranges.

Collect configured DNS, private endpoint approval, NSG/route references, supported effective routes/security rules, peering/resolver and firewall evidence. Active DNS/TCP/TLS/application probes require their own reviewed diagnostic request and run from the selected source network. A browser or unrelated hosted agent is not a substitute. Use Network Watcher only for supported source/resource combinations; qualify an approved private-runner alternative for others.

Rules distinguish configured path, observed resolution, connection success, certificate/hostname validation and application authorization. Limit egress targets, requests, duration and payload; prohibit port scans, metadata endpoints, secret retrieval and model-authored KQL/shell. Display timed evidence and hypotheses, not a guaranteed end-to-end causal diagnosis.

Preview for diagnostics is the exact probe plan and scope. Protected ADO executes approved active tests; infrastructure repair is a separate editable DNS/network/source proposal requiring its own change preview. Verification compares the same source/destination before and after, with identity and timestamp attached. [Network Watcher connection troubleshooting](https://learn.microsoft.com/en-us/azure/network-watcher/connection-troubleshoot-overview).

## 4. Databricks/ADF release promotion

**Proposed ID:** `data-release`. **Skill:** `platform-data-release-review`. Select an immutable source release, source/target environment and approved deployment bindings. Never regenerate code during promotion.

Reuse `scripts/Export-DataPlatformRecipes.ps1`, the versioned Databricks Python/bundle sources and ADF module. Inventory workspace job/bundle identity, reviewed compute policy/runtime dependencies, ADF pipelines/datasets/linked services, integration runtimes, endpoint approvals, run/trigger state and required identity grants. Workspace and data-access scopes are separate from ARM inventory.

**Ownership gate:** the current foundation module declares ADF child resources. Before a dedicated release adapter manages any of those same IDs, choose and test one source owner. Either release their changes through the existing owning composition, or perform an explicitly reviewed ownership migration. Do not let foundation redeployment overwrite promoted definitions; never silently detach resources to achieve this.

Deterministic qualification checks source/asset hashes, unit tests, bundle target bindings, runtime/compute-policy compatibility, approved run-as identity, parameter completeness, workspace authorization and trigger treatment. Preview shows code/config changes, job/task graph, environment mappings, ADF resource changes and any trigger pause/resume sequence. Databricks bundle validation/diff and ADF ARM previews are distinct outputs; neither proves data-processing correctness.

Protected execution publishes the exact release using qualified service identity/federation, preserves unrelated assets and records partial outcomes across systems. Deploying a job is separate from running it. Resume only triggers whose prior state was captured and whose restoration is authorized. Verify deployed hashes/configuration and explicitly selected synthetic acceptance runs. Checkpoints, duplicate-processing risk, production data changes and schema evolution require recipe-specific rules; redeploying old code does not undo data writes.

[Databricks CI/CD and bundles](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/ci-cd/) describes source-controlled assets and ADO integration. Retain the current starter recipes as source; this plan adds release orchestration rather than replacing them with generated artifacts.

## 5. AKS maintenance planning

**Proposed ID:** `aks-maintenance`. **Skill:** `platform-aks-maintenance`. Select a registered cluster and operation: assess versions, plan node-image maintenance, plan a supported Kubernetes upgrade, or inspect maintenance configuration.

Collect actual control-plane/node-pool versions, supported upgrade paths, node images, maintenance/auto-upgrade settings, extensions/add-ons, pool capacity/quota, pod disruption budgets, workloads and storage/drain constraints. Kubernetes evidence requires separate private API access. Missing cluster/data-plane visibility blocks execution eligibility, not the read-only report.

Rules evaluate version compatibility, deprecated APIs using qualified tooling, available surge capacity, disruption/availability constraints and workload ownership. Show an ordered maintenance plan, affected pools/workloads, estimated disruption assumptions and required operator decisions. Codex explains the evidence; it does not invent supported versions or claim all workloads tolerate eviction.

Keep the current generic cluster-change guard. A separately qualified maintenance adapter performs only approved operations with a cluster lock, maintenance window, prechecks, staged progression and stop conditions. Verify node/pod health and product-specific service tests after each step. Kubernetes downgrade is not the recovery strategy; define halt/escalation, replacement/redeployment and data recovery options separately. Maintenance windows are not absolute guarantees. [AKS planned maintenance](https://learn.microsoft.com/en-us/azure/aks/planned-maintenance).

## 6. Overall pipeline analysis

**Proposed ID:** `pipeline-analysis`. **Skill:** `platform-pipeline-analysis`. Select the registered GitHub repository/revision and ADO project/pipeline, with optional exact run IDs. Support local source-only assessment when ADO is unavailable.

Collect parsed source templates, parameter/target matrix, generated-menu consistency, exact ADO definition/repository/branch bindings, expanded YAML, resource authorization/check metadata when readable, agent capabilities and selected run/artifact outcomes. Treat scripts, YAML comments and logs as untrusted content, not instructions. Never execute scripts to analyze them or retrieve secret variable values/service-connection credentials.

Rules cover missing pools/connections, incorrect discovery producer/run/artifact binding, stale inputs, unsafe parameter interpolation, source/template mismatches, unreachable private agents, missing approval/lock evidence, promotion provenance, inconsistent ownership and incomplete failure reporting. A graph explains stage dependencies, identities, artifacts and execution boundaries. Report which parameter combinations were expanded; a sampled matrix is not exhaustive proof.

Use ADO's Preview API with `previewRun: true` for server template expansion; it returns final YAML without a normal execution run. Expansion cannot prove Azure permissions, connectivity or successful deployment. The user explicitly requests this operation under ADO authorization. [ADO pipeline Preview API](https://learn.microsoft.com/en-us/rest/api/azure/devops/pipelines/preview/preview?view=azure-devops-rest-7.1).

Codex produces evidence-linked findings and a reviewable source proposal. Pipeline edits follow the owning Git repository and PR process; generated menus change through their generator. Missing ADO settings produce an administrator setup proposal. Neither route directly rewrites arbitrary pipeline definitions or grants all-pipeline access. Verify source checks and exact server expansion after an approved update, then retain real run evidence separately.

## 7. Modern MCP-backed drift detection

**Proposed ID:** `drift`. **Skill:** `platform-drift-review`. Extend the shared IaC component, rather than build a second inventory engine or interpret generated `existing` Bicep as desired configuration.

### Three inputs and classifications

1. **B — accepted baseline:** last verified deployment's immutable source/template, effective parameters, artifact identity, ownership and observed after-state. A partial/failed deployment cannot silently become the baseline.
2. **O — observed state:** fresh scoped collection from qualified MCP/ARM/service collectors, including explicit property and pagination coverage.
3. **D — desired state:** current reviewed source and resolved parameters plus operation-specific preview. Unknown expressions/defaults remain unknown until supported evaluation.

For each supported property/resource, normalize resource IDs, unordered sets and service defaults with versioned rules. Ignore volatile fields only through a reviewed allowlist and show exclusions. Compare B↔O for deviation since acceptance, B↔D for intended changes and O↔D for the proposed reconciliation.

Classify InSync, DesiredChange, ObservedDeviation, Conflict, MissingManagedResource, UnmanagedCandidate, ExpectedProviderChange, Unknown and Unsupported. If B is missing, offer observed-versus-desired assessment without calling it proven historical drift. If deletion cannot be established with complete scoped evidence/direct reads, report Unknown. Auto-scaling/patching defaults need explicit ownership-aware normalization, not broad suppression.

### Collection and action boundaries

Use the existing pinned Azure MCP collector as an initial transport, then add explicit resource/property profiles for DNS, identity, ADF/Databricks and AKS. Qualify supported Microsoft MCP tools at startup; use fixed ARM/Graph/ADO/Databricks/Kubernetes adapters when MCP lacks the required data. Record which tool provided each observation. Never dynamically choose endpoints or tools from model output, and never silently switch credentials after denial.

Start with manual scans. A future ADO schedule must be deliberately configured per profile with retention, budgets and notifications; this plan does not create one. Multi-subscription scans resolve and freeze the authorized subscription set, preserving denied and unobserved coverage. Evidence hashes prove consistency, not independent Azure authenticity; trust also requires a qualified producer/run/identity chain.

The portal displays a property matrix and graph with separate observed, accepted and proposed layers. Recommendations are: investigate; create a reviewed source amendment; reapply qualified desired configuration; or open a maintenance/migration proposal. Adoption, deletion and automatic remediation are excluded from the first executor. Recollect and compare semantic preconditions immediately before Apply under the target lock; changed material state invalidates approval and requires a new Preview.

Verification uses fresh read-back plus relevant runtime probes. Retain the previous baseline and failed-attempt evidence; publish a new accepted baseline only after the required verification gates pass. Preserve unknown outcomes for reconciliation instead of replaying writes blindly. See [existing IaC coverage](../agentic-iac.md) and [state/recovery design](deployment-state-and-recovery.md).

## Proposed source layout

These locations are the target layout. The operations library/CLI, portal `Operations/` adapters, two skills and tests now exist; future domain directories, schemas, profile configuration and pipeline templates should be added with their tested consumers rather than empty placeholders:

```text
src/SelfService.Operations/             Shared typed contracts and deterministic domain rules
  Contracts/                          Scope, evidence, assessment, proposal, preview, receipts
  PrivateDns/ Identity/ Connectivity/  Domain rules and semantic validation
  DataRelease/ AksMaintenance/ Pipelines/ Drift/
src/SelfService.Portal/Operations/     Browser APIs, orchestration and scoped readers
src/SelfService.Portal/wwwroot/        Domain views sharing workflow state/receipt components
scripts/operations/                   Typed CLI entry/adapters for the same rules
scripts/iac/                          Existing Node MCP/ARM collector; extend via versioned contract
schemas/workflows/                    Versioned artifact/domain schemas
config/workflow-profiles/             Reviewed scope, permissions, limits and allowed operations
pipelines/templates/operations/       Shared collect/assess/preview/execute/verify templates
.agents/skills/platform-*/            Actual tested domain instructions, added with each adapter
tests/SelfService.Operations.Tests/   Deterministic contract/domain tests
tests/operations/                     Collector, pipeline and portal integration fixtures
```

Prefer one shared library consumed by portal and CLI for deterministic rules, with adapters for existing Node collectors. Preserve existing tagging/network/IaC contracts until compatibility tests prove migration. Do not move unrelated files merely to match this diagram. New Bicep leaves such as an existing-zone DNS link module follow `modules/<domain>/<name>/`; shared connectivity changes belong to platform-owned stacks, not application workload stacks.

Create thin manual pipeline roots only when an execution adapter exists, and register them through `Update-ServiceCatalog.ps1`/pipeline registration where applicable. Fixed domain templates supply executable steps; profile JSON cannot contain arbitrary scripts, tool names or repository URLs. No new registry is required.

## Phases, dependencies and completion gates

| Phase | Delivery | Required exit evidence |
|---|---|---|
| O0 — shared contracts and authority | Versioned workflow/evidence/proposal contracts, ownership matrix, typed adapter registration, UI readiness and evidence retention design; reconcile current parameter/source authority. | Existing seven workflows still work; malicious/stale/cross-session evidence rejected; no new action claims without handlers. Platform owner approves intended scope and operations. |
| O1 — DNS and drift analysis | Detailed DNS collectors/rules/visuals, accepted-baseline storage pilot, three-way drift for explicitly supported resource properties, AHP/Codex review and editable source/migration proposals. | Synthetic conflicts/partial coverage plus one read-only live scope; model recommendations reject invented targets; deletion/merge remains unavailable. |
| O2 — identity and private-path analysis | Scoped access matrix and deterministic rules; selected-source diagnostic proposal and protected bounded probes. DNS evidence feeds path analysis. | Denied directory access stays Unknown; unsupported source types blocked; real DNS/TCP/TLS results from approved private runner; no arbitrary scan or privilege grants. |
| O3 — pipeline analysis and release readiness | Source/expanded-YAML/artifact/check assessment, dependency graph and source/setup proposals; qualify read permissions and target locks. | Local and ADO expansion outcomes separated; unreadable checks are not assumed present; analysis creates no ordinary pipeline execution run. |
| O4 — Databricks/ADF promotion | Resolve ADF child ownership, qualified bundle/ADF release adapter, environment mapping, protected publication and optional synthetic run. | Same immutable release promoted across two test environments; unrelated assets/trigger states preserved; partial promotion and duplicate-run cases reconciled. |
| O5 — AKS maintenance | Live supported-path planner, compatibility/disruption/capacity checks, preview and separately protected maintenance adapter. | Disposable cluster rehearsal; PDB/capacity failure blocks; staged health and application checks; no downgrade promise. |
| O6 — qualified DNS/drift changes | DNS migration pilot and narrow drift reconciliation through authoritative source; accepted baseline promotion after verification. | Disposable DNS cutover/reversal, endpoint-managed record preservation, concurrent state-change rejection, independent approval and positive/negative private-path checks. |

O1 and O2 reuse O0; O3 can proceed after O0 and is a readiness dependency for operational execution in O4–O6. O4 and O5 are otherwise independent. DNS migration depends on O1, O2 and O3; generic high-risk guards remain until the specific adapter is qualified. Scope discovery/analysis can be implemented before the platform onboards cloud writes.

## Test and acceptance matrix

For every domain, test: no-model use; selected-scope authorization; denied versus empty collection; pagination/truncation/throttling; stale evidence; secret/untrusted text projection; invented IDs/operations; malformed model output; cancellation; tampered artifacts; concurrent change; unrelated-state preservation; partial execution; timeout with unknown outcome; and actual verification failure. Record local mocks, real local protocol tests and live provider acceptance separately.

Domain fixtures must include conflicting DNS records and managed zone groups; inherited/conditional/denied access; wrong-source DNS success; failed TLS versus authentication; ADF trigger and deployment ownership conflicts; Databricks run-as/bundle-target mismatch; AKS PDB/capacity/unsupported upgrades; pipeline matrix/producer/check mismatches; and drift caused by provider-managed state versus real configuration changes.

Before enabling an operation, configure the exact ADO service identity, private runner, protected environment/approval/exclusive lock, scope permissions and resource ownership. UI connection status is not this approval. ARM What-If cannot replace service-specific previews, and a diagram cannot establish permissions, effective connectivity or recovery capability.

## Remaining platform decisions

These do not block source planning: approved subscriptions/management groups and DNS owner; canonical zone/migration policy; authorized directory enrichment and role sets; diagnostic runner networks/targets; private baseline store/retention; Databricks workspace/federation/compute bindings; ADF definition owner; AKS maintenance authority/windows; and readable ADO project/check scopes. Collect them through typed onboarding profiles before live execution, not as free-form model guesses.

The first implementation milestone remains **O0 plus O1: shared contracts, Private DNS analysis and three-way drift review**. Its source pilot is now implemented with the gaps in the delivery ledger above; live acceptance and accepted-baseline coverage remain open. Subsequent capabilities stay Planned. Add a capability to the portal's executable catalog only when its collector, rules, review validation, UI and tests exist. Add its execution action only after its separate Preview/Apply/Verify adapter and acceptance are complete.
