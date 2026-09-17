# Validation evidence

This records the external-uploader design with the ledger co-located in solution storage. No Azure login, ARM deployment, Azure upload, role assignment, or live Azure test was performed while producing this bundle.

## GitHub repository follow-up: 17 September 2026

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

The emulator integration tests directly invoke Function methods and use storage SDK operations. They **do not** run the Azure Functions host listener, timer scheduler, runtime poison routing, or managed-identity token flow.

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
