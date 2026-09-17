# Operations and recovery

## Validate locally

```powershell
az bicep install --version v0.47.16
./scripts/Test-Project.ps1
./scripts/Test-Recovery.ps1
./scripts/Build-Package.ps1 -ReleaseId 'queue-recovery-001'
```

Prerequisites: PowerShell 7, .NET 10, Azure CLI and Node 22+. Emulator tests exercise the dispatcher method, queue payload, worker, actual storage leases, hashes, reconciliation and recovery without Azure. They do not run the full Functions host or Azure version listing.

## External uploads

No upload helper is required. Any authorized system can place ordinary committed block blobs in the configured source container with its existing filenames. No custom metadata or direct queue permission is required. The external system still needs private connectivity and supported authentication.

Polling BlobTrigger detects arrivals and dispatches pointers. Reconciliation scans the same container and retained versions as a fallback. Processing is asynchronous, potentially delayed by polling/backlog; multiple files are independent, and ordering is not guaranteed.

The source is version-enabled non-HNS storage. Do not disable versioning or expire source versions before processing/recovery requirements are satisfied. Versionless sources cannot guarantee recovery of rapidly overwritten revisions. Source soft delete and version retention are different policies.

## Find a transfer

Use opaque processing IDs from `BlobDispatched` / `TransferCompleted` logs, or enumerate authorized ledger records with standard Blob tools. The ID is SHA-256 of configured source-container URI, filename and normalized ETag separated by newlines. Source names and business metadata are in the protected ledger, not the application message text.

```powershell
az login
az account set --subscription '<application-subscription-id>'
dotnet build ./src/TransferTool/TransferTool.csproj -c Release -m:1 -p:RestoreLockedMode=true
$tool = './src/TransferTool/TransferTool.csproj'
$requestId = '<64-character processing ID>'
dotnet run --project $tool -c Release --no-build -- status --ledger 'https://<source-account>.blob.core.windows.net' --scope 'default' --request-id $requestId
```

Status returns LedgerETag plus record fields including SourceETag, SourceVersionId, Attempts, ErrorCode and DestinationName. It is a read-only operation.

## Controlled recovery

Investigate both `transfer-work-poison` and `webjobs-blobtrigger-poison`, relevant alerts and ledger records. A poison message can remain after later reconciliation completed its transfer; do not equate queue backlog with missing destination data.

1. Fix the underlying permission, connectivity, availability, source-version retention or destination problem.
2. Review exact ledger/source ETags and the persisted source version.
3. Obtain your organization's approval/change reference.
4. Explicitly invoke resume with a trusted recovery operator identity.

```powershell
dotnet run --project $tool -c Release --no-build -- resume --ledger 'https://<source-account>.blob.core.windows.net' --queue-service 'https://<source-account>.queue.core.windows.net' --scope 'default' --request-id $requestId --expected-ledger-etag '<exact LedgerETag including quotes>' --expected-source-etag '<exact SourceETag including quotes>' --approval-reference 'INC-12345' --operator-label '<organizational identifier>' --apply true
```

Resume rejects stale ledger plans, records prior state and reviewed source identity, resets the bounded attempt budget and sends work. If send fails, the saved Retry state is discoverable by reconciliation while its source version exists. Queued does not mean completed.

The tool never deletes/changes a source or conflicting destination, and never deletes poison messages. An integrity conflict requires separate investigation and authorized correction. Unavailable permanently deleted source versions cannot be reconstructed. A new re-upload is a new revision/processing ID. Unsupported blob type and excessive-size cases need upstream correction rather than repeated retries.

After confirmed resolution, a queue operator may archive evidence and remove specific poison messages through an approved process. Work messages use non-expiring TTL; runtime-generated poison TTL/retention must be verified with the deployed extensions. The ledger is the durable work record, not an assumption of indefinite poison retention.

## Timers and performance

- ReconcileTransfers: bounded source/version scan, send due/missing work, persist continuation.
- AuditTransferLedger: report quarantine and unavailable source versions.
- MonitorTransferPoison: observe both poison queues without dequeuing.

Defaults: every five minutes; 100 items/page; five pages/run; ten lifetime attempts; 15-minute reconciliation retry delay; completed destinations eligible for full verification after 24 hours. Actual cycle time scales with the number of blobs and retained versions.

Queue retries use one-minute visibility and five dequeue attempts. Two worker invocations per host instance and two dispatcher invocations are configured. Content hashing, source readback and full destination verification add IO costs. Many duplicate sources can cause repeated verification of one shared destination. Load-test and tune before production.

## Upgrade from the original direct-copy BlobTrigger

The dispatcher now enqueues and the explicit QueueTrigger copies; source uploads remain ordinary blobs. Destination naming changes to content-addressed keys, with filename-to-destination mapping in the ledger.

1. Review active transfers and schedule a controlled release window.
2. Bootstrap the ledger container in solution storage, work/poison queues, settings and roles; review WhatIf.
3. Deploy a new immutable package, synchronize triggers, and confirm one BlobTrigger, one QueueTrigger and three TimerTriggers in metadata.
4. Review scope mapping and source/version retention before enabling scans over historical data. **The scan will enroll existing source blobs/retained versions in mapped prefixes**, not only newly uploaded files.
5. Earlier destination filenames/receipts are not imported into the new content ledger, so historical reenrollment can create new content-addressed copies. Decide migration/exclusion/retention deliberately.
6. Run the external-upload smoke script and live identity/network/alert acceptance checks. Preserve earlier data for approved rollback.

Do not deploy over a populated container without reviewing historical processing scope. No automatic migration, deletion, cleanup, role revocation, or Azure changes have been performed here.

## Production acceptance

Verify private package download; source BlobTrigger/internal queue permissions; explicit queue send/process; ledger leases; version listing and version-addressed reads; destination writes/readback; timer recovery after downtime; both poison alerts; missing heartbeat; action-group delivery; and multi-instance behavior.

Run `Smoke-Test.ps1` from a private-network-connected tester with source write, ledger read and destination read permissions. It uploads ordinary files with no metadata/queue sends, including an overwritten name, then waits for every source revision to reach the same verified destination.

Full Functions host trigger execution, Azure version semantics, managed identity, private DNS, HNS destination behavior and operational alert delivery remain live acceptance requirements.

## Moving an already-deployed separate ledger

New deployments create only host and solution accounts. For an existing deployment, removing the old account from this incremental Bicep template does not delete that Azure account or its private endpoint. This bundle performs no live migration or deletion.

1. Pause the Function App (dispatcher, workers and all timers) and recovery-operator writes. Quiesce external uploads if possible; otherwise retain source versions and allow reconciliation to catch up after restart.
2. Provision the new ledger container and container-scoped recovery access in the solution account. Wait for active ledger leases to expire. Back up the old ledger and copy all current request, content and audit records with their exact names and bytes. Retain old versions and diagnostics under the existing retention policy; ordinary copying does not preserve Azure version history or ETags.
3. Verify object counts and byte hashes. Do not begin with an empty ledger: doing so loses attempt budgets, quarantine and deduplication history. Clear only the copied system scan cursors so scans restart; never delete request/content records to force retries.
4. Deploy the updated package/settings, with Ledger__blobServiceUri equal to UploadStorage__blobServiceUri and distinct container names. Start the app and verify recovered requests, existing destinations, poison monitoring and reconciliation. Re-read ledger ETags before any manual recovery.
5. Retain the old account read-only for approved rollback/retention. Once the new ledger accepts writes, rollback requires reconciling those new records; simply switching back loses state. Decommission the old account, endpoint and obsolete grants only through a separately approved change after verification.
