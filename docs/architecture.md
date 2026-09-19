# Architecture and recovery contract

## Deployment selection and network ownership

The generated self-service pipeline maps an approved subscription alias and network profile to exact resource IDs and protected Azure DevOps resources. Its default discovery operation only reads inventory. Deployment supports either a new dedicated VNet or existing platform-managed integration/private-endpoint subnets and private DNS zones. Existing mode validates those resources before planning and leaves their definitions/delegation under platform ownership. Optional naming and pipeline data-role parameters follow the selected profile; empty naming suffix preserves the original resource names. See [subscription discovery and naming](subscription-discovery.md) and the [self-service release workflow](self-service.md).

## File arrival without uploader changes

An external system writes an ordinary block blob into the configured solution container. The Function App's `DispatchUploadedBlob` uses a polling BlobTrigger (`LogsAndContainerScan`) to detect it. It reads metadata supplied by Storage (ETag and version ID), then sends a work pointer to `transfer-work` in the solution account.

`CopyUploadedBlob` has an explicit Storage QueueTrigger and performs the copy. The dispatcher does not copy file contents. Its internal BlobTrigger queue/receipts are distinct from our explicit work queue. Runtime receipts use the host account; dispatcher poison handling uses the source account's `webjobs-blobtrigger-poison`; worker failures use `transfer-work-poison`.

There is no Event Grid. Storage's Events blade uses Event Grid, and Azure Storage Actions does not expose a queue-send operation. This implementation detects from the Function side. A timer also scans the source, so missed dispatches do not depend entirely on BlobTrigger receipts.

No client request ID, file-name pattern, custom metadata, checksum, or queue send is required. An external client must still have network reachability and its normal authorized blob-write access; this project cannot bypass storage security.

## Stable processing identity and scope

The worker derives a processing ID:

```text
SHA256(configured source container URI + newline + blob name + newline + normalized ETag)
```

Same source revision produces the same ID; another revision or filename produces a different ID. User metadata cannot select a destination, supply a trusted checksum, or assign a scope.

Scope comes from a server-controlled longest-prefix map, `sourceScopePrefixes`. Empty prefix maps all names to the default scope. Separate customer/claim/purpose boundaries require a reliable source-prefix policy or an authoritative mapping integration. Prefixes are routing policy, not per-user authorization.

The worker calculates SHA-256 of actual source bytes. Destination is `v1/<scope>/<sha256>/payload`. Exact duplicate content within a scope shares one destination even with different filenames or revision IDs. Same content across scopes stays separate. Visually equivalent documents with different bytes are not semantic duplicates.

This cannot prevent duplicate source uploads by an external system. It prevents duplicate downstream objects for identical bytes within the scope.

## Versioned queue pointer

Raw, case-sensitive JSON, with no file contents:

```json
{"SchemaVersion":1,"SourceName":"incoming from ERP/report.pdf","SourceETag":"\"0xEXAMPLE\"","SourceVersionId":"<storage-version-id-or-null>"}
```

Only the configured source account/container is accessed. The queue cannot choose a remote URL or destination account. Source names are validated for length/control characters; valid ordinary folder paths, spaces and Unicode are accepted.

If a version ID is present, reads target that exact retained version and are also ETag-conditioned. Reconciliation includes retained versions by default (`recoveryIncludeSourceVersions=true`). This helps recover revisions overwritten before the dispatcher observed them. It does not recover versions permanently removed by retention, or uncommitted blocks never stored as a blob.

Without versioning, a stale pointer whose source changed becomes SourceRevisionUnavailable; it must not copy replacement bytes as if they were the old upload. Repeated updates can be missed without retained versions. Source block versioning is enabled by the Bicep module. Append/page blobs are quarantined; a generic detector cannot infer business completion of an ongoing append stream or multiple partial commits.

## Durable ledger and atomic duplicate handling

Only host and solution storage accounts are provisioned. The existing data lake remains the destination. The solution account contains `incoming`, `transfer-ledger`, and the work/poison queues. The ledger uses the same Blob service endpoint as incoming, with a distinct container. The BlobTrigger and source reconciliation scan only the configured incoming container, so ledger updates cannot dispatch copy jobs. The `ledgerStorageAccountName` deployment output is retained as a compatibility alias for `uploadStorageAccountName`.


The ledger container in the solution storage account contains:

| Key | Content |
|---|---|
| `requests/<scope>/<processing-id>.json` | Source name/ETag/version, state, attempts, hash, destination, retry/verification times |
| `content/<scope>/<sha256>.json` | Canonical committed content record |
| `system/reconcile-cursor.json` | Source/version-list continuation |
| `system/ledger-cursor.json` | Ledger-audit continuation |
| `audit/<scope>/<processing-id>/<recovery-id>.json` | Recovery intent and prior state |

Blob conditional create plus a renewable 60-second lease serializes each request and each scope/content key. Renewal occurs every 20 seconds; failure cancels work. All ledger writes include the lease ID. Finite expiry lets another worker proceed after a crash. Locks are acquired request first, content second.

1. Validate pointer/scope, acquire request lease, fetch exact source revision.
2. Respect quarantine and retry budget; persist Processing before work.
3. Stream-hash source bytes under ETag precondition; reject files over the configured limit (default 1 GiB).
4. Acquire content lease; create destination only if absent.
5. Stream-read destination and verify actual SHA-256 and length.
6. Commit content record, then request Completed.

A crash after copy but before ledger completion is resolved by verification on retry. Conditional writes prevent accidental overwrite. A missing destination can be recreated from retained source. An existing destination with different bytes is quarantined, not overwritten. Source objects are never deleted or renamed by the application.

These steps are a recoverable sequence, not a cross-service transaction or exactly-once execution guarantee.

## Timers and bounded recovery

Reconciliation scans 100 source items per page, five pages per run, every five minutes by default. It advances the durable cursor only after all page messages are accepted; restart may cause safe duplicates. Versions count as separate scan items. Large containers take multiple runs to complete a cycle.

Completed transfers become eligible for full destination re-hashing after 24 hours. Missing files can be rebuilt. Duplicate sources may cause repeated verification of one shared destination, so plan IO capacity and retention deliberately.

Queue retries use a one-minute visibility delay and five dequeue attempts. The ledger limits processing to ten lifetime attempts by default, with a 15-minute rescheduling delay for reconciliation. Queue retries and timer scheduling are separate controls.

States: Pending, Processing, Retry, Completed, Quarantined. Quarantine never resets automatically. A ledger audit reports quarantined work and unavailable source versions. Both poison queues are observed without consuming messages. Alerts cover errors, review-needed states, and absent reconciliation heartbeats.

An authorized operator may reset eligible work after fixing its cause, providing the exact reviewed ledger/source ETags and an approval reference. The tool records intent before changing state. It does not delete poison messages or overwrite conflicting data.
