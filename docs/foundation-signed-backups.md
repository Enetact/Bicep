# Independently signed foundation backups

Implemented source, 24 October 2026. The dedicated-cell library can create independently signed SQLite snapshot commitments and verify a separately pinned latest restore authority before touching a restore destination. This supplements the [existing backup/restore API](../src/SelfService.Foundation/FoundationStoreBackup.cs) and [signed audit anchors](foundation-security-operations.md#signed-audit-anchors-and-recovery-custody). It preserves their local recovery holds and their production custody limits. See the [foundation service](foundation-service.md) and [commercial release plan](plans/platform-studio-commercial-release.md).

The implementation is a trusted cell-owner C# API. It supplies no HTTP/model signing endpoint, private-key import command, provider authentication, cloud deployment or automatic disaster-recovery activation. Existing unsigned `CreateBackup` and `RestoreBackup` APIs remain available to trusted composition. Operators who require signatures must select the signed path and protect that composition against bypass.

## Commitments and custody

`FoundationBackupSignature` binds these fields with RSA-PSS/SHA-256:

| Field | Binding |
|---|---|
| `version`, `kind` | Exact version 1 and `cell-snapshot` |
| `manifestId`, `generation` | Unique manifest identity and explicit positive custody generation |
| `customerId`, `cellId` | Dedicated customer and cell |
| `cellSchemaVersion` | Actual captured SQLite schema version |
| `snapshotSha256`, `snapshotLength` | Exact standalone database bytes |
| `snapshotCreatedUtc`, `issuedUtc` | Snapshot and signature issue times |
| `notBeforeUtc`, `expiresUtc` | Explicit validity window; expiry is exclusive |
| `keyId`, `publicKeySha256`, `algorithm` | Pinned key identity, public SubjectPublicKeyInfo fingerprint and exact `RSA-PSS-SHA256` |

The physical `DatabasePath` is intentionally excluded. A copied or moved snapshot can retain its signed content identity. Other unsigned `FoundationBackup` metadata must still match the verified commitment. A file path is neither customer authority nor proof of backup integrity.

`FoundationBackupRestoreAuthority` is a separate trusted checkpoint containing customer/cell, manifest ID, generation, signed-manifest digest, key ID and public-key fingerprint. `FoundationBackupSignatures.Authority(manifest)` derives this checkpoint when the trusted owner creates a manifest. Store that accepted checkpoint through independently protected latest-authority custody. During restore, obtain it from that custody, rather than deriving it from the incoming packet or trusting a checkpoint shipped beside the backup.

The module requires exact equality to this latest checkpoint. An older correctly signed snapshot cannot pass against a newer pin. The trusted custody system must enforce monotonic generation advancement and prevent rollback of its own latest checkpoint; this library supplies no remote monotonic register or multi-host coordinator. A privileged owner who can replace the signer, backup and latest authority together can replace the evidence.

## Create and retain a signed snapshot

Trusted host composition uses:

```csharp
var bundle = store.CreateSignedBackup(
    snapshotPath, signingHandle, reviewedKeyId, nextCustodyGeneration,
    validFromUtc, validUntilUtc);
var latestAuthority = FoundationBackupSignatures.Authority(bundle.Manifest);
```

The example names refer to already governed host inputs. `signingHandle` is an explicitly supplied RSA handle; no key material belongs in manifest JSON, logs, examples or customer/model requests. The signer may retain private-key custody outside the application. Verifiers receive only the independently pinned public key.

Creation validates key structure, signing capability, key ID, generation and time-window shape before `CreateBackup` can create a destination. It then reuses SQLite's online backup operation, standalone-snapshot validation and `BackupCreated` audit event. A held read handle covers byte hashing and signature creation. The signed schema comes from the snapshot itself. Successful signing also writes a `BackupSignatureCreated` event containing the manifest digest.

The signed manifest and database are separate artifacts. Snapshot creation can succeed before a later I/O/signing/audit failure; retain any resulting unsigned snapshot as unqualified operational evidence until its owner explicitly handles it. A failed signing operation never supplies a successful signed bundle. Existing snapshot destinations are never overwritten.

Snapshots contain customer database data and remain unencrypted by these APIs. A signature protects integrity and origin under the public-key pin; it does not provide confidentiality, secure deletion, WORM storage or protected retention. Protect snapshots, manifests, encryption/credential keys and latest-authority checkpoints in their respective custody boundaries.

## Verify and restore

Parse incoming manifest JSON through `FoundationBackupSignatures.Parse`, then construct the packet with its explicitly selected local snapshot path. Restore with authority and binding taken from trusted configuration/custody:

```csharp
var restored = FoundationStore.RestoreSignedBackup(
    incomingBundle, newRestorePath, independentlyStoredLatestAuthority,
    pinnedPublicKey, expectedCustomerId, expectedCellId, trustedClock);
```

The sequence is:

1. Validate bounded manifest shape and supported schema/algorithm.
2. Match the explicitly expected customer/cell and the independent latest checkpoint.
3. Match the pinned public key and verify RSA-PSS/SHA-256 over canonical payload bytes.
4. Verify issue time, activation time and expiry against the supplied trusted clock.
5. Check unsigned packet metadata, reject linked paths, and open the source read-only. Recheck its length/hash, actual SQLite schema, customer/cell, integrity, foreign keys and audit chain.
6. Delegate to existing `RestoreBackup`, which creates a fresh destination, rechecks the snapshot, advances the recovered cell policy and holds recovered queued/claimed/dispatched jobs as `OutcomeUnknown` with `RecoveryRequiresReconciliation`.

Signature, latest-authority, customer/cell and lifetime failures occur before the source is opened or any target directory/file is created. Source content/schema failures also precede target creation. Successful signature verification returns detached immutable `ValidatedFoundationBackup` claims; they report `sourceHostFenced: false` and `externalExecutionAuthorized: false`.

The existing source host and source queue remain unchanged. Fence the original host before activating a recovered copy. Policy advancement inside the recovered database cannot fence an independent original database, revoke external credentials or prevent two hosts from running. External-effect reconciliation, key recovery, protected service identity, RPO/RTO, multi-host activation and production recovery custody still need separate implementation and acceptance.

## Validation boundaries

Manifest parsing accepts at most 16,384 UTF-8 bytes, rejects unknown/missing/duplicate/case-ambiguous fields, and uses fixed canonical field order with UTC Unix-millisecond timestamps. Only 2,048–8,192-bit structurally valid RSA keys and RSA-PSS/SHA-256 are accepted. Canonical Base64 signatures are required. No public key supplied by an incoming manifest becomes trusted automatically.

Generations must be between 1 and 9,007,199,254,740,991. Snapshot lengths must be positive and no more than 2 GiB. Schema versions must be supported by the current store. Timestamps require UTC offset zero and millisecond precision; snapshot creation cannot follow signature issue, the validity window must be ordered, and validity cannot extend more than 366 days after issue. Future-issued, not-yet-valid and expired signatures fail closed with no clock grace. Clock and latest-checkpoint custody are explicit operator responsibilities.

The canonical signature excludes `signature` itself. The latest authority additionally commits to the complete normalized signed manifest, including its signature. Use the provided `CanonicalPayload` and `Authority` helpers instead of implementing alternative field ordering or digest rules.

## Local evidence

[FoundationBackupSignatureTests](../tests/SelfService.Foundation.Tests/FoundationBackupSignatureTests.cs) uses new temporary SQLite cells and ephemeral RSA keys. It tests genuine signed restore, queued-work quarantine, snapshot relocation, an older signature against a newer pin, wrong keys/bindings, changed claims and bytes, source-schema mismatch, strict parsing, bounded fields, incompatible padding and clock validity. It also verifies that rejected manifests/signers do not create restore/snapshot destinations. [Existing backup tests](../tests/SelfService.Foundation.Tests/FoundationStoreBackupTests.cs) continue to exercise the preserved unsigned operational APIs.

These checks establish local cryptographic and SQLite behavior. They do not establish protected production signer/latest-pin custody, original-host fencing, encrypted backup storage, real identity/provider acceptance or a commercially complete recovery service.
