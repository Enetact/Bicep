# Private DNS and drift analysis

[Wiki](README.md) · [Phased delivery plan](plans/operations-workflow-suite.md) · [Agent connection](agent-workflows.md)

**4 October 2026: first O0–O1 source increment implemented and locally tested.** The portal has a **DNS & drift** workspace, provider-specific ARM discovery, deterministic rules, comparison matrix, configured-relationship diagram, optional validated Codex advice and editable proposal exports. These are analysis capabilities. Live Azure/model acceptance, accepted deployment baseline storage and DNS migration/remediation are not established.

## Use the portal

1. Start the rebuilt application with `scripts/Start-Portal.ps1`. Configure your own Entra tenant/client registration using the [portal setup](local-portal.md); no client secret is needed. Connect **Azure** under Connections.
2. Open **DNS & drift → Refresh subscriptions from Azure**. The authenticated tenant comes from the Microsoft sign-in result. The subscription IDs/names come from the live ARM subscriptions response, filtered to enabled subscriptions in that tenant. No workload catalog subscription or developer-specific subscription ID is used by this collector.
3. Select 1–8 returned subscriptions and **Collect new DNS observation**. Collection re-enumerates subscriptions before reading resources. A forged selection, incomplete subscription enumeration or a changed identity blocks collection. Successful enumeration represents visible scope, not proof of complete tenant access.
4. Choose **Private DNS analysis** and **Assess selected observation**. Inspect records, findings, collection coverage and the configured relationship diagram. No resources are preselected for change.
5. For comparison, collect a later observation, choose **DNS drift and desired changes**, select the earlier reference if available and enter explicit desired TTL/registration settings. Missing desired values remain Unknown; the prior observation is never silently treated as current desired configuration. The first profile does not evaluate arbitrary Bicep or compare every Azure property.
6. Optionally choose **Review assessment with Codex**. This selects the saved assessment in Agent workflows; explicit **Run** sends its bounded evidence through the existing AHP/Codex/MCP implementation. Connect Codex separately. The active policy remains **GPT-6.1 Sol / Ultra / Standard**, with runtime/model checks and no fallback.
7. Select an advice item with **Use as editable proposal**, or select observed resources yourself. Enter an owner, rationale and proposal type: Investigate, SourceAmendment or MigrationReview. A migration review requires distinct same-name source zones and an observed destination.
8. **Save proposal and preview** creates a new immutable proposal and a clearly labeled proposal review. It is **not ARM What-If, DNS migration authorization or a deployable plan**. Editing clears the displayed preview; create a new proposal to preview revised inputs. Export evidence/assessment and proposal/preview separately.

## Coverage and rules

The fixed reader lists zones, records, zone VNet links, private endpoints/zone groups, VNets, private resolvers/endpoints, forwarding rulesets/rules/links. It retains a bounded property projection and ETags; it does not return arbitrary ARM bodies, tags, TXT values, credentials or logs. Initial record value support is **A, AAAA, CNAME and PTR**. Other record types retain metadata with explicit unsupported value coverage. Non-auto-registered records have **Unknown ownership**, not assumed manual ownership.

| Rule | Meaning |
|---|---|
| DNS-001 | Same-name zones are candidates for review, not proven duplicates. |
| DNS-002 | Same-name/type records have conflicting values or TTLs; no automatic winner. |
| DNS-003 | Endpoint zone-group coverage is missing/incomplete or no zone group was observed; alternate DNS remains possible. |
| DNS-004 | Link completion, registration lifecycle and unverified consumer/resolution paths need review. |
| DNS-005 | Denied/partial collections or unknown ownership prohibit unused/retirement claims. |
| DRIFT-001 | DNS property comparison separates InSync, DesiredChange, ObservedDeviation, Conflict, UnmanagedCandidate and Unknown within the supported profile. |

The graph includes observed configuration references only. Records stay in the matrix. More than 200 graph nodes produces an explicit limit message rather than a truncated diagram. Effective DNS answers, forwarding completeness, application reachability and provider-managed record mappings remain unverified. New domain agent reviews return structured advice; their diagram is deterministic, not newly generated Mermaid. The existing resource visualizer is still the separate skill for generated, validated diagrams.

The reader uses Private DNS API `2024-06-01`, Network API `2025-09-01` and Resolver API `2022-07-01`. Each collection allows 10 pages/5,000 returned rows, rejects cross-origin/path and repeated continuations, and bounds responses to 4 MiB. A scan stops adding requests at 220 collections or 5,000 distinct resources; child budgets cover up to 50 zones, 50 endpoints and 20 resolvers/rulesets per subscription. Limits/denials become explicit Partial/Denied/Failed coverage, never empty success. No automatic retry or hidden credential switch is performed.

The design follows Microsoft's [Private Endpoint DNS integration guidance](https://learn.microsoft.com/en-us/azure/private-link/private-endpoint-dns-integration), [record-set pagination contract](https://learn.microsoft.com/en-us/rest/api/dns/privatedns/record-sets/list) and [Private Resolver configuration model](https://learn.microsoft.com/en-us/azure/dns/dns-private-resolver-get-started-bicep). Those describe Azure behavior, not acceptance of this implementation in a live subscription.

## Shared implementation and authority

| Component | Responsibility |
|---|---|
| `src/SelfService.Operations/Contracts.cs` | Versioned evidence, assessment, advice, proposal and preview records; schema/digest/scope checks. |
| `PrivateDnsRules`, `DriftRules`, `OperationsRules` | Deterministic domain checks, explicit desired edits and validated recommendations. Shared by portal and CLI. |
| `Operations/PrivateDnsReader.cs` | Fixed GET routes, page/byte limits, coverage and safe property projection. This is an ARM adapter; no unqualified Microsoft MCP tools are invented. |
| `Operations/OperationsService.cs` | Dynamic subscription resolution, browser ownership, freshness, immutable local files, assessment and proposal orchestration. |
| `BrowserIdentity` | Tenant returned by Azure sign-in, MSAL token acquisition and disconnect invalidation. |
| `AgentWorkflows` + `WorkflowReviews` | Two registered domain handlers and review-output validation through the existing runtime. |
| `AgentHostChannel`, `CodexAgentRuntime`, `AgentMcpBridge` | Existing AHP coordination, current model policy and frozen `platform_evidence`/`platform_skill` tools. No new auth provider or arbitrary cloud agent tools. |
| `.agents/skills/platform-private-dns-review`, `platform-drift-review` | Domain reasoning/output instructions pinned by the normal skill hash receipt. |
| `wwwroot/operations.mjs` | Scope picker, resource/finding/property matrices, deterministic diagram and proposal editor. |
| `src/SelfService.Operations.Cli/`, `scripts/operations/` | Offline deterministic assessment using the same rules; callable by a future qualified pipeline. |

API routes are `/api/operations/catalog`, `/scopes`, `/evidence`, `/collect`, `/assess`, `/proposals` and `/proposals/{id}/preview`. Read a known snapshot through `/evidence/{id}`. POSTs use the existing browser origin/CSRF checks. There is **no Apply/execute route or new ADO pipeline root** in this increment.

Observed configuration belongs to the Azure resource owner. Desired edits and proposals belong to the user/owning source. Codex advice is host-stamped advisory-only and cannot become an execution receipt. The future executor must independently qualify source ownership, identities, permissions, target locks and domain verification.

## Evidence and baseline retention

`artifacts/portal-operations/<id>/` stores immutable `evidence.json`, `assessment.json` (including desired edits) or `proposal.json` using create-new writes. Existing agent review files and receipts remain under `artifacts/portal-agents/`. These ignored local files can contain internal resource names/IP configuration; protect and export them according to your organization's data policy. They are not public repository source.

Browser access is session-owned and cleared on Azure disconnect. Budgets are 20 observations and 100 assessments/proposals per session; reviews/previews require an observation no older than 15 minutes. Older browser-owned observations can be comparison references or exported. The offline CLI can reassess an exported snapshot without asserting it is fresh or authentic. No file-upload endpoint rehydrates browser authority.

**Accepted baseline store remains an onboarding/implementation gate.** A local observed reference is not proof of a successfully deployed desired state, and a digest establishes consistency rather than Azure authenticity. Missing resources stay Unknown because complete ownership/direct-read evidence is absent. Before promoting durable accepted baselines, qualify a private platform store, retention/access policy and verified ADO producer/source/parameter/after-state chain. Future promotion must preserve previous baselines and reject failed or partial deployment receipts. No baseline acceptance button exists yet.

## Offline assessment

Run from the repository root with a portal export (or raw sealed evidence):

```powershell
./scripts/operations/Analyze-Operations.ps1 -Workflow private-dns `
  -EvidencePath ./my-dns-export.json -OutputPath ./new-assessment.json

./scripts/operations/Analyze-Operations.ps1 -Workflow drift `
  -EvidencePath ./later-export.json -ReferencePath ./earlier-export.json `
  -OutputPath ./new-comparison.json
```

The wrapper restores locked .NET dependencies, builds its CLI and passes file paths as arguments. It makes no cloud/model calls and refuses to overwrite existing output files. It is reusable source, not a test integration or an execution adapter.

## Manual acceptance and subsequent phases

1. Configure a separate portal Entra public-client registration, sign in to Azure, refresh scopes, and confirm returned tenant/subscription names match that identity. Validate denied/empty collections in an approved read-only scope.
2. Reconnect Codex after rebuilding. Collect a small DNS scope, assess it, run one explicit domain review and verify terminal completion, both MCP tool reads, exact model policy and structured advice validation in its receipt.
3. Review a second observation against a local reference and explicit desired settings. Confirm missing desired/property/ownership data remains Unknown, and edited inputs require a new assessment/proposal.
4. Confirm proposal Preview displays blockers and no Apply action. Rehearse cloud mutations only after the later adapters and platform qualification gates exist.

O0/O1 live acceptance and broader baseline/property coverage remain open. O2–O6 (identity/private probes, pipeline analysis, data promotion, AKS maintenance, qualified DNS/drift execution) remain planned in the [phase ledger](plans/operations-workflow-suite.md). No new pipeline registration, deployment target, cloud permission, recurring scan or generic rollback is enabled by this increment.
