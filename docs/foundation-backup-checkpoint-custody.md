# Local backup-checkpoint custody

Revision 20 adds a separate [credential-set checkpoint authority](foundation-credential-recovery-checkpoints.md). This SQLite-backup authority retains its existing content kind and signing contract. Database checkpoint custody and credential-set custody remain distinct; neither establishes coordinated database/credential activation or independent key recovery.

`FoundationBackupCheckpointAuthority` stores a bounded, append-only-by-contract
latest-backup checkpoint history for one customer, cell and fixed backup signer.
It is an explicit trusted-owner C# API on the current Windows machine. It adds no
HTTP/AHP/MCP/model route, role, signing-key import, provider call or automatic
runtime activation. The [custody-backed recovery API](foundation-backup-checkpoint-recovery.md)
uses this authority instead of accepting latest authority from an incoming packet.

This local custody increment does not qualify an independently protected backup
service. A trusted OS owner or administrator can replace or roll back local
evidence. WORM storage, multi-host consensus, power-loss durability, production
service identities and independent signer/latest-checkpoint custody remain open.

## Provision an explicit fresh authority

Use a new absolute path on a fixed local Windows drive, outside cell databases,
credential/key/rollback namespaces, activation authorities, software staging and
package roots. Its immediate parent must already exist with an admitted owner
and no effective foreign write/delete/control rights. NULL or absent parent
DACLs, linked/ambiguous paths and protected ancestor or leaf names are refused.
Higher-ancestor custody remains the operator's responsibility.

Provision atomically supplies the fresh directory's private owner/DACL at birth.
It never adopts, repairs or deletes an existing directory, file or partial
namespace. Later provisioning failures preserve any newly reserved partial
authority for review; retrying provisioning against it is refused.

```csharp
var publicKeyPin = FoundationAuditAnchors.PublicKeyFingerprint(pinnedPublicKey);
var authority = FoundationBackupCheckpointAuthority.Provision(
    newAuthorityDirectory, expectedCustomerId, expectedCellId,
    reviewedSignerKeyId, publicKeyPin, trustedClock);
var identity = authority.DescribeIdentity();
```

The example's key handle and configuration come from separately governed owner
inputs. No private key is needed by this authority. Retain the complete returned
identity through trusted application configuration/custody, separately from an
incoming packet. It includes an authority ID and commitment binding the exact
root path, Windows owner, customer/cell, signer ID/fingerprint and creation time.
A copied directory is not adopted at a different path. No path/owner rebinding
or implicit signer rotation is implemented.

Reopening is existing-only:

```csharp
var authority = new FoundationBackupCheckpointAuthority(
    existingAuthorityDirectory, expectedCustomerId, expectedCellId,
    reviewedSignerKeyId, reviewedPublicKeySha256, trustedClock);
```

Construction and reads validate custody, strict metadata and the complete local
chain. They do not provision missing state. A newly provisioned authority has no
checkpoint and cannot issue a read lease for recovery yet.

## Review and accept a candidate

Capture the signed standalone SQLite bundle through the
[signed-backup API](foundation-signed-backups.md). Separately review the exact
candidate manifest commitment. Do not derive a trusted review pin or latest
checkpoint solely from an incoming packet.

```csharp
var previous = authority.ReadCurrent();
var accepted = authority.AcceptNext(
    reviewedBundle, pinnedPublicKey, separatelyReviewedManifestSha256,
    previous?.Sequence ?? 0, previous?.Sha256);
```

The previous sequence/head digest is an exact compare-and-swap input. The
exclusive authority guard is held through full signature, customer/cell,
fixed-signer, lifetime, source-byte, standalone SQLite, required-schema,
foreign-key and audit-chain verification and checkpoint append. A read handle
excludes cooperating source writes/replacement during verification. Snapshot
validation does not repair or migrate the source and does not attest every
business row.

Backup generations must strictly increase; they can skip values. The authority's
checkpoint sequence increments by one and is a different counter. The fixed
signer ID and public-key fingerprint must match; a newer generation cannot
authorize a different signer. There is no implicit rotation or network key lookup.

Rejection before append leaves the current head, source snapshot and customer
audit unchanged. Appending writes only checkpoint metadata, not the SQLite cell,
vault, credentials or signer key. These stores do not share a transaction. An
accepted checkpoint does not guarantee that source bytes will still exist later;
recovery verifies the selected snapshot again.

## Failures and concurrent operations

Concurrent appends use the same exclusive guard and exact prior-state inputs.
One successful append makes a competing/stale request invalid. There is no
automatic re-review, changed candidate or retry against a refreshed head.

`BackupCheckpointAppendUncertain` means an append was attempted and the caller
cannot treat the result as no effect. A complete checkpoint or partial file may
already exist. Preserve the authority and inspect its state through the normal
read path with trusted configuration. Do not delete a record, truncate history,
or repeat the original command automatically. A completed head discovered after
an uncertain return remains accepted local history; a damaged/partial history
is refused and requires owner review. This is not a power-loss recovery protocol.

Shared `AcquireReadLease()` guards permit cooperating readers and block appends
while [verification or fresh-directory recovery](foundation-backup-checkpoint-recovery.md)
holds the lease. Its identity/head assertions are local observations, not bearer
authorization. Disposed leases cannot be reused. Incoming bundle metadata never
selects the recovery API's latest checkpoint. Existing direct signed/unsigned
store APIs remain available to trusted code, so required-custody composition
must be protected against bypass.

## Storage and evidence limits

```text
checkpoint-authority/
  .backup-checkpoint.guard
  authority.json
  checkpoint-0001.json
  checkpoint-0002.json
```

The authority permits at most **1,024 checkpoints**, **16 KiB per metadata file**
and **16 MiB of checkpoint history**. Exact filenames, sequence, preceding digest,
authority commitment, fixed scope/signer, monotonic backup generation and record
hashes are checked. Unknown entries, gaps, truncated/changed records and widened
custody are refused without cleanup or ACL repair. There is no automatic pruning,
rollover or authority relocation.

A missing middle record is detectable. Deleting a valid history tail, rolling
back the entire authority root, or replacing both custody and the configuration
that trusts it is not independently detectable by this local chain. Its hashes
are not a signature or an external monotonic checkpoint. Protect independently
held evidence and latest pins according to a separately qualified recovery design.

The directory contains no snapshot, signing private key, credential encryption
key or rollback-authority backup. It supplies no confidentiality, secure erasure,
key retirement, source-host fencing or live-provider reconciliation. SQLite
snapshot admission and local custody do not prove that credential/key/authority
recovery is possible. Recovery results keep `IndependentBackupCoverage` as
`NotAssessed`, with private-key-backup, retirement and production-recovery
assurances false. See [credential recovery inventory](foundation-credential-recovery-inventory.md)
and [advisory key retention](foundation-credential-recovery-retention.md) for their
separate metadata-only scopes.

## Local qualification

[FoundationBackupCheckpointAuthorityBoundaryTests](../tests/SelfService.Foundation.Tests/FoundationBackupCheckpointAuthorityBoundaryTests.cs)
uses fresh synthetic directories, SQLite cells and ephemeral signing keys. It
checks native private reservation, parent ACL/NULL-DACL and namespace/link
refusals, partial preservation, full candidate admission, fixed bindings,
metadata/chain corruption, compare-and-swap races and bounds. A synthetic clock
failure after file append tests uncertain-return preservation and explicit
subsequent observation; it does not simulate disk loss. Tests do not access
real credential directories, customer backups, cloud services or primary sessions.
