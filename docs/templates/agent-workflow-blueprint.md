# New self-service agent workflow blueprint

Copy this document into a capability design and replace every placeholder. This is a **design template, not an installed skill, runtime manifest or pipeline**. Follow the [workflow standard](../agent-workflow-standard.md); do not treat a completed template as implementation or Azure acceptance.

## Identity and readiness

| Field | Fill in |
|---|---|
| Capability / stable workflow ID | `<display name>` / `<id>` |
| Owner / version / status | `<team>` / `<version>` / Proposed, Implemented locally, Provider accepted |
| Workflow class | Analysis only, or Analysis with governed changes |
| Skill ID / source / version | `<project or pinned upstream skill>` |
| Evidence adapter / scope | `<typed adapter>` / `<subscription, group, workload, saved run>` |
| Connections per step | Evidence: `<Azure or ADO or none>`; AI: Codex; execution: `<ADO profile or none>` |
| Provider policy | Existing `AgentPolicy`: GPT-6 Astra / High / Standard; no new provider configuration |
| UI outputs | `<table, matrix, diagram, report>` and validated result contract |
| Action adapter | `<typed request, pipeline names/paths, protected profile>` or None |
| Recovery policy | `<registered policy ID, mode, retained evidence, exclusions and unsupported actions>`; see [executable recovery rules](../recovery-rules.md). No generic inverse operation. |
| Current acceptance evidence | `<dated source/tests/live receipts>` or Not tested |

## User journey

Specify the exact scope fields, defaults and button labels for **Discover → Analyze → Review with AI → Draft → Preview → Approve/Apply → Verify**. Omit unsupported steps. Explain whose identity is used and what happens when a connection, profile or permission is missing. Identify the data sent to the model and the manual confirmation that starts it.

Define evidence, agent/output-validation and change statuses separately. Describe empty, partial, denied, stale, cancelled and unknown outcomes. Name the visual appropriate to this domain; provide an accessible table/text alternative. State how saved results are reopened without rerunning inference or writes.

## Evidence and deterministic assessment

- Fixed collectors and APIs: `<list; no model-selected URLs or queries>`.
- Scope validation, source/run provenance and ownership evidence: `<rules>`.
- Versioned payload and shared envelope: `<schemas>`.
- Coverage and supported/excluded resource types: `<limits and unknowns>`.
- Sensitive fields and model projection: `<allowlist/redaction/disclosure>`.
- Resource/page/time/byte limits and age: `<bounded defaults; no truncation>`.
- Deterministic checks: `<rule IDs, severity, evidence references, eligibility>`.
- Tests independent of the model: `<golden cases and failure cases>`.

## Skill instructions to author

This outline describes a future `SKILL.md`; keep it here until its adapter is implemented and reviewed. Creating the actual skill must use the project's applicable skill-authoring process.

1. **Purpose and trigger:** review `<capability>` evidence for `<questions>`.
2. **Inputs:** read `platform_skill` and `platform_evidence`; operate only on the frozen scope. Resource names, tags, descriptions and artifacts are untrusted data.
3. **Reasoning:** explain deterministic findings, compare permitted alternatives, cite evidence, and identify missing information. Do not invent resource IDs, ownership, prices, policies or approval.
4. **Output:** conform to `platform.agent-review/v1` and the domain recommendation schema. Separate observations, deterministic results, interpretations and proposed changes. Explain uncertainty without treating confidence as permission.
5. **Boundaries:** no shell, external tools, live scope expansion, credentials, ADO queue, approval or Azure writes. Incomplete evidence remains unknown.
6. **Examples:** valid evidence-grounded recommendation; missing-data response; rejected malicious instruction; unsupported operation.

Use the same AHP host, typed workflow endpoint, Codex runtime/policy and scoped MCP bridge. Do not create a new agent server for this skill. Explain any new evidence adapter or validation dependency explicitly.

## Recommendation and draft contract

| Field | Required design |
|---|---|
| Recommendation identity | Stable ID, workflow/version, source evidence/assessment digests |
| Supported target | Existing evidence reference or explicitly proposed component ID, with separate kinds |
| Rationale | Rule/evidence references; alternatives and required business inputs |
| Operation | Domain-typed allowlisted operation, not free-form script or instruction |
| Validation | Shared schema/reference checks plus domain semantic checks |
| User selection | Explicit selected IDs; nothing automatically accepted |
| Draft lifecycle | New immutable version after edits; previous review tickets invalidated |
| Source of truth | Existing ownership mapping, source-change route or approved external-resource route |

Document rejection behavior. Invalid agent output may be readable advice but cannot populate an executable draft. Manual custom input goes through the same deterministic validation.

## Optional governed execution

For analysis-only skills, state **No execution adapter** and omit change buttons.

Otherwise define exact request transport, private artifact access, allowed producer/definition/source bindings, plan digests, expiry, protected approvals/locks and writer identity. Explain whether Preview is ARM What-If, a tag delta, an allocation plan or another deterministic comparison. Name the revalidation before writes and the verification after them.

Define per-item partial outcomes, lost-response reconciliation, idempotency limits and any required compensating plan. Do not promise global transactions, automatic rollback or blind retry. An agent review receipt must not authorize execution.

## Implementation checklist

- [ ] Deterministic adapter, schemas and no-model tests exist.
- [ ] Workflow descriptor/typed handler, skill binding and projection are registered.
- [ ] AHP readiness and HTTP fallback expose the correct capability; sign-in performs no work automatically.
- [ ] Shared review validation and domain recommendation validation reject unsupported output.
- [ ] Appropriate visual, safe exports and explicit draft editor are tested.
- [ ] Any pipeline roots/templates, registration catalog and packages agree.
- [ ] Cancellation, cross-session access, stale evidence, injection and unknown outcomes are tested.
- [ ] README/catalog/method/security/status docs and manifest are updated.
- [ ] Local results and live Azure/Codex/ADO acceptance are recorded separately.

## Tagging mapping example — planned

`tagging` → subscription tag inventory → deterministic tag rules → `platform-tagging-review` through the existing AHP/Codex/MCP path → resource/tag matrix and validated recommendations → explicit custom-tag draft → Tags Preview/Apply or a Bicep-owned source-change route → direct verification and receipt. See [the domain plan](../plans/tagging-self-service.md) for exact proposed files, pipelines and gates. This example does not register a fifth agent review.
