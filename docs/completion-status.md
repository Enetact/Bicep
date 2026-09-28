# Implementation and verification status

[Wiki home](README.md) · [Overview](platform-overview.md) · [Workloads](workload-catalog.md) · [Validation history](validation.md)

**Source/configuration reviewed: 27 September 2026.** This is the current capability summary. The [validation log](validation.md) retains dated test runs and historical milestones; [validation results](validation-results.json) retains their machine-readable records. Documentation consolidation does not rerun those tests or establish new cloud acceptance.

## Current inventory

| Item | Checked-in state |
|---|---|
| Workloads | Seven types and named instances. |
| Environment targets | 28 registered; **zero enabled**. |
| Pipeline entry points | 20 manual roots, including 14 dedicated workload menus. |
| Skill definitions | 42 Microsoft Azure skills and eight project skills. |
| Agent workflows | Six explicitly invoked reviews/drafts. |
| Recovery policies | Fourteen policies; execution unavailable. |
| Tag Apply / AVNM allocation | Both profiles disabled. |

## Capability matrix

| Capability | Implementation | Verification boundary / remaining work |
|---|---|---|
| Local portal and Windows packages | Implemented | Local backend/HTTP/browser evidence exists. Packages predate some source increments; rebuild to include current code. Native x64 hardware acceptance is outstanding. |
| Microsoft browser authentication and ADO integration | Implemented | Separate audience/session controls and local tests; registration, consent and authenticated live workflows need acceptance. |
| ADO definition registration | Implemented | Reviewed create/reuse/conflict logic for all roots. Local contracts do not establish live registration or permission setup. |
| Seven workload menus/compositions | Implemented | Local YAML routing, Bicep and product checks recorded. Shared dependencies, complete pricing and per-target Azure acceptance remain required. |
| Discovery and saved-run handoff | Implemented | Local coverage/failure/provenance tests and supplied ADO evidence. Discovery does not establish deploy permissions. |
| Event flow prerequisite resolution | Implemented | Create / Reuse / Manage / Blocked rules and local recheck tests; actual Azure creation/reuse remains unaccepted. |
| Preview stage and diagrams | Implemented | Azure stack validation/What-If path and guarded artifact reader; local mocked contracts pass. Successful live What-If is not established by the reviewed records. |
| Template Specs and Deployment Stacks | Implemented | Frozen bundles, ownership, drift checks and publication/apply paths tested locally. Real publication, deny permissions, private agents and workload readiness remain required. |
| Network discovery and agent Mermaid | Implemented | Broader visible scopes, deterministic checks, evidence/grammar validation and image-only rendering tested locally. Effective connectivity and complete tenant visibility are not inferred. |
| AVNM IPAM/network delivery | Implemented, profile disabled | Bounded retained-reservation/create-only flow and local contracts. Live pool/provider acceptance, general lifecycle and automatic workload binding remain open. |
| Codex/AHP/MCP workflows | Implemented | Native startup/account/MCP handshake and local contracts recorded; live sign-in/inference acceptance outstanding. AHP is coordination-only, not a general host. |
| Tag governance | Implemented, Apply disabled | Shared collector/rules, structured advice, drafts and protected pipeline source tested locally. Ownership/type qualification and live change verification remain required. |
| AI-assisted Bicep drafts | Implemented | Typed module bindings and emitted source compiled locally. Drafts remain unqualified; production parameter resolution and admission are incomplete. |
| Recovery rules and policy UI | Implemented | Offline assessor and policy annotations tested; caller assertions remain unverified and never authorize execution. |
| Durable before/after timeline and restore execution | Planned | Completed portal runs reload Preview. Trusted retained-release reader, verified final diagram and protected restore executor are absent. |
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
