# Validation evidence

This records the external-uploader design with the ledger co-located in solution storage. No Azure login, ARM deployment, Azure upload, role assignment, or live Azure test was performed while producing this bundle.

## Self-service completion audit: 18 September 2026

### Subscription discovery follow-up

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
