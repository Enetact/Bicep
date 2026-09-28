# Tag governance: discovery, advice and reviewed changes

Implemented in source on 27 September 2026. The portal, shared deterministic engine, tagging skill, two manual pipelines and source-tag propagation are connected. **Live Azure/Codex/ADO acceptance is still required. Apply is disabled in the checked-in profile, with no registered external resources or qualified writer types.** No cloud resources or permissions were changed during implementation.

## Start and use it

From the repository root, use the existing portal setup:

```powershell
./scripts/Setup-Portal.ps1
./scripts/Start-Portal.ps1
```

Open **Tag governance** at `http://localhost:5087/`.

1. Connect **Azure** under Connections. Refresh subscriptions, select one by display name **and ID**, then click **Discover with Azure sign-in**. Authentication alone does not scan anything.
2. Inspect observed resources, tag state, findings and collector coverage. Search by resource, scope, type or tag; switch between resource rows and the tag matrix. Pages contain 25 rows. The matrix shows the first 12 sorted keys; resource rows and exports retain all observed keys. Key coverage lists usage counts. Selecting a row fixes its resource ID even when the filter changes.
3. Select resources and enter a reviewed key/value. Default **AddIfAbsent** preserves an existing value; **SetValue** explicitly proposes replacement of that key. Use `custom.<name>` for a new custom key. Click **Validate draft and show changes** to see Add/Update/NoChange, source route and blockers. Nothing is written to Azure.
4. Optionally choose up to 50 observed resources and click **Review selected evidence with Codex**. Connect Codex separately and click Run. The existing AHP coordination, native Codex runtime and scoped MCP bridge execute `platform-tagging-review` using Astra / High / Standard. Only required/approved tag keys go to the model; arbitrary custom tag values are excluded. The full projection is bounded to 256 KiB. The agent may explain missing business inputs without recommending values.
5. Select individual validated suggestions and click **Copy selected advice to tag draft**. No suggestion is preselected. Edit and validate again. Agent advice cannot approve changes or queue a pipeline.
6. For ADO delivery, connect Azure DevOps, use **ADO setup** to register the two tagging definitions after their files are published to GitHub main, and queue **Discover** through the reviewed request. Load the successful run ID in Tag governance. Browser snapshots are useful locally but are not accepted as pipeline discovery artifacts.
7. Review **Preview only**, then explicitly confirm queueing. Inspect its Extensions README and `tag-preview` artifact. A later **Preview and apply** request creates its own fresh Preview; the protected Apply stage consumes that same run's exact plan. It is not an approval of an earlier run by reference. Discovery must be at most 30 minutes old and from the same main source commit as Preview.
8. Use **Saved Preview and outcomes** to load the plan, Apply receipt or independent verification from the exact Tag pipeline run. This is historical evidence. Discover again to create another draft. Unknown outcomes require reconciliation before retrying.

## Pipeline entries and setup gates

| ADO definition | YAML | What it does |
|---|---|---|
| Discover - Tags | [azure-pipelines-tags-discover.yml](../azure-pipelines-tags-discover.yml) | Discover stage collects and analyzes; publishes inventory, deterministic findings, manifest and README in `tag-discovery`. |
| Tags - Preview and Apply | [azure-pipelines-tags.yml](../azure-pipelines-tags.yml) | Preview, optional protected Apply, then Verify. Default **Preview only** excludes Apply/Verify. Artifacts: `tag-preview`, `tag-apply`, `tag-verification`. |

Both are manual Windows-hosted pipelines with `trigger: none` and `pr: none`. Their shared preparation template verifies a private ADO project, GitHub repository/main context and the Discover producer before building the locked .NET tool. Parameters are the registered `subscription-tags` profile, operation, discovery run ID and a bounded non-secret base64url request supplied by the portal. Base64 is transport encoding, **not encryption**. Native ADO does not provide dynamic post-discovery dropdowns; the portal provides the richer editor.

Platform setup is deliberately not automatic:

- The ADO project must be verified **private** before queuing or publishing tag inventories. Unknown/public visibility blocks the path. Public GitHub contains source and reviewed configuration, not discovered inventories, credentials or runtime receipts.
- Root files currently bind the existing `SC-AZ-A-Bicep` connection at its existing subscription scope. No permission changes were made. Before write qualification, platform owners should bind a dedicated, least-privilege writer to Apply and a reader to discovery/preview/verification; do not expand a shared Contributor/Owner connection as a convenience. The generic Tags API requires `Microsoft.Resources/tags/write`, plus the relevant discovery/read permissions. Review automation-sensitive keys and scope separately; built-in Tag Contributor is broader than the two permitted operations. [Azure tag access](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/tag-resources#required-access)
- Create **platform-tag-governance** in ADO with approvals, main branch protection, pipeline/service-connection authorization and an exclusive-lock check. YAML's `lockBehavior: sequential` works with that external check; it does not create one. Keep environment administration separate from requesters. The pipeline environment variable identifying Apply is a context check, not proof of approval. [ADO checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals?view=azure-devops)
- Review [config/tag-governance.json](../config/tag-governance.json): exact external resource IDs, source target mappings, resource types qualified by a controlled live test, protected automation keys, governance digest and review status. Do not mark `enabled` or `governanceReviewed` true merely to bypass a blocker. The observation README includes the governance digest. Changes to ownership/support settings require a fresh discovery; changed source commits require a fresh ADO discovery.
- Configure artifact retention/access policies and protect the local `artifacts/` directory. Tag masking is heuristic, not a guarantee that arbitrary metadata is public. Inventory and requests must never contain secrets or personal data.

## What collection and rules establish

The subscription dropdown lists enabled subscriptions visible to the Azure browser identity in the configured tenant. Only the registered profile can use the ADO action path; other subscriptions remain browser discovery/analysis only with no registered ownership.

The shared `AzureTags` adapter uses a fixed, subscription-scoped **Resource Graph** projection, ordered by ID, in pages of 1,000. It checks continuation loops, duplicates, changing totals, missing continuation and a 50,000-resource/100-page bound. Resource groups and subscription tags are collected separately through ARM. Read requests have a 45-second bound and limited 429/503 retries; discovery has a 15-minute budget. Failed collections remain Partial/Failed. Provider children, data-plane tags, inaccessible resources and index lag are not proven absent. [Resource Graph visibility](https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview)

Policy assignments (including scopes below the subscription), referenced definitions/initiatives and subscription lock-list responses are fingerprinted. A definition read failure blocks qualification. This is **not** a complete Policy evaluator, exemption/deny-assignment assessment, or proof of inherited permission coverage. The operator's governance review must establish that the selected external resources are safe for generic tag operations. High-contention, policy-controlled or otherwise unqualified resources must stay outside the allowlist.

The first deterministic rule pack detects missing/blank/placeholder required keys, sensitive-looking data and unknown tag support. It validates case-insensitive keys, exact values, per-type lengths, conservative DNS counts, protected/automation keys and resulting tag counts. A bounded support allowlist is versioned with `ruleVersion` in source; cost-report support, arbitrary organizational aliases/enumerations and compliance certification are not inferred. Unknown types remain visible but blocked. Required business values are human inputs, never derived from resource names.

Inventory is sealed with a canonical digest. Validation recomputes supported type, tag state and ownership from the current profile; an artifact cannot grant itself ownership. Known Bicep control markers can restrict an external route but never grant one. Empty successful tags are Untagged; a denied or malformed read is unknown. [Tag semantics and limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/tag-resources)

## Exact write and source routes

**External:** Preview requires complete required collection coverage, fresh evidence, exact resource IDs, approved types/ownership, valid keys and a direct ARM fingerprint match. It produces a deterministic **tag delta**, not ARM What-If. Apply checks the profile, expiry, plan hash and correspondence to the original request; checks governance and every resource before its first write; then checks each resource again immediately before Merge. It durably saves Pending intent before PATCH, performs a single Merge, directly reads back the full map and verifies requested keys plus preservation of unrelated tags. It stops on uncertainty and retains Verified/Blocked/Unknown outcomes. There is no Replace, Delete, arbitrary resource PUT, automatic rollback or write retry.

The generic [Tags PATCH contract](https://learn.microsoft.com/en-us/rest/api/resources/tags/update-at-scope?view=rest-resources-2021-04-01) does not provide a documented `If-Match` precondition. Read-before-write and an ADO lock cannot prevent another Azure writer from racing. Use a controlled change window/qualified ownership scope; never claim atomic compare-and-swap. Plans expire 30 minutes after Preview, including while waiting for approval.

**Source-owned:** `sourceTargets` maps an exact resource ID to an existing `self-service/targets/<workload>.<environment>.json`. The portal creates a downloadable proposal, not a GitHub commit. Merge its `parameterOverrides` into that target without dropping existing overrides. `owner` and `costCenter` use dedicated parameters. `custom.*` and `criticality` use `customTags`; workload/environment/control tags require their platform/catalog change path. A target change affects **all resources composed by that target**, so mixed resource values for one target/key are rejected. Split mixed external/source selections into separate requests.

All seven main compositions and subscription wrappers accept `customTags`, pass them to existing module `tags` inputs, and merge required platform tags last. The pipeline parameter validators reject protected/case-variant overrides and restrict custom tags to eight pairs, leaving room for required tags on DNS resources. Source values travel through the existing target override, parameter, Template Spec and bundle path. The developer's four-field workload intent contract remains unchanged: custom tags are reviewed **target configuration**, not arbitrary queue-time workload parameters. Existing destination/shared resources are not adopted merely to tag them. Direct portal/third-party tags omitted from source may still be replaced by subsequent Bicep deployment; preserve the desired map in source. [Bicep tag behavior](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/tag-resources-bicep)

## Methods and contracts

| Entry | Downstream responsibility / evidence |
|---|---|
| `GET /api/tags/scopes`, `POST /api/tags/discover` | `TaggingService.Scopes/Discover` → `AzureTags.Scopes/Discover/Graph/List/Governance`; browser-owned report and coverage. |
| `GET /api/tags/evidence/{id}`, `discovery/{runId}` | Session/age validation or private ADO producer/repository/branch/run/commit checks, bounded signed artifact ZIP and inventory digest. |
| `POST /api/tags/drafts` | `TagRules.BuildPlan`; source override proposals, exact deltas and blockers. No write endpoint. |
| `POST /api/agent/run`, workflow `tagging` | `WorkflowReviews.Definitions` → selected evidence projection → existing Codex/MCP → strict `platform.agent-review/v1` validation. Saves `review.json`, text and receipt. |
| `POST /api/tags/pipeline/review`, `queue/{ticket}` | Profile/private project/source checks, exact payload review, single-use five-minute browser ticket, one queue attempt. Edits invalidate the UI ticket; backend rechecks profile and evidence age. |
| `GET /api/tags/results/{runId}/{preview\|apply\|verify}` | Fixed Tag pipeline and artifact names, plan/receipt digest correspondence; historical results only. |
| `Invoke-TagWorkflow.ps1` | Fixed CLI verbs, account/tenant verification, token in process environment only; never a user-provided script. |
| `SelfService.Tagging.Tool` | `discover`, `analyze`, `preview`, `apply`, `verify`; atomic local receipt replacement; sanitized README/artifacts and failure status. |

Domain contracts are typed C# records in `SelfService.Tagging/Contracts.cs`: inventory, coverage, finding, edit/request, plan/delta and execution receipt. Parsers reject unknown or case-colliding JSON properties. Invented model resource IDs, unrelated evidence references, prohibited keys and operations cannot become selectable recommendations. The shared provider remains one active run per browser, five minutes, 12 MCP calls, both snapshot tools required. No separate Azure MCP server or new credential store was introduced.

## Local verification and manual acceptance

```powershell
dotnet test ./tests/SelfService.Portal.Tests -c Release -p:RestoreLockedMode=true
dotnet build ./src/SelfService.Tagging.Tool -c Release -p:RestoreLockedMode=true
npm ci --prefix ./tests/infrastructure --ignore-scripts --no-audit --no-fund
node --test ./tests/portal/tagging.test.mjs
./scripts/Test-Products.ps1
./scripts/Test-Tagging.ps1
./scripts/Update-ServiceCatalog.ps1 -Check
node ./tests/infrastructure/verify.mjs
```

The browser fixture under `tests/portal/` is visibly synthetic, separate from the real application, and never packaged or started by the portal launcher. Local tests do not qualify Azure provider writes or ADO checks.

For live acceptance, first test browser discovery and an optional Codex review. Then publish source, register both definitions, configure private ADO protections and qualify a designated disposable external resource. Record exact principals, source commit, before/after maps, Preview digest, approval, provider request ID, preserved unrelated keys and independent readback. Test drift, denied reads, expired approval and unknown outcomes before enabling routine use. For source-owned resources, validate a target proposal through workload What-If and verify two redeploys retain custom tags. These cloud acceptance steps remain unperformed.

The reusable [platform-workflow-builder skill](../.agents/skills/platform-workflow-builder/SKILL.md) defines this pattern for future iterations. Its [standard](agent-workflow-standard.md) distinguishes this structured tagging path from the four legacy advisory Markdown reviews.
