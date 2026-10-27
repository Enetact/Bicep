---
name: platform-service-architecture
description: Implement or review Platform Studio application and service code using customer-owned cells, shared contracts, typed clients and service-owned verification. Apply to Portal, Foundation API, workers, SDKs, agents and recovery refactoring; infrastructure authoring follows its separately scoped workflow.
---

# Code to the Platform Studio service architecture

Use this as the project coding standard for application services and their consumers. Inspect the relevant implementation before changing its boundary and consult the affected sections of the [architecture decision and migration plan](../../../docs/plans/platform-studio-application-service-architecture.md). When reporting progress or changing gates, consult the [commercial plan](../../../docs/plans/platform-studio-commercial-release.md) and [current status](../../../docs/completion-status.md); load historical detail only when needed. Guidance defines coding decisions; it grants no tools, permissions, approvals or production readiness.

## Choose the owning boundary

Group services around cohesive business capabilities and their data/authority. Begin with the dedicated customer Foundation control plane and independently owned executors. Extract another process when ownership, deployment or measured capacity warrants it; a validation function alone is not a new microservice.

| Layer | Owns | Dependency rule |
|---|---|---|
| Portal or other main application | Presentation, caller interaction and explicit customer connection | Consume typed service operations; keep verification and persistence out of UI handlers. |
| `SelfService.Foundation.Contracts` | Versioned wire models, identifiers, digests and shared tool/schema descriptions | Keep independent of storage, web hosts, provider SDKs, browser state and credential implementation. |
| `SelfService.Foundation.Client` | Bounded typed HTTP calls to a configured customer service | Depend on neutral contracts, not Foundation core or executable hosts. |
| Foundation API and application services | Authentication, current authorization, commands/queries and transport composition | HTTP/AHP handlers reuse application services; keep domain rules in their owning core/coordinator. |
| Foundation core and adapters | Domain rules, customer state, guarded verification, dispatch and reconciliation | Reuse existing store/coordinators; no Portal or web-host dependency. |
| Independent worker | Customer-bound claims, execution, heartbeat and durable outcomes | Use `FoundationPipelineExecutionService`; do not import Host/Portal/MCP to reuse a helper. |
| Trusted owner process | Explicit provisioning, custody, activation and recovery administration | Keep its privileged duties separate from ordinary service APIs. |

Foundation.Host still imports Portal for some source/agent infrastructure. Treat that as migration work, not a pattern to copy. Extract UI-independent adapters in reviewed increments instead of pulling an executable dependency into clients or workers. Reuse the existing Tagging, Operations and Telemetry libraries where their responsibilities fit.

Keep request, exact approval, job, required audit and outbox changes atomic inside one customer cell. Do not distribute this transaction across services for structural symmetry. Other services use the owning API or admitted events, not another customer's database or key directory.

## Preserve customer identity and authority

- Keep `CustomerId`, `CellId` and `EntraTenantId` distinct. Initial isolation uses dedicated customer databases, key namespaces, workers, quotas and private telemetry. Two customers in the same directory remain separate customers.
- Validate each ingress token for its API audience, issuer, lifetime, token kind, allowed calling client and scoped grants. Resolve `CustomerContext` server-side. A route/body customer selector, app role or entitlement alone proves no membership.
- Supply a service-specific token through an explicit trusted connection. Do not turn desktop Azure/GitHub/Codex caches, a provider token or serialized `BrowserSession` into shared service authority.
- Preserve human requester/approver separation and distinct workload duties. Models, AHP and MCP cannot create approval or mint privileges; reuse the existing exact human approval route.
- Centralize policy implementation while retaining checks at ingress, after slow body reads, transactional admission and final provider send. These protect different times; removing them is not deduplication.
- Keep current role/membership/policy, connection/catalog/source versions, exact digest, scope and expiry bindings. Changes invalidate stale intent and must be rechecked at execution.

## Keep verification and effects together

Run a complete guarded operation inside the service that owns its keys, actual custody sessions and outcome transaction. Retain concrete activation/checkpoint/stage/credential guards across awaits through the final decision and durable outcome. A remote `verified=true`, caller callback, matching DTO or serialized lease cannot replace that custody or authorize another process to write later.

Wire contracts carry bounded identifiers, immutable commitments and sanitized results. Keep private roots, tokens, keys, trusted contexts and live guard handles process-local. For recovery, use trusted server-side selections and derive every affected scope, including absence scopes; never accept a caller's replacement scope list or filesystem/key objects.

Keep review-only intent separate from changing authority. An approved adoption intent does not reserve destinations, adopt credentials, activate a database or release its recovery hold. Stage ciphertext and absence/completion proofs cannot become runtime provenance by renaming files or fabricating original transactions. Follow the [recovery protocol and remaining prerequisites](../../../docs/foundation-restricted-recovery-adoption-intent.md) when implementing that domain.

## Make calls and jobs reliable

- Use typed commands/queries rather than generic remote execution. Reuse shared descriptors instead of duplicating tool names, schemas and action maps in each transport.
- Configure endpoint and customer/cell/project/environment from trusted composition. Bound deadlines, bytes and concurrency; validate protocol, returned scope and artifact integrity. Keep tokens per-request, out of default headers, model input, errors and logs. Follow the [existing read client](../../../docs/foundation-service-client.md).
- Preserve redirect/cookie refusal and explicit credential routing. Do not silently fall back to direct provider execution when a service is unavailable.
- Persist jobs/outbox/inbox, idempotency, leases, fencing, cancellation and reconciliation. No ad hoc background loop may bypass the durable runner.
- A possibly completed changing effect with a lost response or lease remains `OutcomeUnknown`. Do not automatically replay it. Retries need operation-specific evidence and bounds; reconciliation records the actual outcome.
- Keep observed, predicted, proposed, verified, denied, partial, stale and unknown states distinct. Receipts record evidence/outcomes; they are not credentials or software versions. Git commits/tags/Releases define product history.
- Qualify backend, broker, service identities, independent custody and measured load/failure behavior before claiming multi-host or production scale. Extra processes do not establish those properties.

## Resolve source paths without changing custody identity

Use `PortalWorkspace` at Portal startup and explicit source-configuration boundaries. Derive the checkout containing `config/workloads.json`, including a known `Bicep` child of an outer workspace; do not hardcode a developer drive path or assume the launch directory is the checkout. Preserve explicit/bundled precedence, configuration-relative roots and source-link refusal. Foundation's `RepositoryRoot=null` remains source-free; invalid explicit source must not fall back elsewhere.

Do not apply source discovery in a global options setter or to database, activation, credential, cache or actor-runtime paths. `CodexFoundationModelClient` deliberately uses private actor `PortalOptions.RepositoryRoot` and `AgentRuntimeRoot` directories that are not checkouts. Their existing identities, private path validators and custody commitments stay separate. See [canonical workspace behavior](../../../docs/local-portal.md#canonical-workspace-selection).

## Implement and qualify a bounded slice

1. Trace the entry point through shared handler, authorization, persistence/adapter and outcome. Identify owning customer data, caller identity, consistency requirements and existing reusable implementation. Separate current code from proposed boundaries.
2. Make the smallest complete application change with thin transport/client adapters and one policy implementation. Preserve wire/CLI compatibility unless the task explicitly changes it. For application-only work, leave pipeline definitions, Bicep/parameter assets and emitter rules intact; separately scoped infrastructure work uses its own workflow guidance.
3. Review project references, dependency locks and actual publish closures after extraction. Contracts/clients must stay neutral; Worker must stay web-independent. Keep strict collision and hash checks. Align reviewed runtime assets rather than choosing arbitrary conflicting DLLs or suppressing security findings.
4. Test the affected behavior with disposable cells, synthetic signed identities and mock providers when live execution is outside scope. Select meaningful cases for the change: cross-customer/same-directory isolation, revocation/expiry, stale approvals, invalid proofs, cancellation, lease loss, duplicates and uncertain outcomes. Preserve private state and unrelated changes.
5. Use governed lifecycle/owned-process helpers for local service and test runs. Bound output and descendants, verify ownership before stopping processes, and retain failed evidence. Do not loosen deadlines, skip checks or relabel partial runs solely to obtain a pass. A failed broad profile remains failed even when focused cohorts pass.
6. Update affected guides and commercial work-packet status with exact source/test scope, skips and remaining gates. Requalify changed archives; prior package results do not cover later code. Run the source manifest/check and diff hygiene. Guidance-only edits need skill/link/catalog checks, not a new full runtime suite.

Use the [documentation skill](../self-service-docs/SKILL.md) for maintenance and [workflow builder](../platform-workflow-builder/SKILL.md) for new self-service journeys. Provider workflows also follow their existing orchestration/provider instructions. Honor explicit task authorization and local-only constraints; this skill does not authorize deployments, permission grants, real credential recovery or hold release.

In the handoff, state which service owns the changed behavior, how callers use it, what actually passed and which migrations/gates remain. Consult current evidence before closing F0/F1/F2; local synthetic qualification and later G3 production acceptance are separate decisions.
