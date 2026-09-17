# Validation evidence

This records the external-uploader design with the ledger co-located in solution storage. No Azure login, ARM deployment, Azure upload, role assignment, or live Azure test was performed while producing this bundle.

## Confirmed locally

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
