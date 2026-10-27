# Platform Studio application service handoff

Snapshot: 26 October 2026, America/Chicago. [Commercial plan](../plans/platform-studio-commercial-release.md) · [Application service audit](../plans/platform-studio-application-service-architecture.md) · [Validation](../validation.md).

## Starting point and user direction

The Git root is `C:\Bliobby\blob-transfer\Bicep`, branch `feature/selfservice`. At audit start, HEAD and the actual remote branch both equal `6d7ef65786de2d1616a8ca22ccb6c9e14a4ab45f`; the worktree was clean. Later application-refactor changes follow that baseline. Recheck actual Git state rather than assuming older push-auth failures are still current. GitHub commits/tags/Releases define product history; operational/build/test evidence is not another versioning scheme.

The service-boundary implementation is recorded in `d1d7e46ad6b817cf0e4129360e473469313fb04e`. The preview package below binds that source revision. A subsequent canonical-workspace correction changes Portal startup and explicit Foundation source configuration; that correction is tested from current compiled outputs and is not included in the earlier ZIP. Rebuild/requalify a later archive before promoting it.

Latest direction: audit the whole application/foundation/service architecture and refactor toward shared services and typed clients, preserving customer isolation and scale readiness. Keep pipelines, infrastructure Bicep/parameter files and existing pipeline/source-generation behavior intact. This broadens the immediate priority beyond the preceding restricted-recovery-only increment. Do not create one distributed service per validation function or split an existing atomic customer transaction into best-effort remote calls.

The software is a local preview. No defensible completion percentage is established. F0–F2 and commercial admission remain open.

## Progress against the plan

| Gate/area | Implemented or locally qualified | Remaining |
|---|---|---|
| F0 architecture/baseline | Dedicated-customer-cell design; identity/data/job/audit interfaces; signed isolation fixtures; reproducible evidence and preview packaging tooling. | Supported product/pattern freeze, formal architecture/threat/rights decisions, named ownership and production backend/runtime/capacity objectives. |
| F1 identity/data/audit | Strict signed tokens and scoped grants, reviewed administration/support, separate CustomerId/CellId/EntraTenantId, SQLite schema 15, immutable plans/approvals, artifacts, audit/exports/anchors. | Complete the supported bounded foundation slice and local isolation/authority exit evidence. Broader Portal migration follows F2; real identity provisioning/consent and production hosting/custody are later acceptance. |
| F2 reliability/recovery | Durable jobs/outbox/inbox, fencing/leases, crash/duplicate/reconciliation tests, owned workers, quotas/local load tests; encrypted credentials, restore holds, runtime binding and protected restricted recovery review. | Actual changing recovery/adoption, source-consumer coverage/fencing, coordinated activation/hold release, independent latest/key custody and supported-host/storage/scale qualification. |
| P2 product/commercial | Existing catalog, AVM/child parameterization, skills, AHP/Codex/MCP and operator Portal; development package/install tooling. | Customer UI service migration, admitted feature expansion/demo, signing and clean-machine lifecycle, legal/security/rights/support clearance and release admission. |

Customer multitenancy currently uses dedicated cells, not a pooled multi-customer database. Each cell owns its data/key/runtime boundary. The store preserves runtime state; contracts bind scope and allowed transitions; receipts record outcomes/provenance. AHP/model/MCP responses never manufacture approval or provider authority.

## Latest completed recovery and lifecycle work

- `3c569c6`: schema-15 restricted adoption-intent preparation and distinct-human approval, following schema-14 joint verification in `13a1329`. Actual activation → checkpoint → stage custody, full present-plus-absence scope and fresh destination proof authentication precede the frozen-time final authorization/audit commit.
- Approved intent grants **no** namespace reservation, changing admission, adoption, activation, source fence or hold release. Never-bound/unknown/mismatched restored lineage is refused. [Exact protocol](../foundation-restricted-recovery-adoption-intent.md).
- Earlier full qualification: **3,852 Foundation passed / 0 failed/skipped**; **733 Portal passed / four native opt-ins skipped**; exact preview checks **217 package / 608 installation / 67 dependency / 65 static-secret**. These are retained results for that increment, not fresh results for later code.
- `6d7ef65`: access-rule-only private-directory permissions fix in the three startup helpers. Separate **145 lifecycle/ACL checks** passed and a real six-project preserving Studio restart was accepted. Individual provider authorization was not established by that startup.
- During this audit, ordinary and native HTTP checks at `http://localhost:5087` returned connection refused. The successful startup is historical acceptance; do not claim the app is currently running. This audit did not restart it.

## Current application refactor

- `SelfService.Foundation.Contracts`: dependency-neutral descriptions for the seven existing Foundation tools, exact schemas/order/AHP mapping, and scoped read-response contracts.
- `SelfService.Foundation.Client`: fixed-cell typed job/artifact reads using per-request Foundation API bearer tokens. No provider/desktop token conversion, stored token, write/approval/adoption method, redirect, cookie or automatic retry. Scope/cell/contract/object checks and artifact SHA verification precede accepted results. Existing artifact metadata route includes content; the metadata projection verifies then discards those bytes.
- Foundation HTTP adds authenticated cell/contract response headers. They are routing evidence, not authorization capabilities.
- `FoundationPipelineExecutionService`, `FoundationExecutionRuntime` and concrete `FoundationExecutionActivation` centralize execution and actual lease lifetime. Existing Host runner/activation APIs delegate for compatibility. Independent Worker references Foundation core instead of Host/Portal.
- Existing Foundation.Host → Portal dependency and browser-session workflow path remain. The client is available for explicit consumers; the Portal has not been silently switched to customer service mode.

The first focused execution passed **85/85**, no failures/skips or compiler warnings, under `artifacts/service-boundary-audit/e5c5a1e28759481d8690f18075771bff/results/service-boundary-focused.trx`. This includes new contract/client/executor/dependency checks and existing activation regressions. The new client tests use actual signed TestServer journeys, both reconciled outcomes, and an actual loopback production-handler redirect/cookie check. Consult the new dated validation entry for any later full/regression/package results and their exact source scope.

The final dependency-aligned focused run also passes **85/85** in `final-results/service-boundary-final.trx`. A separate affected-consumer cohort passes **148/148** in `consumer-results/service-consumers.trx`; cohorts overlap. Fresh Portal regression is **733 passed / four native opt-ins skipped**. The actual package passes **211 checks** and three isolated entry-point smoke paths; dependency and static-review results are indexed in validation. These do not erase the rejected unfiltered Foundation attempt.

Following the canonical-workspace correction, **750 Portal tests pass / four native opt-ins skip**, and **122 Foundation configuration/service cases pass**. The 17 new Portal cases and three new Foundation cases cover workspace/checkout distinction, configuration-relative source selection, explicit failure and source-free mode, including link refusal. An actual hidden Portal start from the outer workspace, with no RepositoryRoot argument, serves matching source assets on its owned temporary listener and then stops. No provider/login/checkpoint/main-port operation occurs. See the canonical-workspace validation entry for report pins.

The fresh unsigned preview is `artifacts/foundation-packages/1bc5075ca1d1497c9fdca67a4c9f0184/PlatformStudio.Foundation.LocalPreview.zip`, SHA-256 `d1914b60c3eb5ffe75442de37ab96174a49948b49e313516ee52cc126eb2615e`, 30,742,177 bytes, 118 entries/117 hashes/69 runtime files. Actual closure: nine projects/25 packages/twelve rights findings, still Blocked. Initial publishes `34c513...` and `379a2e...` refused an abstraction-version collision; Worker now explicitly pins the same 10.0.10 logging/DI assets as Owner, without web dependencies, warning suppression or weakened collision checks. The successful publisher removes five owned scratch targets / 368,192,734 logical bytes, zero uncertain removals. Failed/partial records remain retained.

That exact ZIP passes **211 package / 608 installation / 67 dependency-SPDX / 65 static-review contract checks**. The dependency run uses the existing hash-pinned official SPDX schema. The supported text/managed-string scan has no findings in covered surfaces; native/resources/obfuscated surfaces and broad security/legal clearance remain unqualified. Initial malformed-path invocations and the older pre-extraction dependency-count refusal remain recorded, not relabeled as successful tests. Commands requiring custody paths use full Windows backslash paths; `ProbeSdkPath` is the SDK directory, not `dotnet.exe`.

The audit captured **1,545 boundary-inventory files**, including **123 Bicep/parameter files and 79 YAML files**, in `artifacts/service-boundary-audit/e5c5a1e28759481d8690f18075771bff/excluded-files.before.json`. The final documentation update intentionally changes root `README.md`; all other inventory entries must remain identical. Final preservation comparison belongs with this task's validation. Source generation/emitter and pipeline-facing scripts are also outside the implementation changes.

## Next implementation sequence

1. Resolve the full qualification runner's deadline/descendant handling before claiming a fresh complete Foundation pass. Run `bab6d48582a94098be9af70a06ef8175` is Rejected (`TestExecutionRejected`), with unchanged source-before/after, no TRX and no pass count. The 900-second combined build/test deadline was not enlarged; surviving exact-run testhost/vstest processes were stopped only after holding and verifying their process handles, executable and creation time. Captured evidence is in that run. `FoundationTestExecutionEvidence.ps1` waits for output and exit together and only kills the parent; reuse existing owned Windows Job containment with explicit test-appropriate limits and regress inherited pipes/nonzero/overflow/deadline paths. No application correctness stall was established. Requalify actual preview closures before promoting any package; new first-party projects need distribution review.
2. Implement an explicit customer-aware Portal service connection and narrow read-only jobs/evidence slice. Define the API audience, calling client and delegated or workload identity contract; test with synthetic signed identities locally. Never forward a provider token or convert an in-memory browser context into Foundation authority. Fail visibly instead of falling back to direct provider execution.
3. Extract UI-independent agent/source infrastructure so Foundation.Host no longer imports the entire Portal executable. Preserve pipeline/Bicep/emission interfaces and output. Migrate additional Portal features onto shared commands, immutable plans, jobs, receipts and artifacts one tested slice at a time.
4. Continue recovery inside its own customer authority boundary. Define authenticated recovered runtime credential envelopes and original absence provenance first; stage files cannot become runtime credentials by renaming files or inventing an original transaction. Then add fresh reservation, changing capability/one-use admission, interruption reconciliation and coordinated activation under independently reviewed conditions. Keep hold release closed until its prerequisites pass.
5. Complete F0 decisions and F1/F2's local executable evidence for the supported slice before P2. Real identity/provider permissions and production backend/worker/storage/custody/capacity acceptance remain later G3 work. All 22 packet identifiers remain unchanged.

## Invariants and operating constraints

- Preserve authorization at ingress, after slow body reads, transactional admission and final send. These protect different time boundaries; reduce duplicate implementations, not current-authority checks.
- Never serialize trusted `CustomerContext`, private keys, native credential caches or retained guard/session handles into service contracts. A remote `verified=true` cannot authorize another process to mutate a vault or activate a database.
- Keep source discovery startup-only. `CodexFoundationModelClient` deliberately creates private actor `PortalOptions.RepositoryRoot`/`AgentRuntimeRoot` paths that are not source checkouts; never apply the workspace resolver in a global options setter or private-path helper.
- Keep complete request/approval/job/audit/outbox commits within one customer cell. Lost response or lease after dispatch remains `OutcomeUnknown`; no automatic changing replay.
- Recovery-job membership resolver currently filters to Operator. Credential review needs separate Approver/Auditor scope handling. Do not reuse it unchanged or bypass active-runtime startup leases for an inactive restored cell.
- Default Foundation host/worker remain disconnected. Do not enable Azure targets, deploy, submit live pipelines, grant permissions, execute provider/model calls, migrate primary customer databases, recover real credentials or release holds.
- Preserve live data, databases, backups, keys, cached sign-ins, audit/evidence, packages and current/newest qualified builds. Clean only narrowly classified superseded compiler outputs after successful qualification and ownership checks. No arbitrary recursive cleanup, PID-only stop or ownership bypass.
- Use governed `Manage-Suite.ps1` for an authorized local lifecycle operation. A pending checkpoint with stopped writers uses the documented resume flow; do not overwrite or print resume proofs. Starting Studio does not automatically start Foundation API/worker.

Read root `AGENTS.md` and apply `.agents/skills/platform-service-architecture/SKILL.md` to application coding and review. Also read `.agents/skills/self-service-docs/SKILL.md`, `.agents/skills/platform-workflow-builder/SKILL.md`, `docs/agent-workflow-standard.md` and its blueprint when relevant. For provider work, read the orchestration/provider skills and instructions, preserving exact approval and credential routing. Guidance does not grant tools or permissions.

Pinned local SDK is `C:\IT\Package\dotnet\dotnet.exe` (10.0.300), NuGet cache `C:\IT\Package\nuget-packages`. Existing local environment helper is `artifacts/commercial-release/revision23-env.ps1`; inspect it before use. Use isolated build outputs, locked restore after reviewed graph updates, normal vulnerability auditing and the existing local test profile. Keep `PLATFORM_TEST_CODEX=0` and `PLATFORM_TEST_BICEP=0` for disconnected Portal regression unless a specific native fixture is intentionally qualified.

## Copy into the next project task

```text
Continue Platform Studio in C:\Bliobby\blob-transfer\Bicep on feature/selfservice.

Read docs/handoffs/2026-10-26-platform-studio.md, the application service architecture
plan and commercial release plan, plus applicable AGENTS.md and project skills.
Apply $platform-service-architecture as the coding architecture for application changes.
Verify current Git/source/test/runtime state before acting; dated evidence may be stale.

Continue the application-wide service migration using cohesive customer-owned services,
shared contracts and thin typed clients. Keep pipeline definitions, Bicep/parameter assets,
source generation and pipeline-facing behavior intact. Reuse the shared executor, contracts
and typed read client rather than duplicating verification in controllers or clients.

Choose the next bounded slice from the handoff after checking what is already complete.
First resolve the rejected full-profile deadline/owned-descendant handling without weakening
its safety or relabeling old results. Then prioritize a customer Portal connection and read-only jobs/evidence
journey, then remove Foundation.Host's remaining Portal executable dependency. Do not convert
desktop provider credentials into Foundation authority or silently fall back to direct execution.

Preserve dedicated-cell isolation, human approval separation, transactional audit/outbox,
execution-time authorization, actual custody leases and uncertain-outcome reconciliation.
Implement and test locally with disposable cells, synthetic signed tokens and mock payloads.
Do not deploy to Azure, run live pipelines/provider/model calls, grant permissions, migrate
primary data, adopt real credentials, activate recovered cells or release recovery holds.

Update the implementation/release plan with completed versus remaining work and exact tests.
Keep old evidence and current/newest qualified outputs; remove only proven superseded build
intermediates after successful replacement checks. Verify the manifest and unchanged pipeline/
Bicep boundaries. Report source implementation, local verification and production acceptance
separately. F0–F2 remain open until their actual exit evidence is satisfied.
```
