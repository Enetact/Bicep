# Validation evidence

This records the external-uploader design with the ledger co-located in solution storage. No Azure login, ARM deployment, Azure upload, role assignment, or live Azure test was performed while producing this bundle.

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
