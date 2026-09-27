# Validation evidence

## Bulk ADO pipeline registration: 27 September 2026

The [ADO setup page](ado-pipeline-registration.md) covers all **18 root pipeline entry points**. The Release build completed with zero warnings/errors. **130 backend tests passed, one optional native Codex test skipped, zero failed**, including 18 new registration cases (`artifacts/test-results/pipeline-registration.trx`). Synthetic ADO/GitHub responses exercise create-all-missing, preservation/conflicts, incomplete inventory, source/review drift, concurrent creation, lost responses, partial batches and token/secret isolation. These tests do not establish live ADO registration acceptance.

Both Windows packages in `artifacts/portal-packages/20260927-155358/` passed **29 localhost HTTP checks and five AHP coordination checks** each, without cloud calls. ARM64 ran natively; x64 ran under Windows ARM emulation, not native x64 hardware. Package checks stopped their test processes. The source portal remains running on localhost 5087 and requires fresh browser authentication after its restart.

Codex browser verification showed the real 18-entry setup page and its disconnected-ADO error. A separate, explicitly labeled localhost fixture exercised status selection, the review dialog and a synthetic completed registration receipt with zero queued runs. The fixture was stopped afterward; it is not the runtime backend. Screenshot: `artifacts/pipeline-registration-ui.png`.

An anonymous, read-only GitHub check observed public `Enetact/Bicep` main at `bab03bf3c93745f61be9a69e6bca4d9a6a518fc3`: **17 of 18** local root YAML blobs matched; `azure-pipelines-network.yml` differed. Registration will require that missing definition's exact source to be published, or the local/package copy reconciled. This is a point-in-time comparison, not an assurance about later main revisions.

Also passed: catalog generation check (28 registered selections), **133 pipeline/infrastructure contracts across 32 YAML files**, and repository hygiene (**15 ignore probes, 183 source/control paths**). No ADO server expansion, live authenticated inventory, definition creation, pipeline run, Azure change, GitHub write, target enablement or permission change was performed. Public-repository protection and full-history secret clearance are outside these targeted checks. The ordered manual acceptance procedure is in the setup guide.

## Network discovery, diagrams and AVNM source delivery: 27 September 2026

The bounded [network/diagram implementation](network-discovery-and-diagrams.md) was checked locally. **112 backend tests passed, one optional native Codex test skipped, zero failed** (`artifacts/test-results/network-increment.trx`). New coverage includes untrusted diagram rejection, resource/relationship references, tenant filtering, denied/unsafe scope reads, management-group exclusions, prefix arithmetic/overlap and ADO review expiry/configuration drift/single-use/lost-response handling. Tests use synthetic provider responses; they are not Azure or Codex acceptance.

Also passed: **seven topology tests**, **42 skill definitions/944 vendored hashes**, **16 offline allocation helper/parser cases**, **133 infrastructure contracts with 32 YAML files parsed**, and compilation of both the AVNM static-CIDR module and subscription network composition. YAML checks are local; ADO server expansion was not performed. No live pool, reservation, Deployment Stack, model call or ADO run was created.

The source portal was restarted from Release source and left running on localhost 5087. Codex browser checks verified the real Network discovery menu, disabled reservation configuration and clear disconnected-Azure error. An isolated, visibly labeled local fixture verified network evidence handoff, accepted graph rendering, partial coverage and rejected-output text-only behavior. The fixture was stopped after verification; it is not the portal's runtime backend. Visual evidence: `artifacts/network-diagram-browser.png`.

Windows packages under `artifacts/portal-packages/20260927-135312/` each passed **26 HTTP checks and five AHP coordination checks** without cloud calls. ARM64 ran natively; x64 ran under Windows ARM emulation, not on native x64 hardware. Package documentation snapshots were refreshed after these executable checks. The current portal/package source adds no new .NET dependency: SDK 10.0.300, existing locked packages, PowerShell 7.6.2 and Node 24.16.0 were used.

Remaining acceptance: real browser consent and cross-subscription visibility, live Codex output/downloads, ADO definition/environment/check configuration, real AVNM pool/permissions and reservation/reconciliation/network creation. The current spoke profile is create-only; effective DNS/routing, automated workload admission/binding and release/resize/renumbering are outside this delivery. All workload targets and the allocation profile remain disabled. Follow the ordered manual runbook before enablement.

## Source and runtime hygiene audit: 27 September 2026

Expanded `.gitignore` for Codex/Azure/MSAL credential files, repository-local Codex state, private certificate/key files, test evidence and editor files. Preserved example settings, project instructions, source and fixtures. `./scripts/Test-RepositoryHygiene.ps1` passed **15 ignore probes and 169 source/control-path checks** with no tracked ignored files, portal test-path references or test dependencies in the available Release dependency graph.

Read-only process inspection matched the portal ownership receipt to the live Release assembly and executable on localhost 5087. Ports 5088 (package test) and 5098 (diagram fixture) had no listeners; no diagram fixture process was found. MSAL and ModelContextProtocol were in the application's dependency graph; test-host/xUnit dependencies were absent. No source relocation was needed: application code, first-party analyzer, Bicep modules, catalog/skills and tests already have separate authoritative locations. The audit left the portal running and did not rebuild, stop services, inspect token contents or invoke a model.

The user reported Codex connected. At this inspection the portal had no active Codex child or agent process receipt, so portal-level authentication could not be confirmed; this does not establish the state of a separate Codex desktop login. Model/provider acceptance remains distinct from the source/runtime audit. This is not a full-history secret scan or production-readiness certification.

## Codex agent workflows: 27 September 2026

Implemented four bounded reviews with GPT-6 Astra / High / Standard, isolated ChatGPT sign-in, scoped MCP tools and an experimental AHP coordination profile. **85 backend tests passed, zero failed/skipped**, including the explicitly enabled native Codex 0.153.2 startup, account-read, inherited-MCP check and login-start/cancel protocol without completing login or invoking a model. Real in-process MCP tests cover isolated evidence, revoked sessions, scope/tool rejection and call budgets. Resource-group route and input validation tests passed. Evidence: `artifacts/test-results/portal.trx`.

The unchanged diagram suite passed **six tests**, and all **944 vendored source hashes** passed. The running source portal passed **24 actual HTTP checks** and **five AHP WebSocket coordination checks**. A fresh in-app browser tab showed the four workflows, fixed model settings and disabled Run before required authentication; the older tab had stale initialization state. No Azure resource changes, ADO queue requests, credentials entry, completed ChatGPT login or inference occurred. Model-generated reviews, native x64 hardware, live consent/permissions and Azure/ADO acceptance remain unverified. See [agent contracts and remaining validation](agent-workflows.md).

Rebuilt packages: `artifacts/portal-packages/20260927-124510/win-arm64.zip` and `win-x64.zip`. Each published folder passed 24 HTTP and five AHP checks. ARM64 ran natively; x64 ran under Windows ARM emulation. SHA-256: ARM64 `21CE5074412C6D853B2DA61FE6BBF0ED40FBEF416EF23A2C8A0BB541210A9C54`; x64 `2354F7797A8B0DB7AF67AC6D36E3123DA01BDA5D7A1B3F7422667014570A8695`. These packages contain the agent implementation; the latest source validation documentation additionally records their post-publish checks.

## Network discovery scope review: 27 September 2026

Verified the current portal and ADO single-subscription boundaries against source, the pinned Microsoft skill guidance and Microsoft Learn. All **17 existing AzureDiscoveryTests passed** using the prior qualified build and mocked HTTP/tokens; evidence is `artifacts/discovery-scope-audit/discovery-scope.trx`. No application code changed, no full-suite rerun occurred and no tenant, management-group or MCP live scan was performed. The [scope assessment](plans/tenant-network-discovery.md) records the proposed UI, Resource Graph collector, optional MCP analysis, identity separation and acceptance requirements. This review does not implement or enable broader discovery.

## Five-workload expansion: 27 September 2026

Added Private Storage Workspace, Key Vault, Observability, HTTP Functions API and Service Bus worker. The source catalog now has seven types, 28 disabled profiles and fourteen dedicated Discover/Deploy roots. This is local implementation and qualification, not Azure acceptance. The working tree extends base commit `6e36a5822d0d3f7c26433ff3c58fa8a2dc15b3ed`; changes were not committed or pushed by this task.

The final complete `scripts/Test-Project.ps1` run passed (log: `artifacts/product-full-verification.log`), including **91 product contracts**, **10 ProductFunctions tests**, **46 portal tests**, **80 discovery cases**, the existing cost/platform/stack/Preview/Event flow/prerequisite checks, and **131 pipeline/infrastructure contracts across 31 YAML files**. The original Blob transfer tests passed 18 cases with 15 opt-in emulator cases skipped. All seven workloads' Bicep compositions/wrappers and environment files compiled. The operator tool built with zero warnings/errors. Advisory queries reported no vulnerable BlobTransfer or ProductFunctions dependencies. Azure calls in adapter tests were mocked; no Azure deployment or ADO server-side expansion was performed. Infrastructure-only bundle roundtrips, tamper rejection, cross-product scope, missing/failed prerequisites, network drift, flat What-If change serialization and strict runtime message handling are covered.

Actual Functions ZIP creation and an infrastructure-only release receipt were verified under `artifacts/product-releases/*/local-validation-20260927`. These receipts correctly mark the local working tree dirty and cannot be used as qualified deployment artifacts. Final portable packages at `artifacts/portal-packages/20260927-105645` each passed **14 real localhost HTTP checks**; ARM64 ran natively and x64 under ARM emulation. Native x64 hardware remains untested. Browser review confirmed seven distinct cards, scoped summaries/cost caveats, saved-discovery Preview selection and disabled Deploy. Screenshot: `artifacts/seven-workload-portal.png`. Live Entra consent and ADO execution remain unverified.

Onboarding values, reviewed prices, private connectivity, registered ADO definitions and each product's live positive/negative/recovery/alert or DLQ acceptance remain required. See [workload onboarding](workload-onboarding.md). No targets, permissions or Azure resources were changed. Older records below retain their original scope and counts.

## Bundled Azure Skills and browser discovery: 27 September 2026

The portal catalog now includes 42 pinned Microsoft definitions and five project skills. `scripts/Test-Portal.ps1` passed **41 backend tests, zero failures/skips**, including fixed ARM discovery, scope rejection, safe pagination, filtered metadata, successful empty results and partial/failed reads. `tests/portal/verify-skills.mjs` verified all 42 index entries and **944 source-file hashes**. Azure responses in backend tests were mocked; no subscription was queried.

Both Windows packages at `artifacts/portal-packages/20260927-084842` launched and passed **14 real local HTTP checks each** with `scripts/Test-PortalPackage.ps1`. ARM64 ran natively and x64 under Windows ARM emulation; native x64 hardware remains untested. All 944 bundled source hashes were also verified inside each ZIP. The unconfigured portal rejects discovery without Azure sign-in. Browser inspection confirmed filtering, **No pipeline associated yet**, the networking coverage explanation and the disabled unauthenticated discovery control; the screenshot is `artifacts/portal-tests/azure-skills.png`.

The source portal is running on localhost:5087 for review. Live Entra consent, Azure collection and ADO execution remain unverified; the separate portal registration is required. No deployment targets were enabled, cloud writes executed or upstream skill scripts run. This targeted increment does not repeat the full application/infrastructure suite. See [implemented discovery scope](azure-skill-discovery.md). The earlier portal baseline below remains historical.

## Saved-discovery analysis implementation: 26 September 2026

Reviewed documentation/plan scope and repository placement, then implemented the first offline reporting contract, compatibility readers, safe renderer, shared Discover report step and four project-local review skills. `scripts/Test-Project.ps1` completed successfully: **36 new offline analysis cases**, **5 output-schema validations**, **46 pipeline/infrastructure checks across 20 YAML files**, the existing tooling/manifest/self-service/discovery/cost/platform/stack/preview/Logic App/prerequisite suites, both workloads' environment/stack compilations, and **18 application tests passed / 15 opt-in emulator cases skipped**. The operator tool built without warnings/errors; the Function dependency advisory query reported no vulnerabilities. This run did not start the Functions/Azurite stack or execute Azure/ADO services.

New analysis cases cover supported manifest versions, successful empty versus failed/truncated/count-inconsistent collections, stale/future evidence, selection/subscription mismatch, byte tampering, ownership contradictions, resource bounds, unsafe IDs, secret-bearing raw fields, unrelated ARM resources, paginated diagrams, CLI evidence preservation and schema output. Evidence: `artifacts/analysis-tests/run-FEhjMA/results.json`; application TRX and vulnerability report are under `artifacts/test-results/`. Existing mocked producer output was also consumed successfully by the real new report command at `artifacts/analysis/producer-compatibility/`.

Four new `SKILL.md` files passed the bundled skill validator using isolated PyYAML 6.0.3 under ignored `.tools/skill-validation`. The local request-design command resolved the current Blob copy example with deployment disabled. A synthetic SVG was rasterized with the bundled Sharp library and visually inspected: labels and observed/proposed containment were readable. All four emitted Mermaid documents parsed with Mermaid 12.0.0 in strict mode, using isolated validation dependencies under `.tools/analysis-render`; this does not add Mermaid to the report's runtime dependencies. Live ADO summary rendering, authenticated handoff of a real run, private runtime acceptance and all allocation behavior remain unverified. Full-suite success is not evidence for those gates.

The [progress record](plans/implementation-progress.md) and [analysis guide](self-service-analysis.md) describe the delivered subset and exact commands. Existing dated evidence below is retained as history.

## Documentation and catalog audit: 26 September 2026

Re-traced both allowlisted workload adapters, all eight disabled target profiles, the four dedicated menu roots, generic compatibility routing, hosted Preview and protected Deploy jobs, prerequisite decisions, Template Spec/stack lifecycle, and artifact/readiness contracts. Added the catalog/method guide, documentation index and proposed expansion plan. Corrected stale single-workload, four-target, generic-only setup, resource-group precreation and publication-preview descriptions. Older evidence below remains historical.

Fresh checks passed: catalog freshness for **8 target selections**, **46 pipeline/infrastructure contracts across 20 YAML files**, and local Markdown file/anchor validation. No application suite, emulator, hosted workflow, Azure operation or ADO run was executed for this documentation-only change. The machine-readable file now has `currentDocumentationReview`; its older sections retain their original snapshots and scope. The expansion plan describes proposed work, not additional registered products.

## Event flow dev onboarding authorization: 19 September 2026

The operator supplied service-principal object ID `2b7a2791-e7d6-4181-9db3-1bee486236d0` and application ID `4749bb42-f74e-48fd-aa30-82bcec17a483`, requested generated missing dev settings, and authorized configuring both named exceptions. Dev now uses generated tags `enetact-dev` / `dev-poc`, the object ID for role assignments, and HTTPS references to the checked-in [dev review record](reviews/eventflow-dev-exceptions.md). Those GitHub links become available when the record is merged to main. No external approval ticket, finance-system code or verified Entra identity relationship is claimed.

Targeted verification passed **26 prerequisite contracts**, **50 Logic App contracts** and **22 preview contracts**. The real dev parameter file compiles and, with a saved successful-empty discovery plan, has zero onboarding findings and passes `Assert-LogicParameters`. Negative cases still reject missing ownership/identity/approval values; the unchanged QA file still reports all 14 onboarding blockers. Tests also compile the stack template and exercise existing security gates. Evidence: `artifacts/prerequisite-tests/d3dfc838f2f74649bff26b596739e7b1/results.json`, `artifacts/logic-tests/1947eaddc8944800bf9aadeb73c04496/results.json`, `artifacts/preview-tests/ebe7bf4d8a2646f8a63db0ca02ef65bf/results.json`.

This was local configuration and mocked validation only. No Azure sign-in, permission grant, native What-If, deployment or ADO run was performed. Merge the configuration, review document and updated source manifest together, then rerun Event flow Preview with matching complete discovery. Target enablement and actual private-agent/platform readiness remain separate requirements. Earlier validation entries below describe their dated source state.

This records the external-uploader design with the ledger co-located in solution storage. No Azure login, ARM deployment, Azure upload, role assignment, or live Azure test was performed while producing this bundle.

## Event flow discover-or-create prerequisites: 19 September 2026

Complete scoped discovery now saves an integrity-protected prerequisite plan. An empty successful inventory plans a VNet, two subnets, integration NSG, six private DNS zones/links and a workspace in the workload RG. Compatible selected shared resources are Reuse; existing stack-owned resources remain Manage. Failed reads, ambiguous/missing explicit selections, address conflicts, tampered plans and unowned resources appearing after discovery block the flow. Preview reports decisions before onboarding checks; actual resource creation remains in the protected workload stack apply. See [the runbook](prerequisite-resolution.md).

The final `Test-Project.ps1` run passed **333 PowerShell contracts** (including **25 new prerequisite cases**, **50 Logic App cases** and **22 preview cases**), **46 pipeline/infrastructure checks across 20 YAML files**, and **18 application tests**. **15 opt-in emulator cases were skipped**. Both workload wrappers, all eight environment configurations and platform templates compiled. The operator build had zero warnings/errors; the Function dependency advisory query reported no vulnerabilities. An initial full run exposed a test mock leaking into later compilation; scoping the mock to its test script fixed this, and the complete suite then passed.

New cases exercise successful-empty and failed discovery, shared reuse, partial existence, stable stack ownership, overlap rejection, selection drift, explicit parameter protection, live resource collisions, planned Foundation absences and the actual discovery entrypoint with a mocked Azure CLI. Compiling the checked-in dev values and applying the empty-inventory plan resolves nine resource-ID onboarding blockers; five real-value/review blockers remain: owner, cost center, deployment principal object ID and both exception approvals.

Evidence: `artifacts/prerequisite-tests/a6f698102d09449ead45e24d491e6264/results.json`, `artifacts/logic-tests/d056e4c8f88f486dacbcb92377e79b5c/results.json`, `artifacts/preview-tests/16cd8209fca441dcb3aa10ae13259a25/results.json`, and `artifacts/test-results/pipeline-structure.json`, `unit.trx`, `vulnerabilities.json`. These are local and mocked contract results, not live deployment evidence. No Azure sign-in, native Azure What-If, publication, deployment or ADO run was performed. Private-agent routing/DNS, platform approvals, provider/permission/quota readiness and live Azure acceptance remain outstanding. Rerun Discover after checking in the changes; older artifacts cannot activate automatic creation.

## Preview run 20 onboarding blocker: 19 September 2026

The supplied log confirms all 206 source-manifest entries passed, discovery run 18 downloaded and passed provenance checks for subscription `f4f2eafe-2512-4c2f-9b5b-c88f6767e778`, and Bicep preparation reached Logic App parameter validation. It stopped on onboarding placeholders before the AzureCLI What-If task. Six files were published as `deployment-preview`; publication does not imply a successful resource-change plan.

The checked-in Event flow profiles still require real environment configuration. Validation now reports missing/placeholder field paths and both pending platform reviews together, with the selected parameter-file path, rather than a generic first error. Preview retains this checklist as `onboarding-requirements.json` and renders it in the README. Values are not invented, shared infrastructure is not automatically provisioned, and deployment enablement/approval guards remain in effect.

Targeted verification passed **50 Logic App contracts** and **22 preview contracts**, including compilation of the checked-in dev parameters and detection of all 14 onboarding blockers, safe diagnostics for absent/blank values, approval validation and blocked-report rendering. No Azure evaluation or deployment was performed locally. Actual environment values, shared resources and platform review decisions remain necessary to unblock Preview.

## Preview run 17 source-manifest failure: 19 September 2026

The supplied ADO log shows run 17 checked out main commit `3f60541e9bb2402ef4af77f1a6445c7ddd5b07c2`, installed Bicep successfully, then failed the source-manifest check before discovery download and Azure validation. The one-file `deployment-preview` artifact was the initial README; it contained no resource-change result.

The local source commit `cd86ecd` reproduced a manifest-versus-Git-blob mismatch for `self-service/pricing/usd-eastus2.json`: the manifest matched mixed local line endings while the committed JSON used LF. The run's merge commit was not available locally, so additional differences in that exact commit were not assessed. The pricing data was preserved; local line endings were normalized and the manifest regenerated.

Manifest generation/checking now follows Git's effective `eol=lf` rule for text, preserving all other bytes and exact binary hashes. Failures list changed, missing or unlisted paths without regenerating anything in CI. Preview records tooling/manifest failures in its published Markdown. **9 manifest contracts** passed, including a fresh Git clone, mixed endings, non-ASCII text, explicit CRLF, binary integrity, changed/missing/new files and malformed records. **21 preview contracts** and **46 pipeline/infrastructure checks** also passed, including an executed tooling-failure README fixture. Evidence is under `artifacts/tooling-tests/manifest-*`, `artifacts/preview-tests/` and `artifacts/test-results/pipeline-structure.json`.

No Azure calls or ADO rerun were performed for this fix. Merge the corrected source and manifest, then queue a fresh Preview-only run. Other environment/discovery/permission prerequisites still apply.

## Two-stage workload Preview and Deploy: 19 September 2026

Dedicated Blob copy/Event flow Deploy menus now default to **Preview only**, consume their selected Discover artifact and publish a resource/property-change README in Summary / Extensions and `deployment-preview`. **Preview and deploy** adds the protected second stage with application qualification, exact input matching, Template Spec publication, full-preview drift recheck and existing Foundation/Release apply/readiness operations. Targets remain disabled; configured disabled targets can preview. Original generic menu behavior is preserved.

`Test-Project.ps1` passed **227 existing contracts**, **46 Logic App contracts**, **20 initial preview contracts**, **45 pipeline/infrastructure checks across 20 YAML files**, and **18 application unit tests**, with **15 opt-in emulator cases skipped**. Both workload environment sets, stack wrappers and platform templates compiled; operator build had zero warnings/errors; the dependency advisory query reported no vulnerabilities. A subsequent targeted run passed **21 preview contracts**, adding failed-rerun cleanup coverage, and repeated pipeline checks after the final Preview tooling/source-check step.

New mocked contracts cover full-release parameters for both adapters, local-template What-If without publication/apply, discovery/input integrity, source/release mismatch, 24-hour expiry, other-run rejection, disabled-target separation, failed/deletion/uncertain report retention, nested property changes/redaction, missing Azure results, successful/failing apply sequencing, drift before workload writes and stale-success cleanup. Evidence: `artifacts/test-results/workload-preview.json`, `pipeline-structure.json`, `deployment-stacks.json`, `unit.trx`, and per-suite folders under `artifacts`. The pipeline checker is a bounded local template-expression validator, not ADO server compilation.

No ADO pipeline was queued, Azure account authenticated, real What-If run, Template Spec published or workload deployed during this change. Native run UI rendering, permissions and actual Azure acceptance remain outstanding. Real environment values and preview permissions are required before an Azure report can be generated; placeholder configurations are expected to block.

## Separate native workload menus: 19 September 2026

Added four generated manual roots: Blob copy Discover/Deploy and Event flow Discover/Deploy. Each fixes its workload type and restricts instance/options/help to that pattern. Deploy resources bind to distinct configured Discover definitions. Original generic roots remain compatible but omit both resource-summary panels. The shared protected deployment/catalog templates and target enabled flags are unchanged.

Targeted validation passed **45 pipeline/infrastructure checks** across **19 YAML files**, including unrelated-summary exclusion, fixed type binding, correct discovery source, foreign-instance rejection and expansion parity with original routing for all eight targets. **80 discovery/catalog/network checks** passed, including generation/freshness and dedicated versus legacy handoff summaries. Evidence: `artifacts/test-results/pipeline-structure.json` and `artifacts/discovery-tests/dd4d6416415045acb5c3f5c0af406a94/results.json`. No application/emulator rerun was needed for this menu change. Source manifest and whitespace checks passed.

No remote ADO definitions were created, UI rendered, resource authorization performed or Azure resources deployed. Register the exact four names/paths documented in [self-service](self-service.md#register-the-new-definitions-in-ado), creating Discover definitions before their matching Deploy definitions, and run fresh discovery. All targets remain disabled pending onboarding.

## Workload run-menu clarity: 19 September 2026

Discover and Deploy now use descriptive workload choices with resource names. Deployment reference fields separately describe each pattern's resources, existing dependencies and cost assumptions. Discovery clearly creates no resources; deployment guidance reflects catalog enablement. Display values are normalized to canonical IDs before protected template routing. Existing resource configuration, protected bindings and enabled flags are unchanged.

Targeted validation passed **35 pipeline/infrastructure checks** (`artifacts/test-results/pipeline-structure.json`) and **78 discovery/catalog/network checks** (`artifacts/discovery-tests/fd79c41f0f60475e8bd970adb579141c/results.json`). All 15 YAML files parse and all eight selections resolve correctly. This was a menu/generator change; application/emulator suites were not rerun. No ADO server expansion or live menu rendering was performed. Publish the branch changes and reopen Run pipeline to inspect the actual updated form. Both pattern summaries remain visible; no dynamic dependent help panel is claimed.

## Second workload and typed platform flow: 19 September 2026

Implemented Logic App Standard + Event Grid Basic through the queue bridge described in the [workload runbook](../workloads/logic-app-event-grid/README.md). Both compositions use local modules, content-hashed Template Specs and independently owned Deployment Stacks. All eight targets remain disabled.

The final `Test-Project.ps1` run passed **225 existing contracts**, **46 Logic App contracts**, **34 pipeline/infrastructure contracts** and **18 application tests**, with **15 opt-in emulator tests skipped**, zero failures. All eight environment parameter sets, both stack wrappers and the optional Policy/registry templates compile. All 15 YAML files parse; both embedded wrapper compositions and complete parameter/output forwarding match. Operator build: zero warnings/errors. Function dependency advisory check: no reported vulnerabilities.

| Suite | Evidence |
|---|---|
| Tooling: 19 | `artifacts/tooling-tests/db021c020bdd4b98b5f4fdfd864088ea/results.json` |
| Self-service: 42 | `artifacts/self-service-tests/029317e595564317b9acfdce66637d05/results.json` |
| Discovery: 78 | `artifacts/discovery-tests/8a2a4a444d6f49b5b0543f01d5ef1729/results.json` |
| Costs: 15 | `artifacts/cost-tests/cf511899c7f74497811e89c1a3b0bea3/results.json` |
| Platform: 37 | `artifacts/platform-tests/0618c6cf15e546ff9e466731cabe813b/results.json` |
| Stacks: 34 | `artifacts/stack-tests/ab8d6b7645ab4c6e9e2f906b3409d024/results.json` |
| Logic App: 46 | `artifacts/logic-tests/1f8c6c84566d443dacd15ad156ef4d05/results.json` |
| Pipelines: 34 | `artifacts/test-results/pipeline-structure.json` |
| Application: 18 passed / 15 skipped | `artifacts/test-results/unit.trx` |

New contracts cover typed intent/provenance, effective shared-resource inventory, schema, package traversal/tampering, scoped public-storage exception handling, cost floor, one-event JSON batch serialization, runtime-key evidence redaction and mocked Foundation/Release failures. Package bytes were built locally; clean-source release qualification is not claimed for this dirty checkout.

No actual Logic Apps host, Event Grid delivery, Azure sign-in/publication/deployment, ADO server expansion or UI authorization was exercised. The ordinary test run did not rerun the existing opt-in Functions/Azurite integration cases. Azure private runtime/mounts, expression execution, RBAC propagation, SCM indexing, duplicates/quarantine/dead letters and alert notifications remain acceptance gates. Only public documentation and retail price endpoints were read.

## Shared qualification and pipeline evidence refactor: 19 September 2026

The [full flow guide](pipeline-flow.md) records the three stable manual entry points, six enabled deployment stages and local-module/Template Spec/Deployment Stack boundaries. Build and Deploy now share qualification, cancellation cleanup and complete evidence collection. Azure stages prepare context before download/sign-in, and disabled setup publishes current onboarding guidance. Module registry setup/publication is deferred.

`Test-Project.ps1` passed **225 existing offline contracts**, **20 pipeline/infrastructure contracts** and **18 application tests**; **15 opt-in emulator tests were skipped**. All four environments, stack wrapper and the two optional platform templates compiled. The operator build reported zero warnings/errors, and the Function dependency advisory query reported no vulnerabilities. All **14 YAML files** parse. No new actual local-host/emulator run, ADO server expansion, Azure publication or deployment was performed.

The pipeline checks expand nested steps and verify Build/Deploy qualification parity, handoff-before-build, success-gated packaging, unconditional cleanup, all-suite evidence retention, evidence-before-sign-in and the protected six-stage artifact chain. Isolated PowerShell fixtures verify that early failures retain context without a readiness receipt and that evidence collection works before tests start or with partial results.

| Suite | Evidence from this run |
|---|---|
| Tooling: 19 | `artifacts/tooling-tests/15a8da28223c4680a02ebeb400dd8bbd/results.json` |
| Self-service: 42 | `artifacts/self-service-tests/6e9579c27f544f79847bfc25bd4fc5db/results.json` |
| Discovery: 78 | `artifacts/discovery-tests/99d3484dd11e4f8a9c97f109726c30d4/results.json` |
| Costs: 15 | `artifacts/cost-tests/e82c0ca72c9749b9ae39a9d8514a54ac/results.json` |
| Platform: 37 | `artifacts/platform-tests/fb52225baf29492dbce01b1570987aeb/results.json` |
| Stacks: 34 | `artifacts/stack-tests/62978b7fbc4f4da98ee89fd3c492eef0/results.json` |
| Pipelines: 20 | `artifacts/test-results/pipeline-structure.json`; isolated fixtures under `artifacts/pipeline-tests/` |
| Application: 18 passed / 15 skipped | `artifacts/test-results/unit.trx` |

All targets remain disabled. Cleanup/evidence steps have a five-minute cancellation allowance but cannot guarantee completion if a job never starts or its agent is lost. Live ADO expansion/authorization and Azure acceptance remain required. Older entries below preserve their historical counts and evidence.

## Microsoft-aligned repository layout: 19 September 2026

The [repository conventions](repository-structure.md) record the Microsoft Learn/AVM sources, chosen workload/resource-module layout, environment/stack configuration and path migration. Twelve Bicep/parameter sources moved; all root ADO entry filenames remain stable. Root lint now also rejects unused variables and secret-bearing outputs. The build pipeline installs Node before the new locked YAML validation step.

The final `Test-Project.ps1` run passed **225 existing offline contracts**, **16 new pipeline/infrastructure contracts**, and **18 application tests**, with **15 opt-in emulator tests skipped**. All four environments, the stack wrapper, Policy and registry templates compiled successfully. The operator build had zero warnings/errors. The Function dependency advisory check reported no vulnerabilities; it is not an npm tool audit.

| Suite | Current evidence |
|---|---|
| Tooling: 19 | `artifacts/tooling-tests/98cac48e335b4b68959b10f64df0e003/results.json` |
| Self-service: 42 | `artifacts/self-service-tests/180d4a4048cd4f85a510ef02f18a381c/results.json` |
| Discovery/catalog: 78 | `artifacts/discovery-tests/49af7c29bd8c49f2bf9064ca785abbb7/results.json` |
| Costs: 15 | `artifacts/cost-tests/80a2e142dd674cb0bf3b2b48e91a5087/results.json` |
| Platform contracts: 37 | `artifacts/platform-tests/32a90fe384e34cfe92fe45cbf5517147/results.json` |
| Stacks: 34 | `artifacts/stack-tests/6c9f66fc6317476dbeebd455759e892c/results.json` |
| Pipeline structure: 16 | `artifacts/test-results/pipeline-structure.json` |
| Compiled migration parity: 6 | `artifacts/test-results/repository-layout-comparison.json` |

The migration comparison uses pre-refactor compiled resource-group/stack templates and all four parameter files. After removing only ARM-template and parameter/output authoring metadata, the before/after structures are equal. Resource names, types, scopes, dependencies, properties, outputs and environment values were compared. Full template hashes can change because descriptions/metadata changed. This local equivalence check is not Azure What-If.

The checked-in pipeline validator parses 11 YAML files and checks paths, required template arguments, four discovery routes, target-enabled routing, enabled-stage bindings, the six-stage dependency/artifact chain and complete stack-wrapper forwarding. Its deliberately limited expression evaluator does not implement the ADO service. No server expansion, authorization, publication or deployment is claimed. Azure tests remain mocked, the 15 emulator cases were not rerun, and all deployment targets remain disabled. Older entries below retain their original evidence and file paths.

## Discovery menu and handoff review: 19 September 2026

Checked the Discover entry point, literal service-connection routing, hosted agent, inventory export and Deploy manifest checks. Added menu guidance for inventory scope and successful-main-run selection; replaced obsolete deployment-checkbox instructions in the published summary with the current four-field developer contract. Discovery remains read-only and does not validate publishing/stack ownership or enable targets. All **78 discovery/catalog/handoff cases passed** using mocked Azure calls: `artifacts/discovery-tests/3c0f3f3c8e89415c949632eb684403ae/results.json`. All 11 YAML files parse and local template wiring checks pass. Updated live ADO menu rendering and an actual discovery run were not exercised.

## Deployment Stacks upgrade: 19 September 2026

This supersedes the incremental-only decision and nine-file pipeline layout in the earlier enterprise review below. The [upgrade runbook](deployment-stacks-upgrade.md) records the implementation plan, delivered behavior and outstanding Azure acceptance. All four target profiles remain disabled.

The final `Test-Project.ps1` run completed successfully: **225 offline contract cases**, **18 application tests passed**, **15 opt-in Azurite integration tests skipped**, zero failures. All four environment templates, the subscription stack wrapper, Policy definitions and private registry template compiled. The operator build reported zero warnings/errors; the Function dependency advisory query reported no vulnerable direct/transitive packages.

| Suite | Passed | Evidence |
|---|---:|---|
| Tooling | 19 | `artifacts/tooling-tests/60ff5a32769d4256995b317dfbbafee1/results.json` |
| Self-service orchestration | 42 | `artifacts/self-service-tests/eb84355793584683ba52ee069fe7a019/results.json` |
| Discovery/catalog/handoff | 78 | `artifacts/discovery-tests/057cfa84077e4a749cba82a1be5a910d/results.json` |
| Cost/options | 15 | `artifacts/cost-tests/ccebc0c26b364ad5bdd9190035ccae04/results.json` |
| Platform intent/governance | 37 | `artifacts/platform-tests/f5b13734c0fd4f029681798ed4eddcd2/results.json` |
| Stacks/Template Specs | 34 | `artifacts/stack-tests/a9c912b40a11466d8de23dbdac22f80c/results.json` |

Stack cases cover publication/reuse/tamper rejection, CLI capability checks, Foundation/Release sequencing, native preview parsing and cleanup, incomplete/potential changes, drift, failed/unhealthy stacks, shared-resource boundaries, deny weakening and removal rejection. Every Azure call in these cases is mocked. The final tooling checks also used the locally installed CLI 2.89.1 help; no Azure connection was made.

Eleven YAML files parse. Local structural verification checked all four intent/disabled routes, the `extends` entry, six-stage ordering, template parameter declarations/forwarding, exact enabled-target publisher bindings, and full wrapper parameter/output forwarding. The embedded RG composition equals the compiled original template. Evidence: `artifacts/test-results/platform-structure.json`. This is not ADO server-side expansion or an executed deployment.

Application test evidence remains `artifacts/test-results/unit.trx`; the earlier actual Functions-host/Azurite run is dated 17 September. No new emulator/host execution, Template Spec publication, Azure stack creation or live deny-assignment/What-If acceptance is claimed.

## Enterprise platform review: 19 September 2026

Current developer inputs are workload type/name, environment and region. Subscription, network, endpoint and alert choices are platform-owned. The older menu/checkbox entries below are historical evidence, superseded by this intent contract. Only blob-transfer is implemented; all four targets remain disabled and route to the hosted setup check.

`Test-Project.ps1` completed successfully with PowerShell 7.6.2, .NET 10.0.300 and Bicep 0.47.16. No new application packages were required.

| Verification | Result / evidence |
|---|---|
| Tooling contracts | 19 passed: `artifacts/tooling-tests/012ae6128239484ca03af845e0122155/results.json` |
| Self-service orchestration | 42 passed: `artifacts/self-service-tests/33bbace888694dd48b677c8f15d4c286/results.json` |
| Discovery/catalog/handoff | 78 passed: `artifacts/discovery-tests/14adbe2f3bea4541b3b017e2744ad60b/results.json` |
| Cost/options | 15 passed: `artifacts/cost-tests/4d9ecb8adf564245835ac56f205c3584/results.json` |
| Platform request/governance | 37 passed, including the final resolver ownership test: `artifacts/platform-tests/7fecb51ecfb540149a90e451dba674a8/results.json`; copied to `artifacts/test-results/platform-contracts.json` for existing pipeline publication |
| Bicep | dev, qa, uat, prod plus separate Policy and registry templates compile without reported errors/warnings |
| Application tests/build | 18 passed, 15 opt-in Azurite cases skipped, zero failed: `artifacts/test-results/unit.trx`; operator Release build has zero warnings/errors |
| Function dependency advisory query | No reported vulnerable direct/transitive packages: `artifacts/test-results/vulnerabilities.json`; not an npm-tool audit |
| YAML / compiled structure | Nine YAML files parse; four literal intent mappings and disabled hosted routes checked; generic endpoint arrays/outputs, shared-workspace conditions, alert query filters, workload IDs, definitions-only Policy and private ACR verified in compiled JSON: `artifacts/test-results/platform-structure.json` |
| Local JSON request | Example resolves the registered disabled dev target without Azure: `artifacts/platform-request-review-20260919.json` |

The new contract cases use mocked Azure reads and plans. No Azure deployment, central-network connection, Policy assignment, registry publication, ADO server expansion or live form inspection was performed for this revision. Actual Functions-host/Azurite execution remains the separately dated 17 September evidence below; it was not rerun for infrastructure/menu changes. ARM Provider validation, Policy effects, cross-subscription permissions, private DNS/routes, idempotency and approvals require the live acceptance sequence in [enterprise-platform.md](enterprise-platform.md).

## Self-service completion audit: 18 September 2026

### Temporary hosted setup for disabled targets

Generated disabled-target routes now use `self-service-setup.yml` on `windows-latest`, omitting private pools, deployment environments and Azure tasks from the expanded Deploy workflow. The check reports configuration/setup only; it does not consume discovery evidence or indicate deployment readiness. Enabled-target generation retains the original private-pool deployment stages. **78 offline cases passed**: `artifacts/discovery-tests/b65919c1652b4eb5b12afac9e04a0e01/results.json`, including disabled/enabled routing and the absence of Azure execution in hosted setup. Live ADO template expansion remains unverified until publication.

All nine YAML files parsed, and structural checks confirmed all four current Deploy routes resolve to the hosted setup template. The actual setup task script executed locally and produced `artifacts/hosted-setup-check/setup-only.md`, explicitly reporting no Azure deployment or discovery-artifact validation. Catalog freshness and source-manifest checks passed. No Azure calls were made.

### Deployment checkbox cleanup

The two deployment checkboxes now follow the four target selectors, with short action/cost labels. Endpoint prerequisites, production alert requirements and pricing assumptions remain in the reference fields below. Both default to checked. Parsed-YAML comparison against the previous revision confirmed identical resource selection, stage inputs and checkbox defaults. Catalog freshness and PowerShell parsing passed; all **76 discovery/catalog/handoff cases passed** again: `artifacts/discovery-tests/1c598b7d5cc84c12bdffb1e59d40e7f1/results.json`. The updated form has not been inspected live. The reported missing/unauthorized `blob-transfer-private` pool remains a platform setup blocker; this label change does not create or authorize a pool.

### Separate native Discover and Deploy menus

Discover now exposes only four target selectors and a fixed discovery operation. Deploy owns resource/cost descriptions and boolean options, and declares the existing `Enetact.Bicep` definition as its `discovery` pipeline resource. Developers select a saved run through **Run pipeline > Resources > discovery**. Runtime resource metadata supplies the existing exact-run artifact download and manifest checks. No manual ID fields or automatic completion triggers remain in either entry point. Shared stage templates and all four protected target bindings remain in use.

- **76 discovery/catalog/handoff cases passed**, zero failures: `artifacts/discovery-tests/f5e712dd92bf4937b30cfdda0dc2d39b/results.json`. Includes menu separation, source resource metadata, UTC-stable generated price dates, and existing failure/branch/scope/freshness checks.
- All eight YAML files parsed successfully. Structural checks verified the native resource declaration, four deployment routes, task/environment metadata forwarding, exact-run artifact inputs and checkbox types. Catalog freshness, PowerShell parsing and source-manifest checks passed.
- The browser showed existing definition `Enetact.Bicep` (ID 1) and a successful run 9. That establishes its name/status, not deployment readiness or live rendering of the new local YAML.
- The updated menus require publishing these source changes. The ADO new-pipeline wizard redirected to GitHub sign-in, so the separate Deploy definition was not created. Live resource selection, Azure DevOps template expansion and deployment remain unverified. All checked-in deployment targets remain disabled.

### Cost references, real checkboxes and frozen estimates

Both menus now display reviewed USD retail rates and boolean options for destination private endpoints and log alerts. Public pricing was retrieved from Microsoft's retail API on 19 September UTC / 18 September US Central; the snapshot retains meter IDs, units, region, tiers and effective dates. The selected cost report is hashed into the release bundle and shown before approvals. Required private access and core resources remain enforced; production rejects disabled alerts. This change did not deploy or alter Azure resources.

- **15 cost/option cases passed**: `artifacts/cost-tests/45d66537b070415b90ceba7c03ca332a/results.json`. Covers all four checkbox combinations, non-HNS destinations, reused DNS, hosting/instance counts, stale/missing regional prices and production constraints.
- **41 deployment cases passed**: `artifacts/self-service-tests/a3b66d1960aa4a248e7d07460c253d5f/results.json`. Includes report tampering, frozen options, production parameter validation and approval-preview cost/option display.
- **74 discovery/catalog cases passed**: `artifacts/discovery-tests/793c285dd66d49b89f759328a79d6294/results.json`; **19 tooling cases passed**: `artifacts/tooling-tests/d03e4fea040d4152bff540516cae6f05/results.json`.
- All four environment templates compiled under `artifacts/cost-compile/`. Inspection confirmed both destination endpoint conditions and all three alert enabled expressions use the selected booleans. Actual compiled default configurations produced full-release fixed subtotals of dev $77.81, qa $134.75, uat $291.70 and prod $404.85 per 730-hour month, plus excluded usage.
- YAML parsing verified boolean forwarding through both entry points and all four deployment routes; discovery receives no resource options. PowerShell parsing, catalog freshness and source-manifest checks passed.
- Offline tests use synthetic prices/Azure responses; the separate pricing refresh used only the public retail API. Live menu rendering, deployment of optional configurations and actual billed costs remain unverified. No app runtime tests were rerun for these infrastructure/menu changes.

### Workload resource descriptions in the run menu

Both generated self-service entry points now describe the blob-transfer workload and expose three single-value informational parameters covering package resources, networking, and billable deployment prerequisites. These values are not passed to deployment templates; workload IDs and protected-resource routing remain unchanged. The existing 74 offline discovery/catalog/handoff cases passed again: `artifacts/discovery-tests/0f255b82470842bda65145d9d0c3cfe5/results.json`. Catalog freshness passed. Live rendering of this updated Azure DevOps form remains unverified until the changes are pushed.

### DNS inventory fallback

Build 7 published its discovery artifact but reported partial inventory because the private DNS list failed. Its browser summary showed successful ARM subscription/provider diagnostic calls; those statuses alone do not establish the provider registration state or explain the DNS error. The user reports that no DNS zones have been created.

Discovery now falls back to the subscription-scoped ARM resource list for `Microsoft.Network/privateDnsZones`. Successful empty and populated responses produce complete DNS inventory with explicit fallback provenance. Both reads failing still save partial evidence and stop. Recovery does not clear failed VNet/subnet status. Bicep's existing new-network module defines all five private DNS zones and links; no Azure resources were created during this change.

- **74 discovery/catalog/handoff cases passed**, zero failures: `artifacts/discovery-tests/2bfe280f86da481bb18d7a2a712a6c1d/results.json`.
- New cases exercise empty and populated fallback results, exact subscription/type scope, manifest hashes, summary text, native exit-code recovery and preservation of VNet/subnet failures. Existing cases cover failure of both DNS paths and rejection of partial inventory.
- Azure calls were mocked. Successful live fallback, deployment handoff and provisioning remain unverified; rerun discovery from the updated source.

### Historical legacy Azure DevOps URL and evidence-retention fix

The latest supplied run signed in using workload identity federation, selected its subscription, received the private DNS `BadRequest`, and then stopped at organization/project validation before writing discovery evidence. The supplied project URL is `https://enetactgames.visualstudio.com/Enetact`: organization `https://enetactgames.visualstudio.com/`, project `Enetact`. The previous validator only accepted the modern `dev.azure.com` form.

Discovery and handoff now share validation for both supported URL forms (including legacy `DefaultCollection`). Optional organization/project validation failures are caught and included in the report, so they cannot discard completed Azure reads or DNS diagnostics. Required network failures still save partial evidence and fail; they are never interpreted as empty resource lists.

- **70 discovery/catalog/handoff cases passed**: `artifacts/discovery-tests/af9d24c98f8e48eaa522b7617996aae3/results.json`, including the exact supplied organization/project, rejected unsafe URL forms, missing project/organization, and combined DNS plus optional-configuration failures.
- **38 deployment cases passed**: `artifacts/self-service-tests/2b57dd0a7b3d427b8d50e107f5f377e6/results.json`.
- Catalog freshness and PowerShell parsing passed. Azure calls were mocked; the organization was not contacted. The underlying DNS error and live acceptance remain unresolved.

### Empty inventory, DNS diagnostics and two-run deployment handoff

The latest user-supplied AzureCLI excerpt selected subscription `f4f2eafe-2512-4c2f-9b5b-c88f6767e778` and reached `network private-dns zone list`, which returned `BadRequest: The specified subscription ... does not exist`. This shows progress beyond the earlier empty connection-input failure. It does not establish the cause of the DNS service error or prove zero DNS resources exist. No successful full inventory or Azure deployment is claimed.

Discovery now distinguishes successful empty lists (`None found`) from failed reads (`Unknown`). Required network-read failures save a partial inventory/manifest before failing; private DNS failures also collect independent read-only ARM subscription/provider diagnostics. Optional endpoint failures remain warnings without leaking their native exit code into a successful required-inventory run.

New-network target registration accepts complete empty inventory plus explicit naming/location/CIDRs. The separate deployment entry point consumes the selected discovery artifact and verifies its hashes, completeness, freshness, target scope and actual Azure DevOps source run before qualification. Discovery evidence is retained in the frozen deployment bundle. Shared-network reuse and deployment checks remain separate from resource creation.

- **65 discovery/catalog/handoff cases passed**, zero failures: `artifacts/discovery-tests/ce8ca60d315c464f817d354777528bd3/results.json`.
- **38 self-service/bundle/orchestration cases passed**, zero failures: `artifacts/self-service-tests/05b15cf9dbec477b8fd1818a88825ce8/results.json`.
- **19 tooling cases passed**: `artifacts/tooling-tests/a997c2733db24ea6aced3f99c4a369c7/results.json`.
- Eight YAML files and all PowerShell scripts parsed; generated catalogs match their source profiles. All four Bicep environments compiled successfully. All pipeline entry points explicitly disable automatic triggers.
- These tests mock Azure calls and source-run records. They do not prove successful Azure DevOps artifact download, server-side template expansion, the DNS service fix, or live provisioning. The application runtime was not restarted or retested for these deployment-tooling changes.

### Historical live discovery log diagnosis: logs_4.zip

The supplied `logs_4.zip` records a manual `discover` run on `feature/selfservice`. Inventory reached the Azure CLI task (2.279.1, Azure CLI 2.90.0 with azure-devops 1.0.8 installed), but failed with `Input required: connectedServiceNameARM`. The expanded YAML contained top-level `serviceConnection: SC-AZ-A-Bicep` while both the Azure CLI `azureSubscription` input and script `BoundServiceConnection` argument were empty. Authentication and inventory never started. Artifact publication then failed because its directory had not been created. This is pipeline wiring evidence, not an Azure RBAC failure or a deployment attempt.

The generator now emits a stage router with literal protected-resource values passed through explicit template parameters for both discovery and deployment. Nested deployment template paths are relative to their containing template. Discovery prepares its artifact directory and an explanatory README before Azure login.

Validation at that revision: **34 discovery/catalog cases passed**, evidence `artifacts/discovery-tests/09ea87474ec7431b9de8fd4a68c026cf/results.json`; **37 deployment cases passed**, evidence `artifacts/self-service-tests/45fe741212a64388baf0a69db8731b65/results.json`. A local YAML/template-subset expansion check evaluated all eight environment/operation combinations and verified nonempty matching connection inputs, script bindings, pools and environments; an invalid combination produced the rejection stage. This local check is not Azure DevOps server validation. The newer supplied log above reached the private DNS query.

### Subscription discovery follow-up

The subsequent service-connection bootstrap update passed **31 discovery cases** and **37 deployment cases**. Evidence: `artifacts/discovery-tests/f7a7ab3c850c498fbc89cc9e992d6ff4/results.json` and `artifacts/self-service-tests/30b5ef2aea0141478ffa3cf4c403c81d/results.json`. New cases prove that the disabled placeholder can discover the active service-connection subscription without enumerating alternatives or modifying the profile, while missing/wrong bindings, mismatched registered subscriptions, and disabled accounts fail. These are mocked Azure results, not live subscription evidence. Read-only discovery now uses a hosted agent; deployment retains the private pool.

- Added read-only `discover` versus `deploy` routing, subscription/network dropdown generation, identity-mapped disabled profile generation, existing-network Bicep support, naming suffixes and optional container/queue-scoped pipeline roles.
- **24 discovery/catalog/network tests passed**, zero failures, with strict mocked Azure calls. Evidence: `artifacts/discovery-tests/15a4245289a642c0a8092e7796ade9e3/results.json`. Coverage includes selected-subscription scoping, duplicate subscription names, filtered Azure DevOps endpoints, principal object-ID mapping, standard names, overwrite rejection, ambiguous catalog entries and existing network/DNS failures.
- The existing **37 self-service cases passed again**: `artifacts/self-service-tests/942c6fe7d4d641fe9e49ebe1b1b04ea0/results.json`.
- All four environments compiled after the Bicep changes; the normal .NET run still passed 18 cases with 15 opt-in integration cases skipped, and dependency advisories remained clear. YAML parser checks cover the generated catalog and all four stage templates, not Azure DevOps server-side expansion.
- No live discovery, role assignment, shared-network deployment or Azure DevOps run was performed. Dropdowns contain the disabled `unconfigured` example until an actual subscription/service connection is registered. See [discovery setup and limits](subscription-discovery.md).

### Original completion-audit run

`Test-Project.ps1` passed on the updated source: all four Bicep environments compiled, **18 .NET cases passed and 15 opt-in emulator cases were explicitly skipped**, the operator tool built with zero warnings/errors, and the current Function dependency query (including transitives) reported no vulnerabilities. No emulator/host rerun was needed for these deployment-script/documentation changes; earlier runtime evidence remains dated below.

- **37 offline self-service cases passed**, zero failures. Evidence: `artifacts/self-service-tests/e4714aba510c4e8eb6e8e7d3d8e62a2d/results.json`. These exercise the actual PowerShell orchestration with fake Azure responses: target validation, file integrity, canonical fingerprints, what-if rejection, existing-app Foundation skip, drift/expiry guards, failed entrypoint receipts, connectivity failure, package reuse/conflict, failed or duplicate smoke evidence, and successful ordered release. No live Azure behavior is proven by these mocks.
- **19 tooling contract cases passed.** Evidence: `artifacts/tooling-tests/0e127c271fe440a68524d5fd9f98909b`.
- Unit and advisory evidence: `artifacts/test-results/unit.trx` and `artifacts/test-results/vulnerabilities.json`.
- Both pipeline YAML files and three self-service YAML templates parsed successfully with YAML 2.9.1. This is syntax validation, not Azure DevOps server-side template expansion or execution.
- The self-service guide, completion audit and dispatcher build/deployment requirements now reflect the implemented workflow. Target examples remain disabled pending platform onboarding.
- No Azure deployment, organization configuration or real self-service bundle qualification/run was performed. Missing private agent routes, service connections, approvals and live acceptance remain explicit requirements in [self-service](self-service.md).

## Local run mode: 17 September 2026

The current source adds a separate emulator configuration and guarded local SDK clients while preserving the Azure identity path. No Azure deployment was performed.

- **33 .NET tests passed, zero failed, zero skipped:** 8 policy, 1 Function contract, 9 local configuration guards, and 15 Azurite integration cases. Evidence: `artifacts/recovery-tests/aa1e3cd1d4d8440db27f5982bee9d636/recovery.trx`.
- **6 local lifecycle tooling checks passed:** path containment and process ownership, including rejected traversal, sibling paths, reused PID start times, and changed executable paths.
- `Run-Local.ps1` built both applications with locked restores, seeded storage, and started the actual Functions host. `Test-Local.ps1` uploaded three ordinary files with identical bytes and verified three completed records, a shared destination, byte equality, source retention, BlobTrigger dispatcher log records, and all three timer heartbeats. The QueueTrigger worker completed the transfers. Evidence: `.local/logs/134580633e584f8ab616bef43851df21/smoke-639e99603e814147a9054fbd1d638fb3.json`, with host logs in the same directory.
- The selected SDK/Node were reused; Azurite 3.37.0 and Core Tools 4.14.0 were installed into ignored project folders. Core Tools 4.12.0 failed with an Options 10 assembly dependency mismatch; the pinned 4.14 minimal ARM64 distribution resolved it without changing application packages.
- Stop removed only recorded process trees and released all four ports. Reset removed task-created emulator data while retaining logs/tools. Restart and the final smoke succeeded. A duplicate run was rejected without changing its process receipt. All four listeners were verified on `127.0.0.1`.
- Setup check-only worked through Windows PowerShell 5.1 by reusing installed PowerShell 7 and left the tool receipt unchanged. Missing-SDK, missing-Node, missing-PowerShell download, and Windows x64 install branches remain untested on clean machines.
- Azure DevOps YAML now includes the actual-host smoke test and evidence publishing. That YAML has not been executed in Azure DevOps.

Generated logs, emulator data, tools, build output, and developer settings are ignored. See [local development](local-development.md) for exact commands and limits. Earlier package/diagram/compiler evidence below is retained as historical evidence and is not a newly qualified release of this changed source.

## GitHub repository baseline follow-up: 17 September 2026 (historical)

The source now lives in the `Enetact/Bicep` Git repository on `feature/selfservice`. The obsolete direct-copy Function and its policy tests were removed, leaving only the queue-based pipeline. Current local evidence supersedes the older test/package counts below:

- The normal test run passes **9 cases** and explicitly skips the 15 emulator cases. This includes a new assembly contract requiring exactly five correctly bound Functions.
- The isolated Azurite run passes **24 cases, zero failures, zero skips** (9 unit/contract cases and 15 storage integration cases). Evidence: `artifacts/recovery-tests/2a6215430fc54db4a715bcb4105d2571/recovery.trx`.
- **19 PowerShell tooling cases** cover CLI argument translation, exact Function metadata, ordinal longest-prefix scope mapping, advisory report handling, and the existing-app Bootstrap guard.
- Both application projects build with locked restores. All four environments compile with Bicep 0.47.16 through the fixed Azure CLI fallback and through the standalone compiler during packaging.
- The current Function dependency advisory query, including transitive dependencies, reports no vulnerable packages. Feed unavailability now fails validation rather than being treated as clean. This does not certify the emulator's npm dependency tree.
- `Build-Package.ps1 -ReleaseId selfservice-fixes-20260917-01` built a ZIP with exactly the expected five Functions and stored its hash/provenance alongside compiled environment templates. ZIP SHA-256: `5b4d1587884b07d0da579a3a3c3def3485fba09a27ba56c3d2e2f69421ce81a0`. The receipt explicitly records local uncommitted changes.
- `Deploy.ps1` and `Smoke-Test.ps1` were parsed and their shared validation contracts tested. They were not executed against Azure. The new Azure DevOps YAML has not run in a hosted pipeline; local script success does not prove organization permissions or pipeline service connectivity.

An initial build encountered a generated Functions extension restore error; a direct restore succeeded and subsequent builds/tests passed. Initial sandbox-only access to the advisory feed failed; successful qualification used normal network access without disabling auditing. Azurite installation reported upstream package deprecation warnings.

The source manifest is now regenerated from all Git-tracked and non-ignored new files, excluding itself and deleted files, and checked in CI. LF checkout attributes keep its hashes consistent across agents. The broader self-service deployment architecture remains a plan; no Azure infrastructure or Azure DevOps organization configuration was changed.

## Earlier bundle evidence (historical)

- Bicep 0.47.16 compiled main.bicep and Dev/QA/UAT/Prod parameter files after ledger co-location. Compiled ARM inspection confirmed exactly two new storage accounts, both source/ledger containers in solution storage, the shared source/ledger endpoint, and removal of the ledger private endpoint.
- Installed .NET SDK 10.0.300 was used. Function and operator tool build successfully with locked dependency restores.
- The final emulator-enabled suite passed **23 tests, zero failed, zero skipped**: eight policy cases and fifteen storage integration cases.
- Source and ledger integration-test containers share one emulator storage account, matching the revised account layout.
- Tests use real loopback Azurite 3.37.0 blob/queue operations with ordinary filenames and no uploader metadata. They cover dispatcher payloads, repeat dispatch, scope-separated content deduplication, concurrent workers, conditional writes, interruption between copy/ledger commits, missing destinations, corrupt content, ignored untrusted hash metadata, overwritten filenames, stale unversioned pointers, missed dispatch and paginated reconciliation, bounded attempts, reviewed recovery, lease exclusion, both poison monitors, missing sources, excessive size, and unsupported page blobs.
- Test-Project.ps1 ran successfully. Its ordinary test run passes eight policy cases and explicitly skips fifteen opt-in integration cases.
- Test-Recovery.ps1 ran successfully end to end: installs pinned Azurite locally, starts a hidden loopback process with telemetry disabled, checks its owned listeners, runs all 23 cases, and stops only its own process. An initial emulator startup timeout was corrected before the successful run.
- Prior to ledger co-location, Build-Package.ps1 verified release packaging by building a ZIP with 93 entries including host.json, functions.metadata and .azurefunctions dependencies.
- Generated metadata contains CopyUploadedBlob (queueTrigger), DispatchUploadedBlob (polling blobTrigger), ReconcileTransfers, MonitorTransferPoison and AuditTransferLedger (three timerTriggers).
- All PowerShell scripts passed parser checks. Smoke-Test.ps1 and Deploy.ps1 were inspected/parsed but not executed against Azure.
- The Function dependency vulnerability query, including transitive NuGet packages, reported no vulnerable packages from the configured NuGet.org source at validation time. This does not certify the complete application or emulator npm dependency tree.
- Twelve Mermaid diagrams rendered with Mermaid 11.17.2; all were visually inspected. The offline HTML contains twelve embedded SVGs and produced no browser script errors.

Builds and emulator tests ran in an isolated temporary copy because this workstation restricts compiler writes under Documents. The final editable source is in this project. The downloadable distribution is a standard source TAR, not a deployed application.

## Explicit limitations

The 15 emulator integration cases directly invoke Function methods and use storage SDK operations. The separate local-host smoke now exercises real BlobTrigger/QueueTrigger listeners and timer scheduling. Neither validates Azure managed identity, private networking, retained versions, or complete runtime poison routing and restart-failure scenarios.

Local tests set IncludeSourceVersions=false because they do not qualify Azure version-listing/retention behavior. Production configuration defaults to true, and the code uses version-addressed, ETag-conditioned reads. The live smoke script specifically exercises an overwritten source name, but it has not been run here.

The ordinary external uploader still needs its own private network path and authorized access. No external uploading system was configured. No historical Azure data was migrated or cleaned up.

## Local portal verification — 27 September 2026

This is a targeted portal verification, not another full Bicep/Functions project test run. The existing pipeline YAML and target enablement were unchanged by the portal increment.

- `scripts/Test-Portal.ps1`: **24 passed**, zero failed/skipped. Tests cover catalog/region/operation gates, disabled deployment, exact discovery binding, unconfigured identity, rejected definition/YAML/repository/branch/age/selection cases, sanitized upstream errors and no automatic queue retry. ADO transport is mocked. Evidence: `artifacts/test-results/portal.trx`.
- `node tests/portal/smoke.mjs`: **13 passed** against the real unconfigured local HTTP server. Includes session/CSRF/Origin/Host protections, unauthenticated access, disabled deployment, synthetic inventory analysis via Node and disconnect.
- Final self-contained packages `artifacts/portal-packages/20260927-082310/win-arm64.zip` and `win-x64.zip`: both launched and each passed those 13 HTTP checks using the bundled catalog and analyzer. ARM64 ran natively; x64 ran under Windows 11 ARM emulation. Native x64 hardware and clean-machine installation are still acceptance items. Per-package receipts are `package-test.json` in their publish folders.
- Package SHA-256: ARM64 `53D962784C2058FB194BFE666638B520F44A1E5746ECF2B1517B7E8DA292ECD8`; x64 `EF060D5DA15F0953BB369F53FDE4837A83BB598CE707D77F4CAEB7D1D9F6DFB9`.
- Actual setup/start/ownership-checked stop verified. A timestamp/path normalization defect was corrected. Source startup works with the repository SDK/runtime (10.0.300 / 10.0.8); portable packages carry runtime 10.0.12. MSAL is 4.90.1; package advisory query reported no known vulnerable portal dependencies.
- Codex browser inspected narrow and 1440-pixel desktop layouts, workload selection, Preview handoff fields, disabled gates, registration setup, skill listing and skill text. The browser file chooser stalled, so the **file-picker-to-report UI path remains unverified**; HTTP upload and real analyzer invocation passed. Clean narrow-viewport screenshot: `artifacts/portal-tests/portal-preview.png`. The browser's stitched desktop capture was unsuitable as evidence.
- Generated menu check passed for all eight targets; portal PowerShell files parsed successfully. No live Entra login/consent, Azure reads/writes or ADO queue calls were performed. Registration and the live Discover → Preview acceptance flow remain outstanding.

## Required live acceptance

- Resource policies, region/SKU/runtime/zone capacity, quotas, filled-in WhatIf, destination ownership.
- Host package download, identity propagation, BlobTrigger service/internal queues, explicit queue send/process, ledger leases, destination writes/readback.
- Private endpoint approvals, DNS and routing for the app, external uploader, deployment runner and operators.
- Actual BlobTrigger discovery, queue dispatch, concurrency across instances, runtime retry/poison behavior, and restart recovery.
- Retained-version enumeration and exact-version reads, source lifecycle deletion policy, overwrite races, large-file load and backlog.
- Both poison alerts, quarantined-ledger alerts, missing-reconciliation heartbeat, action-group notifications and telemetry retention.
- External-upload Smoke-Test.ps1: ordinary uploads with no metadata/queue sends, two names with identical bytes, one repeated overwrite, three completed revision records and one verified destination.
- Recovery audit attribution, ledger backup/restore, privilege boundaries and approved operational cleanup.

This is a locally tested production-oriented baseline. It is not a claim of deployed production readiness.

## Connected portal diagrams — 27 September 2026

Implemented inline observed inventory, conceptual component diagrams for seven workload configurations, and guarded reading/projection of saved ADO Preview artifacts. Scope and methods: [connected diagrams](portal-diagrams.md). No cloud calls, target enablement or new package dependencies.

- `./scripts/Test-Portal.ps1`: all 944 pinned skill source hashes matched; six topology tests and 66 backend tests passed, zero failures/skips.
- `node tests/portal/smoke.mjs`: 17 real localhost HTTP checks passed on ARM64, including module serving and unauthenticated Preview rejection.
- Codex browser: real Private Storage configuration rendered; a separate localhost fixture verified partial discovery/unknown references, the same-subscription comparison control, blocked Preview display and clearing on environment change. Fixtures are synthetic, not Azure or ADO evidence.
- Evidence: `artifacts/test-results/portal.trx` and `artifacts/portal-tests/http-smoke.json`. Portable packages from before this increment are unchanged and require rebuilding. Native x64 and live identity/artifact compatibility were not tested in this increment.
