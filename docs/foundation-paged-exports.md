# Paged customer metadata exports

Implemented locally, 24 October 2026. The [dedicated foundation service](foundation-service.md) can capture and page a scoped historical metadata snapshot. This extends the bounded legacy metadata export; it does not add Azure execution, a model tool, provider credentials or a raw-source export. Customer UI migration, automatic cleanup scheduling, production load qualification and live acceptance remain open.

## Capture and read

All HTTP routes below use the existing signed caller context and begin with `/api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}`. They require the current human Auditor role, the existing lifecycle-export capability and the exact route scope. A paid capability does not grant an Auditor membership. Body fields cannot choose another customer, actor, project or environment.

| Route suffix | Method/body | Result |
|---|---|---|
| `/administration/export-snapshots` | POST `{"pageSize":100,"lifetimeMinutes":15}` | HTTP 201 with `snapshot`, `firstCursor` and `firstPage`. |
| `/administration/export-snapshots/{id}/page` | POST `{"cursor":"<returned opaque cursor>","snapshotSha256":"<original snapshot digest>"}` | One committed page, its digest, `nextCursor` and `complete`. |

Save the original snapshot digest and manifest in the consumer's trusted storage. Process the returned first page, then follow `nextCursor` until `complete` is true. Keep sending the original snapshot digest. Do not replace it with a value obtained from an untrusted later response. Cursors belong in POST bodies, outside normal URL/access logs; they still need protected application logging and transport.

The store also supplies `GetPagedScopeExport` for owner-bound recovery using the snapshot ID and original digest, and `PurgeExpiredPagedScopeExports` for bounded, audited cache maintenance. These two methods currently have no dedicated HTTP route or AHP tool. The legacy `/administration/exports/{id}` read explicitly rejects every `xpg-` snapshot/page identifier so it cannot expose the private cursor index or bypass the new checks.

## Consistency, authority and provenance

Capture takes one immediate SQLite transaction in the dedicated customer cell. Only one writer/capture runs at a time in that database; other cells use separate databases. The complete cell audit chain is checked in SQL keyset batches of 128 records. Scoped artifacts, plans and audit metadata are then read in keyset batches of 128 and persisted as bounded immutable snapshot pages. Cancellation, a failed page insert, corrupt audit, capacity failure or snapshot expiration during capture rolls back every staged page, root and creation audit event. This is a bounded synchronous capture operation, not a qualified distributed background-export service.

The snapshot copies metadata as it existed at capture. Later source insertion, artifact retention/deletion or modification cannot shift page boundaries or silently skip/duplicate captured rows. Later events and export-read audits are outside the captured audit tail. Missing or altered pages fail explicitly; reads never advance past a gap. The historical snapshot can retain artifact identifiers/digests after the source bytes are removed, until its own expiry/cleanup policy applies.

Every page/recovery read checks the current human Auditor grant, exact customer/project/environment, original creator actor/version, cell policy version, snapshot lifetime and the original snapshot digest. Revocation denies access immediately; regranting creates a different actor version and does not revive an old snapshot. A policy change invalidates the snapshot. Suspended lifecycle state does not by itself replace these checks: the existing explicit Auditor export policy permits inactive-cell reads. Each accepted read appends an audit event in the same transaction.

Cursors contain 256 bits of server-generated randomness and are persisted only through a private page index. They are neither caller-controlled offsets nor self-signed credentials. A cursor resolves only its exact snapshot/page and remains subject to current authorization. The server checks ordered page commitments, previous-page links, page indexes, scope, actor/version, policy, type and expiry. Unknown/duplicate/null-required JSON members and malformed cursor/digest formats fail closed.

The returned `snapshot.canonicalManifestBase64` is Base64 encoding of the exact canonical UTF-8 JSON bytes whose SHA-256 is `snapshot.sha256`. It contains the schema, scope, creator/version, policy, capture/expiry times, audit tail and ordered page digests. It contains no private page-row index or first cursor. Each page's `canonicalPageBase64` similarly encodes the canonical metadata page bytes whose SHA-256 is `page.sha256`; these include the page's already-returned next cursor. Base64 is an encoding, not encryption.

To verify downloaded pages offline:

1. Decode `canonicalManifestBase64` and hash those bytes with SHA-256; compare with the originally pinned snapshot digest. Check its expected scope, actor/version, schema and timestamps.
2. Decode each `canonicalPageBase64`, hash its bytes and compare with both the page digest and that index in the committed manifest's `pageSha256` array.
3. Read the verified canonical JSON as the authoritative metadata, or compare its items/index/link/completion fields with the convenience response fields. Check all pages appear once in order, previous-page links match, the final page is complete, and item totals match the committed manifest.

The digest preimage is the decoded canonical envelope, not serialization of the convenience DTO or its Base64 text. Pinning a digest detects later rewriting even if a database checksum is recomputed. These hashes and the local append-only audit are not independent proof against a host/database administrator who controls an initial response, the cell file, backups or consumer trust. The separate audit-anchor implementation and an externally protected receipt can supply an additional trust boundary; this paging API does not claim independently signed or externally timestamped export receipts.

## Explicit limits and data boundary

| Limit | Implemented value |
|---|---|
| Requested items per page | 1–200; default 100. Pages can be smaller when the byte cap is reached. |
| Stored canonical page | 256 KiB; a single item too large for a bounded page is rejected. |
| HTTP page DTO / capture-or-recovery DTO | 1 MiB / 2 MiB, including verification envelopes. |
| Pages per snapshot | 4,096; overflow rejects the entire capture. |
| Snapshot logical manifest storage | 128 MiB, including private root and pages. |
| Aggregate export logical manifest storage | 256 MiB per cell; legacy and paged creation count existing export manifests. |
| Lifetime | 1–60 minutes; default 15. Future-created or expired snapshots cannot be read. |
| Expired snapshots per cleanup call | 1–16; default 8, processed in expiration order. |
| Existing cell audit capacity | 100,000 events, including creation/read/cleanup audit events. Full audit storage blocks operations that cannot append their required audit record. |

Storage numbers count UTF-8 manifest bytes, not SQLite indexes, WAL, free pages, backups or process resident memory. Capture keeps only a bounded source batch/page plus the bounded page-commitment index in application memory. The 128 MiB figure is a logical snapshot cap, not an OS memory reservation or a measured production capacity. Concurrent HTTP admission, SQLite timeouts and the existing dedicated-cell limits still apply; capture blocks other writes in its cell while it completes.

Artifact projection contains only ID, SHA-256, byte length and creation time. Plan projection contains only ID, digest, connection generation, policy version and expiry. Audit projection contains typed audit metadata and hash-chain provenance. The queries do not select artifact/source bytes, plan payloads, connection configuration, credential profiles, tokens or provider receipts. Scope/actor identifiers and audit history remain customer-sensitive metadata and require protected storage and transmission.

Expiration immediately prevents further reads but does not delete cache rows automatically. An authorized call to `PurgeExpiredPagedScopeExports` checks each stored commitment, removes only expired protocol snapshot/page rows and appends `ExpiredPagedExportPurged`. It leaves source, credential and audit tables intact. A corrupt/missing page rolls back that cleanup transaction. Unexpired snapshots and legacy exports remain. The receipt reports `sourceOrAuditDeleted=false` and `secureErasureVerified=false`; SQLite deletion does not prove secure erasure from disk, WAL or backups. Artifact retention follows its separate reviewed workflow.

## Local verification

Focused tests use new disposable SQLite databases, synthetic identities and test payloads. They exercise more than 1,000 artifact metadata rows, more than 1,000 genuine audit events, exact ordering/completeness, response limits, canonical-envelope verification, durable recovery, scope/owner isolation, role/version/policy changes, malformed/swapped cursors, rehashed tampering, source mutation/retention, expiry and audited cleanup, page-count/item bounds, storage failure rollback and cancellation after pages/root have been staged. No existing customer database, provider session, cloud endpoint or deployment is used.

Run from the repository root with the supported .NET SDK and audited locked restore:

```powershell
dotnet restore tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj --locked-mode --artifacts-path artifacts/foundation-paged-check -m:1 -nodeReuse:false -p:BuildInParallel=false
dotnet test tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj --no-restore --artifacts-path artifacts/foundation-paged-check -c Release --filter FullyQualifiedName~FoundationPagedExport --logger 'trx;LogFileName=paged-export.trx' --results-directory artifacts/foundation-paged-check/results -m:1 -nodeReuse:false -p:BuildInParallel=false
```

The 24 October 2026 focused run passed **29 tests, 0 failed, 0 skipped**, with audited locked restore and a Release build. Its receipt is `artifacts/foundation-paged-check/results/paged-export.trx`. This focused receipt does not establish full-suite, production-load, external audit trust, provider/model, Entra or Azure acceptance.
