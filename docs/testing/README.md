# Discovery acceptance and issue log

## 5 October 2026 — local fixes and named Preview validation

D-002 is fixed and all 80 discovery regression cases now execute. D-001 has bounded metadata fallback in both portal collectors, with original DNS failures retained as incomplete evidence; live API health is unresolved. D-003 uses UseNode@1 in shared Discover. D-007 now accepts the observed constrained ADO artifact host pattern, with malicious-host/token-forwarding tests. D-008 passed a real rebuilt-app browser sequence: valid report → tampered input rejection with old report hidden → valid reanalysis with notice cleared. These are local fixes, not a repeat of the historical authenticated acceptance below. The test candidate used port 5099 and isolated cookies; the authenticated primary portal was preserved. See [current validation](../validation.md).

[Wiki](../README.md) · [Current status](../completion-status.md) · [Validation history](../validation.md)

## 4 October 2026 — authenticated discovery

**Result: live read-only discovery is usable, with open DNS coverage and test-fixture issues. This is not deployment acceptance.** Tests ran against the normal Release portal at `http://localhost:5087/`, retaining the user's Azure, Azure DevOps and GitHub sessions. No application code was changed or restart performed during this acceptance pass.

Local checkout: `feature/selfservice`, source commit `305e6e23edd5916ab1f0e77d83bc96296bf15d98`. The user merged the branch during testing. Both ADO runs used `main` commit `3d88537d72bde25cdfbdf834acccb4f980610e88` (PR 21). These are distinct source contexts. Times in the artifacts are 5 October UTC; the local test date is 4 October, America/Chicago.

Only two read-only discovery pipelines were queued. No Azure resources, tags, role assignments, provider registrations, IPAM reservations or deployment targets were changed. No ADO pipeline definitions were created or modified. ADO run records and discovery artifacts were created.

### Results

| Check | Observed result | Boundary |
|---|---|---|
| Portal Azure / ADO / GitHub connections | Passed authenticated resource, pipeline and private GitHub source reads | Separate identities; does not certify deployment permissions |
| Actual application HTTP smoke | 42 passed | Local HTTP contracts, not 42 live Azure tests |
| AHP coordination / policy checks | 7 passed | No model inference |
| Release backend regression | 271 passed, 1 optional native Codex test skipped, 0 failed | Existing Release binaries; `--no-build` |
| PowerShell service-discovery regression | Failed early in a fixture | D-002; later cases did not execute |
| Registered-subscription network discovery | Partial; 2 ARM resources; VNets, NSGs, route tables and private endpoints successfully empty | Private DNS HTTP 400; D-001 |
| Tenant-visible network discovery | Partial; 1 of 1 visible subscriptions queried | Caller-visible tenant coverage, not proof of all tenant subscriptions; management-group mode untested |
| Skill library → azure-resource-visualizer → Discover visible resources | Partial report saved; observed diagram rendered with 5 scope/container/resource nodes | Same DNS gap; this is deterministic evidence visualization, not a Codex-generated Mermaid acceptance test |
| Tag discovery and tag matrix | Succeeded; 5 items, 14 findings; unsupported resource types explicitly unknown | Subscription + 2 groups + 2 resources, not five workload resources; no tag edits |
| Private DNS / drift workspace | Partial observation; DNS-005 Unknown; missing baseline disclosed | No invented drift or assertion that failed collection means no zones |
| Private GitHub source / ADO registration inspection after merge | 25 YAML roots found; 6 existing definitions, 19 missing | Source checks passed; missing definitions remain platform setup |
| Blob copy Discover, definition 3 | [Run 25 succeeded](https://dev.azure.com/enetactgames/Enetact/_build/results?buildId=25), 2m 9s; one artifact | Queued in native ADO on main |
| Event flow Discover, definition 5 | [Run 26 succeeded](https://dev.azure.com/enetactgames/Enetact/_build/results?buildId=26), 1m 55s; one artifact | Portal Review → confirm → queue → completed/succeeded activity update verified |
| Codex live readiness | Not connected after Refresh readiness; agent execution disabled | Azure/ADO/GitHub sign-in does not connect Codex; no live AI review tested |

The subscription contains two existing platform resources (`Microsoft.AzureActiveDirectory/guestUsages` and `microsoft.visualstudio/account`) and two groups. It has no observed workload networks. Counts differ across the resource, graph and tagging views because they include different scopes and containers.

### Pipeline artifact observations

Both runs published `subscription-discovery`. Run 25's artifact listing includes `manifest.json`, `inventory.json`, `summary.md`, `README.txt` and `analysis/`. Run 26 additionally includes `prerequisite-plan.json`. Their Extensions summaries were inspected and saved locally. The first browser download attempt did not yield a file within its wait. The follow-up below successfully downloaded and verified both artifacts through the authenticated ADO API; the actual Deploy pipeline consumer remains unexecuted.

Both discovery summaries report Complete inventory with no VNets or private DNS zones. Direct DNS listing failed, but the subscription-scoped ARM resource inventory fallback succeeded. This establishes inventory, **not direct DNS API health or deployment readiness**. The portal does not yet recover equivalent inventory (D-001).

Event flow produced a Ready prerequisite resolution with proposed Create actions for a VNet, integration/private-endpoint subnets, integration NSG, six private DNS zones and links, and a Log Analytics workspace. These are saved declarations; discovery created none of them. Its provider evidence reports `Microsoft.Web`, `Microsoft.Storage`, `Microsoft.EventGrid`, `microsoft.insights` and `Microsoft.OperationalInsights` as `NotRegistered`. Provider onboarding remains a separate deployment prerequisite.

Saved-evidence analysis uses offline provenance (`UnverifiedOffline`); its Partial assessment is an expected limitation, not a failed pipeline. Raw inventory confirms `serviceConnectionQuery.status=Succeeded`, `count=0` in both runs despite successful task use of `SC-AZ-A-Bicep`. Optional connection-list scope/permissions and filters need follow-up before treating this as authoritative absence.

### Follow-up — artifact integrity, handoff and management groups

Additional tests on the same local date, without rebuilding, changing application code, queuing pipelines or changing Azure resources:

| Check | Result |
|---|---|
| Real run 25 and 26 artifact downloads | Passed using the portal-owned Azure CLI identity for ADO metadata, then anonymous signed artifact downloads without forwarding the token or following redirects. Tokens and signed URLs were not persisted. |
| `Read-DiscoveryManifest` against both exact downloaded pairs | Passed inventory SHA-256, freshness, workload/environment, subscription, connection and applicable intent/provider contracts. |
| `Assert-DiscoveryRun` against authenticated ADO run records | Passed pipeline/run/project/repository/branch/commit provenance for both artifacts. |
| Tampered copy of run 26 inventory | Appending whitespace to a separate copy caused hash rejection in the shared PowerShell reader and portal analysis. Original downloaded files preserved. |
| Portal Event flow/dev → Preview → discovery picker → Review | Run 26 appeared and Review produced main + discovery version 26 + `Preview only`. Confirmation was dismissed; Preview was not queued. |
| Same run selected for QA | Review rejected the workload/environment mismatch. |
| Portal Discovery analysis of real run 26 manifest/inventory | Report and one topology page rendered. Seventeen Create declarations remain Proposed, with runtime Unverified and offline provenance explicit. |
| Management-group descendants | Tenant Root Group resolved one visible enabled subscription and collected it. Overall Partial due to the known DNS error; membership reconciliation and caller-visibility limits retained. This does not test large hierarchies or hidden subscriptions. |
| `./scripts/Test-SelfServiceAnalysis.ps1` | 36 offline cases passed; five emitted documents passed versioned schema validation. Initial sandbox write denial was environmental; the authorized rerun passed. |

Raw artifact diagnostics also show `Microsoft.Network` as `NotRegistered`, with the subscription Enabled and supported private DNS API versions returned. This is relevant evidence for D-001, not proof that registration alone fixes every DNS failure. No provider was registered.

Evidence: `artifacts/discovery-acceptance/2026-10-04/live-artifact-verification.json`, `run-25/`, `run-26/`, `eventflow-handoff-review.json`, `tamper-rejection.txt`, `live-artifact-analysis-ui.txt`, `rejected-analysis-ui.txt`; management-group report `artifacts/portal-network/d331f81838004a5caa406d47b293d2c0/report.json`; portal analysis `artifacts/portal-analysis/8b9738db01334d4fac24cc44315d5713/report`; offline cases `artifacts/analysis-tests/run-3yTvns`. The diagnostic download helper under the ignored evidence directory is session-specific, not a production workflow.

### Open issues and follow-up

| ID | Priority / category | Reproduction and actual result | Expected outcome / acceptance for a fix |
|---|---|---|---|
| D-001 | P1 — incomplete live DNS evidence | In Network discovery, run registered or tenant-visible scope; also run Skills → visualizer discovery, or DNS & drift → collect/assess. Zones return HTTP 400 and coverage stays Partial/Unknown. ADO discovery recovers inventory through ARM fallback. | Diagnose the Azure response and align shared collectors. Preserve original API failure and explicit provenance if inventory fallback succeeds. An empty fallback must not certify records, links, reachability or permissions. Add regression cases for empty, denied, failed and partial collections; rerun all affected live views. Root cause is not established by this test. |
| D-002 | P1 — regression harness blocked | Run `./scripts/Test-ServiceDiscovery.ps1`. Case `service connection discovers its active subscription from a disabled placeholder` fails because its temporary fixture lacks `scripts/data-platform-common.ps1`. The helper-copy list is in `scripts/Test-ServiceDiscovery.ps1`, around line 136. | Include the real transitive helper dependencies in the fixture, then run the full discovery suite. Do not remove the test or claim unexecuted cases passed. This is a fixture failure, not demonstrated production collector failure. |
| D-003 | P3 — deprecated ADO task | Both live runs show two warnings for `NodeTool@0` and recommend `UseNodeV1`. | Migrate the owning shared template to the supported task, validate generated/root YAML contracts, and rerun Discover. No current run failure. |
| D-004 | Setup — missing definitions | ADO setup → Check registration after merge reports 19 missing and 6 existing definitions. | Review/register the required missing definitions, then test their own workflow contracts. Registration alone does not qualify execution or grant service-connection access. No registration done in this pass. |
| D-005 | Acceptance — Codex disconnected | Agent workflows → Refresh readiness displays Not connected and disables Run. | Complete Codex sign-in in this portal session, verify the pinned model/effort/speed, and run a bounded visualizer review; inspect evidence citations, Mermaid validation/rendering and receipt. |
| D-006 | Investigation — optional service connection inventory | Both Extensions summaries report no matching connections although AzureCLI authenticated through the configured connection. | Inspect `serviceConnectionQuery`, optional ADO identity/visibility and filters in the downloaded inventory. Display skipped/denied/empty distinctly. Do not infer that the connection used by the task is missing. |
| D-007 | P2 — artifact host compatibility risk | Authenticated ADO signed-content metadata for both discovery artifacts returns `artprodeus21.artifacts.visualstudio.com`; bounded anonymous downloads succeed. `PreviewDiagram.DownloadUri` currently permits only `.vsblob.vsassets.io` and `.blob.core.windows.net`, so it rejects this host. | Qualify legitimate ADO artifact hosts without forwarding bearer tokens or allowing arbitrary redirects. Add fixed-host and malicious-host cases, then test a real Preview/IaC artifact through the portal. The host mismatch is confirmed; an actual Preview artifact using that host is not yet tested. Discovery Review does not download artifacts. |
| D-008 | P2 — stale analysis remains visible after rejection | Analyze valid run 26 input, replace inventory with a modified copy and analyze again. The error correctly reports rejected input, but the previous report and diagram remain visible with their old saved location. | Clear or prominently label the previous result while a new request runs and after rejection. Bind displayed evidence to the accepted input; add a successful-analysis → failed-analysis browser regression. No invalid report was accepted, but the UI can mislead. Re-analyzing the restored valid pair produced a new report while the prior error banner remained visible; clear stale notices on success too. Screenshot: `artifacts/discovery-acceptance/2026-10-04/artifact-analysis.png`. |

Before merge, registration showed 14 missing definitions and five unpublished YAML roots. A fresh check against the merged SHA found all five sources and 19 missing definitions. The unpublished-source observation is resolved, not an outstanding bug. Portal requests still select `main`; feature-branch testing is a separate capability boundary.

### Repeat the local checks

Run from the repository root while the normal portal is available:

```powershell
node tests/portal/smoke.mjs
./scripts/Test-AgentHost.ps1
dotnet test tests/SelfService.Portal.Tests/SelfService.Portal.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=discovery-acceptance.trx' --results-directory artifacts/discovery-acceptance/2026-10-04
./scripts/Test-ServiceDiscovery.ps1
```

`--no-build` reruns the current binaries. For code fixes, rebuild the real application through the documented lifecycle before acceptance; preserve or re-establish browser-session authentication as required. The final command is currently expected to reproduce D-002.

For live retesting, refresh Connections, use returned subscription choices, collect the same network/tag/DNS/skill reports, and compare explicit collection coverage. Queue only the dedicated Discover operation on the intended source commit. Verify run status, artifact file list, Extensions summary, then download/validate raw manifests and exercise their reviewed consumer separately.

### Retained evidence and remaining scope

Local evidence is ignored by Git and is not shipped in a fresh clone:

- `artifacts/discovery-acceptance/2026-10-04/`: backend TRX, registration before/after merge, network/tag/DNS/drift/skill UI text, Event flow reviewed request, both ADO Extensions summaries and portal activity screenshot.
- `artifacts/portal-network/a52194d2c96241038c8f52b5a6bcdd53/report.json`: registered scope.
- `artifacts/portal-network/5ef7dc9def9f4737b5b0a2e5a26aabe5/report.json`: tenant-visible scope.
- `artifacts/portal-discovery/bfab0669fa6d4ae6a18d5616d0b16939/report.json`: visualizer skill evidence.
- `artifacts/portal-tags/fbf3d0db9dcb4695b9869fdf90bdee80/inventory.json`: tagging evidence.
- `artifacts/portal-tests/http-smoke.json`: real HTTP checks.

Still unverified here: nonempty network/nested-property probes, large management-group hierarchies, multi-subscription pagination/denial, effective connectivity, live Codex inference, actual Deploy pipeline consumer execution, Azure What-If, deployment, recovery and the 19 unregistered pipeline definitions. Raw artifact integrity and a one-subscription management-group traversal are now verified. Do not extrapolate an almost-empty subscription's discovery success into enterprise or runtime acceptance.
