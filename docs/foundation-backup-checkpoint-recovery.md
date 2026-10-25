# Recovery using protected backup checkpoint custody

`FoundationBackupCheckpointRecovery` composes the [local checkpoint authority](foundation-backup-checkpoint-custody.md) with the existing [signed backup verifier and restore API](foundation-signed-backups.md). This explicit trusted-owner C# path obtains the latest accepted checkpoint from its configured authority while retaining the authority's shared read guard. The incoming backup packet cannot provide or override that latest checkpoint.

This is implemented local Windows composition. Existing trusted-caller `FoundationStore.VerifySignedBackup` and `RestoreSignedBackup` methods retain their explicit `FoundationBackupRestoreAuthority` parameter. Existing [Owner CLI](foundation-owner-cli.md) backup commands still use that legacy contract; they do not automatically select the new authority. There is no new HTTP, AHP, MCP, model, worker, activation or pipeline route, signing-key loader, credential migration or live deployment.

## Configure and use

Provision and protect the checkpoint directory separately using its documented owner process. Pin the complete `FoundationBackupCheckpointAuthorityIdentity` from separately reviewed configuration: customer, cell, authority ID, signer key ID, public-key SHA256 and authority-header commitment. The commitment includes the configured root and Windows owner identity. Do not adopt a new identity from a packet or automatically replace a mismatched configured pin with `DescribeIdentity()` output.

```csharp
var authority = new FoundationBackupCheckpointAuthority(
    configuredAuthorityDirectory, expectedCustomerId, expectedCellId,
    reviewedSignerKeyId, reviewedPublicKeySha256, trustedClock);
var recovery = new FoundationBackupCheckpointRecovery(
    authority, independentlyPinnedAuthorityIdentity, trustedClock);

var verified = recovery.VerifySignedBackup(incomingBundle, pinnedPublicKey);

// This directory must not exist; its immediate parent must already exist.
var staged = recovery.RestoreSignedBackup(
    incomingBundle, newOwnerDesignatedStagingDirectory, pinnedPublicKey);
FoundationStore restored = staged.Store!;
```

These names represent governed inputs supplied by trusted composition. `pinnedPublicKey` is an independently selected public RSA handle. The recovery API does not discover keys, import private material, sign a manifest or accept the packet's public key as trusted. Snapshot paths must be canonical absolute paths on a fixed local Windows drive, with no linked path component, UNC location, relative component or alternate data stream.

Each operation acquires the authority read lease, checks all configured identity fields and takes `Checkpoint.RestoreAuthority` from that lease. It retains a read-only source handle with `FileShare.Read`, excluding ordinary Windows writers, deletion and replacement during verification and recovery. It runs the existing signature, latest checkpoint, binding, lifetime, exact bytes, standalone SQLite, supported schema and audit-chain checks, and revalidates current authority before returning. The shared checkpoint guard excludes cooperating `AcceptNext` calls until completion; they receive `BackupCheckpointBusy`, with no automatic wait or retry. Other checkpoint readers can coexist.

There is no database transaction retained while waiting for an asynchronous dependency. These recovery methods are synchronous local operations and introduce no hard I/O deadline, background task or detached key lookup. An accepted checkpoint does not suspend signature expiry: an expired, future-issued or not-yet-valid packet remains refused at use. There is no fallback to an older checkpoint when the current packet, source, key or authority is unavailable.

## Fresh staging and preservation

Full signed-snapshot verification precedes staging directory reservation. Restore requires an absent destination directory, an existing immediate parent and a canonical local path. The immediate parent must be owned by the current Windows SID, SYSTEM or Builtin Administrators and must grant no other SID effective write, append, create, delete-child, delete, ACL-change or ownership-change rights. An absent or NULL DACL is refused. Safe read access and nonapplying inherit-only entries do not grant mutation authority. The API does not repair an existing ACL or create missing parents.

The destination leaf and its ancestors are checked for protected activation, checkpoint, credential, key, rollback, installation, package and owner-initialization names, including marker directories and legacy credential/key envelope names. SQLite auxiliary filename aliases are refused. An existing file, empty directory or partial stage is never adopted or overwritten.

Reservation creates an owner-only directory atomically. Before restoring `cell.sqlite`, it creates a private `.foundation-owner-initialization.json` sentinel with a distinct `platform-studio.foundation-backup-checkpoint-recovery-stage.v1` schema. The marker binds customer/cell, configured authority identity/commitment, accepted checkpoint and signed snapshot commitments; it contains no physical paths or private keys. Other operational namespace guards recognize its existing sentinel name. It is evidence of a reserved recovery stage, not authority to activate or adopt it.

The retained checkpoint and source guards cover fresh directory/marker creation, the existing restore's repeated validation, staged schema migration and recovered-work hold. Restore rotates the database instance and policy, stops copied worker registrations and holds copied unfinished jobs and maintenance reviews under the existing recovery rules. It does not migrate the original database, activate the stage, restart a process or repeat an external effect. A returned restored store is for trusted C# composition; it is excluded from result JSON.

Invalid input creates no staging directory. A failure after this API reserves its own stage leaves that private directory and marker, plus any remaining owned partial database state, for explicit owner inspection. The underlying restore may remove the new failed database it reserved; this composition never recursively removes the directory or repairs/adopts a prior destination. A failed call supplies no successful recovery receipt. Preserve the evidence and inspect it before choosing a new destination; do not treat retry as reconciliation or activation.

## Receipts and limits

`FoundationBackupCheckpointRecoveryResult` contains the checkpoint and immutable validated backup metadata. `RecoveredWorkHeldForReconciliation` is true only after the staged restore and final authority check return successfully. Verification alone leaves `Store` null and the flag false. Result JSON contains no source/staging path or restored database instance ID.

`LocalCustodyOnly` is true. `SourceHostFenced`, `ExternalExecutionAuthorized`, `ActivationPerformed`, `KeyRetirementAuthorized`, `PrivateKeyBackupVerified` and `ProductionRecoveryQualified` are always false. `IndependentBackupCoverage` remains `NotAssessed`. A valid result does not authorize work, retire a key, prove independent backup coverage or establish production disaster recovery.

The authority's bounded chain and sharing guard coordinate cooperating callers on one Windows machine. They do not defeat the same Windows owner, an administrator, an offline filesystem replacement or rollback of the complete authority root together with an older valid snapshot. Checksums and a header commitment do not independently establish the greatest checkpoint ever accepted. Protect and verify an independent latest checkpoint outside that rollback domain for stronger assurance. Higher ancestor directories and the operating system remain trusted; only the immediate staging parent receives the effective-write custody check.

Signed snapshots contain unencrypted database data. Confidential storage, independent retention, customer isolation across service identities, original-host fencing, multi-host recovery, service-account qualification and measured recovery objectives remain separate requirements. A SQLite snapshot does not back up credential envelopes, DPAPI keys, private signing keys, Windows user/machine protection material or their recovery authorities. DPAPI recovery under another account or machine is not established by this API or its tests. The [credential inventory and retention assessment](foundation-credential-recovery-retention.md) also do not supply missing key material or independent backup coverage.

## Local verification

[FoundationBackupCheckpointRecoveryTests](../tests/SelfService.Foundation.Tests/FoundationBackupCheckpointRecoveryTests.cs) uses fresh private Windows fixtures and ephemeral RSA keys. It checks configured identity pins, empty/missing/corrupt custody, stale signed snapshots despite their matching legacy DTO, fresh private marked recovery, restored-work holds, signature lifetime/byte/sidecar checks, source and existing-target preservation, protected namespace and parent custody refusals, retained partial stages, and blocked checkpoint advance/source writes during verification and post-reservation restore validation. Public-key export pauses are synthetic test dependencies; production recovery has no callback hook.

These are local composition checks. They do not use real credentials, customer backups, cloud services or the primary portal, and establish no independent human review, production recovery or publication clearance.
