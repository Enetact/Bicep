# Implementation and verification status

**Portal lifecycle — 5 October 2026:** `Manage-Portal.ps1` adds read-only Status and governed Start/Stop/Restart orchestration. Start/Restart include the actual portal Release build and startup readiness checks. The initial 14-scenario fixture missed a keyed-catalog count bug in final readiness; the correction passes 16 scenarios with the production catalog shape. Live bootstrap returns nine workloads. Tool-context process metadata is unavailable, so full ownership verification remains with the launching user. No further restart or provider reauthentication was performed for the correction. See [commands and authentication effects](local-portal.md#combined-lifecycle-command).

**5 October 2026 — discovery and named Preview integration:** all nine workload compositions now accept typed standard-v2 name maps. Fresh discovery generates observed identities and matching source-draft candidates; the dedicated Deploy menus can freeze a reviewed instance into **Preview only**. Names shown for Azure changes come from returned What-If resource IDs. Existing evidence is never edited. All targets remain disabled, the source draft registry remains empty, and named Deploy is explicitly rejected. Local verification is recorded below; live ADO/Azure Preview and Codex inference acceptance remain outstanding. See [the guide](resource-naming.md) and [validation log](validation.md).

**Authenticated discovery acceptance — 4 October 2026:** Blob copy and Event flow Discover succeeded on merged `main` in ADO runs 25/26. Private GitHub source checks and live tag discovery passed. Portal DNS collections remain Partial (HTTP 400), the discovery PowerShell fixture fails on a missing helper, and 19 pipeline definitions remain unregistered. Codex is disconnected in the tested browser session. This establishes bounded read-only discovery evidence, not deployment acceptance. See [the test README and issue log](testing/README.md).

**Follow-up:** Both raw discovery artifacts now pass integrity and live ADO provenance checks. Portal handoff accepts matching dev evidence and rejects QA mismatch; real-artifact analysis and one-subscription management-group traversal were exercised. Additional artifact-host compatibility and stale-analysis-display issues are recorded. Deploy execution and live AI acceptance remain open.

**AKS/Helm increment — 4 October 2026:** AKS foundation source, catalog, portal topology and dedicated menus are implemented. The separate Helm pipeline produces Kubernetes render/diff artifacts and guarded deployment receipts. Targets remain disabled; no live cluster, Helm rollout or ADO acceptance is established. Helm launches from native ADO; no separate portal Kubernetes diff screen or agent executor was added. See [the implementation and onboarding guide](../workloads/private-aks/README.md).

[Wiki home](README.md) · [Overview](platform-overview.md) · [Workloads](workload-catalog.md) · [Validation history](validation.md)

**Naming/discovery source review: 5 October 2026; broader source review retained from 4 October.** This is the current capability summary. The [validation log](validation.md) retains dated test runs and historical milestones; [validation results](validation-results.json) retains their machine-readable records. The data-platform increment adds local validation; no new cloud acceptance is established.

## Current inventory

| Item | Checked-in state |
|---|---|
| Workloads | Nine types and named instances. |
| Environment targets | 36 registered; **zero enabled**. |
| Pipeline entry points | 25 manual roots, including 18 dedicated workload menus. |
| Skill definitions | 42 Microsoft Azure skills and ten project skills. |
| Agent workflows | Nine explicitly invoked reviews/drafts. |
| Recovery policies | Seventeen policies; execution unavailable. |
| Tag Apply / AVNM allocation | Both profiles disabled. |
| Helm application profiles | Four configured examples; zero enabled. |

## Capability matrix

| Capability | Implementation | Verification boundary / remaining work |
|---|---|---|
| Local portal and Windows packages | Implemented | Local backend/HTTP/browser evidence exists. Packages predate some source increments; rebuild to include current code. Native x64 hardware acceptance is outstanding. |
| Microsoft authentication and ADO integration | Implemented: local Azure CLI button or registered-app browser flow | Separate audience/session controls, isolated CLI cache, cancellation and renewal tests. User consent, tenant policy and authenticated live Azure/ADO workflows need acceptance. CLI mode needs no custom registration. |
| ADO definition registration | Implemented | Reviewed create/reuse/conflict logic for all roots. Local contracts do not establish live registration or permission setup. |
| GitHub CLI connection and private source | Implemented; local sign-in accepted | User completed browser consent; portal verified Enetact identity and an authenticated CLI read confirmed private repository access. Registration routing is locally tested; end-to-end private-source ADO registration remains unverified. [Guide](local-portal.md#github-browser-sign-in). |
| Nine workload menus/compositions | Implemented | Local YAML routing, Bicep and product checks recorded. Shared dependencies, complete pricing and per-target Azure acceptance remain required. |
| Private AKS / Helm | Implemented; targets disabled | 34 mocked AKS checks, real Helm 4.3.0 rendering and 22 application contracts pass. Cluster/namespace onboarding and live installation/upgrade/failure acceptance remain outstanding. |
| Private Data Platform | Implemented | Databricks Premium/ADF v2/ADLS composition, private connectivity and managed identities compile; 41 mocked domain checks pass. Manual ADF staging/optional Job launch and a parameterized Databricks bundle are also implemented. Endpoint approvals, workspace/UC onboarding, native bundle validation and a controlled data-flow run remain mandatory. |
| Discovery and saved-run handoff | Implemented | Local coverage/failure/provenance tests and supplied ADO evidence. Discovery does not establish deploy permissions. |
| Event flow prerequisite resolution | Implemented | Create / Reuse / Manage / Blocked rules and local recheck tests; actual Azure creation/reuse remains unaccepted. |
| Preview stage and diagrams | Implemented | Azure stack validation/What-If path and guarded artifact reader; local mocked contracts pass. Successful live What-If is not established by the reviewed records. |
| Template Specs and Deployment Stacks | Implemented | Frozen bundles, ownership, drift checks and publication/apply paths tested locally. Real publication, deny permissions, private agents and workload readiness remain required. |
| Network discovery and agent Mermaid | Implemented | Broader visible scopes, deterministic checks, evidence/grammar validation and image-only rendering tested locally. Effective connectivity and complete tenant visibility are not inferred. |
| AVNM IPAM/network delivery | Implemented, profile disabled | Bounded retained-reservation/create-only flow and local contracts. Live pool/provider acceptance, general lifecycle and automatic workload binding remain open. |
| Codex/AHP/MCP workflows | Implemented; live sign-in/catalog verified | Rebuilt portal's ChatGPT authentication and GPT-6.1 Sol/Ultra catalog verified on Codex 0.160.0 (4 October). Completed inference and cloud-backed review acceptance remain outstanding. AHP is coordination-only, not a general host. |
| DNS/drift operations pilot | Implemented; locally verified | Dynamic authenticated subscription picker, bounded ARM evidence, deterministic rules/matrices/graph, local-reference comparisons, two structured Codex handlers and editable proposal previews. Live domain acceptance, accepted baselines and cloud execution are outstanding. [Guide](operations-workflows.md). |
| Tag governance | Implemented, Apply disabled | Shared collector/rules, structured advice, drafts and protected pipeline source tested locally. Ownership/type qualification and live change verification remain required. |
| AI-assisted Bicep drafts | Implemented | Typed module bindings and emitted source compiled locally. Drafts remain unqualified; production parameter resolution and admission are incomplete. |
| Recovery rules and policy UI | Implemented | Offline assessor and policy annotations tested; caller assertions remain unverified and never authorize execution. |
| Before/What-If/after IaC comparison | Implemented; locally verified | Pipeline Microsoft Azure MCP + nested ARM/child probes, profile coverage gates, before-state drift fingerprint, hash-bound portal reader and Codex review. See [nested coverage](nested-iac-evidence.md) and [workflow](agentic-iac.md). No live acceptance claimed. |
| Durable retained-release store and restore execution | Planned | ADO artifact retention is used for the new phase history. No universal final-state proof, backup or protected restore executor. |
| Blob copy local runtime | Implemented | Recorded real Functions/Azurite transfer and recovery evidence. Not every clean-machine/architecture installation path is qualified; Azure runtime acceptance remains outstanding. |
| Event flow and starter application runtimes | Implemented | Source/package/compile contracts recorded. Hosted triggers, delivery, private access, identity propagation and product smoke need Azure acceptance. |
| Cost estimates | Implemented with gaps | Dated references, freshness rules and exclusions exist. Newer products explicitly lack complete estimates; no live billing or enforced budget integration. |

## How to read the evidence

**Implemented** means the source path exists and has been traced. **Locally verified** means recorded checks passed, often with mocked Azure responses. **Observed in ADO** means supplied logs establish only the steps reached. **Azure accepted** requires retained real Preview/apply/readiness evidence. **Planned** is not a shipped feature or deployment authorization.

Supplied Preview run 23 verified discovery run 21, then stopped at onboarding settings before Azure What-If. Later local settings/tests do not prove that a subsequent Azure run succeeded. No full successful Azure workload deployment is established by the reviewed evidence.

The latest recovery increment records 54 rule checks, 22 mocked Preview checks, 186 backend passes with one optional skip, four Node recovery-view checks, 133 pipeline checks and 34 local HTTP checks. These are **historical recorded results**, not a new test run performed by this documentation review. See [recovery validation](validation.md#recovery-policy-implementation-27-september-2026).

Earlier full-project runs, architecture-specific package checks, Bicep draft compilation and real emulator runs remain in [validation history](validation.md). Read the record for the capability and revision being assessed; a later targeted pass does not replace a full-suite run. Artifacts may live locally or in ADO rather than in the public repository.

## Acceptance before enabling a target

1. Review product prerequisites, ownership, costs and authorization. Generated dev labels are not validated finance-system values; Blob copy needs its actual destination/topology.
2. Confirm identities, service connections, providers, permissions and quotas. Run matching Discover on `main` after the reviewed configuration is in place.
3. Run **Preview only** and retain successful Azure validation/What-If. Resolve unknown or failed evidence as blockers.
4. Configure private-agent connectivity, protected environment approvals and exclusive locks. Enable only the reviewed target, regenerate catalog/manifest and refresh discovery.
5. Run **Preview and deploy**, retain the product result and independently test private access, alerts and runtime behavior. Qualify other environments separately.

Recovery execution, immutable application promotion and wider network lifecycle are separate roadmap work. Existing policies or dev exception records do not grant production approval. Start with [workload onboarding](workload-onboarding.md), [security/RBAC](security-and-rbac.md) and [the expansion plan](self-service-expansion-plan.md).
