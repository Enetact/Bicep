# Local latest-set checkpoints for credential recovery

Revision 22 adds a separate [source-v2 retained artifact](foundation-scoped-recovery.md). This v1 checkpoint authority has no scoped-set overload, implicit conversion or fallback. Scoped latest-set acceptance and scoped destination staging remain separate protocol work.

`CredentialRecoverySetCheckpointAuthority` records the **latest accepted signed
credential recovery set** for one configured customer/cell/source namespace and
one fixed signing-key identity. Acceptance verifies the exact reviewed artifact,
its signature and retained metadata, then authenticates the actual retained
credential ciphertext with existing source keys before appending a checkpoint.
It does not substitute the current vault's files for the retained bytes.

This is a cooperating local Windows-owner custody mechanism. It is not an
independent latest-set witness, WORM storage, a key backup, human approval or
promotion authority. A privileged owner who restores a valid prefix or the
entire authority root can roll back its locally observed head. The source
generation is the signed producer's counter; increasing it does not independently
prove that its contents are the newest live-vault state.

## Provision once and pin the authority

Use a fresh canonical local directory under an already trusted existing parent.
Provision reserves an owner-private root atomically and writes its guard before
the identity marker. Existing empty, partial or unrelated entries are refused
without adoption, permission repair or cleanup. Linked/ambiguous paths and
protected software, activation, cell, credential/key/rollback or recovery-stage
namespaces are refused. Immediate-parent custody and the root's owner-private
permissions are rechecked for existing operations and after external awaits;
higher-ancestor and OS-administrator trust remain operational responsibilities.

```csharp
var authority = CredentialRecoverySetCheckpointAuthority.Provision(
    freshCheckpointDirectory, configuredSourceNamespace,
    configuredSignerKeyId, configuredSigningPublicKeySha256, trustedClock);
var authorityIdentity = authority.DescribeIdentity();
```

Persist the expected identity through separately governed configuration. Its
commitment binds the exact root, Windows owner, random authority ID, source
namespace and fixed signer. Reopening an existing authority uses its constructor
with the same configured namespace/signer pins; it does not re-provision it.
The marker contains private custody metadata and needs protected storage even
though it contains no encryption/signing key or credential payload.

```text
checkpoint-root/
  .credential-recovery-checkpoint.guard
  .credential-recovery-checkpoint.json
  checkpoint-0001.json
  checkpoint-0002.json
```

No encrypted recovery artifact is automatically copied into this directory.
Retain each [private signed set](foundation-credential-recovery-set.md) separately
and confidentially. No source key, signing private key or DPAPI recovery material
is exported or provisioned by checkpoint acceptance.

## Accept an exact reviewed candidate

```csharp
var head = authority.ReadCurrent();
var accepted = await authority.AcceptNextAsync(
    selectedPrivateSet, separatelyReviewedArtifactSha256,
    configuredPinnedSigningPublicKey, existingSourceKeyProvider,
    head?.Sequence ?? 0, head?.Sha256, cancellationToken);
```

The artifact pin is a trusted-owner selection, not a digest automatically copied
from an untrusted incoming packet. Acceptance requires exact current sequence
and head hash. The candidate must have the configured full source namespace,
customer/cell and signer, a new set ID and a strictly greater source generation.
The public RSA key and private retained artifact are detached before any source
key-provider await. Signature/lifetime validation and actual retained-byte AES
authentication finish before a checkpoint file is created. Expired or revoked
profiles remain recovery dependencies; authenticating them does not renew or
enable them.

An exclusive checkpoint guard spans candidate validation, every source-key
await, final authority/time checks and append/readback. There is no database
transaction or application audit write, no generic authorization callback and no
default HTTP/MCP/AHP/model/worker route. The result binds the accepted artifact,
source inventory, rehearsal digest, source lifetime, predecessor and authority
commitment. Successful flags remain `KeyBackupVerified`, `PromotionAuthorized`,
`ActivationPerformed` and `ExternalExecutionAuthorized` false, with
`IndependentBackupCoverage = "NotAssessed"`.

Before append, candidate, key, cancellation, stale-CAS or custody refusal leaves
the authority history unchanged. Once append is attempted, failure can leave
partial or completed output and is `CredentialRecoveryCheckpointAppendUncertain`.
Do not overwrite, truncate, repair or automatically retry it. Read the existing
head explicitly with the original trusted configuration; a lost response may
coexist with a complete accepted record. Malformed partial history fails closed
and remains for owner investigation. These local fixtures do not qualify disk
full, power-loss durability or independent recovery of the authority itself.

## Hold latest selection through its consumer

```csharp
using var lease = authority.AcquireReadLease(
    separatelySelectedSequence, separatelySelectedHeadSha256);
lease.AssertCurrent();
// Retain this concrete lease for the entire authorized consuming operation.
```

Shared read leases permit cooperating readers and block append. A lease binds
the exact current head and authority identity; an old valid artifact or its own
DTO cannot choose an older head after a newer acceptance. Disposed/stale leases
cannot supply current authority.

The [checkpoint-backed staging composition](foundation-credential-checkpoint-staging.md)
retains this lease across the complete existing fresh inactive staging flow.
It compares configured authority identity, exact current set and reviewed plan
before source/destination keys or output reservation. It still returns only a
held, unusable stage: no live vault adoption, source-host fencing or activation.
It does not create a customer review admission. The separate
[durable multi-scope review](foundation-credential-recovery-review.md) remains
required for its own read-only verification workflow; its empty-profile and
unscoped-absence refusals are unchanged. Authenticated absence-scope provenance
and actual reviewed promotion are separate future gates.

## Bounds and evidence

History is capped at **1,024 checkpoints**, **16 KiB per metadata file** and
**16 MiB of checkpoint records**. Existing signed-set/rehearsal limits remain in
force. Acceptance has a 20-second cooperative deadline; an uncooperative source
key provider retains the operation, private buffers and exclusive guard until
it actually returns. This is not hard process termination or a cleanup service.

Metadata parsing rejects unknown/duplicate fields, invalid fixed flags,
cross-bound identities, gaps, invalid names, corrupt digests and non-increasing
generations. Hash-chain integrity detects inconsistent observed history; it
cannot prove that a valid tail was not removed by the owner.

[Independent native boundary tests](../tests/SelfService.Foundation.Tests/CredentialRecoveryCheckpointBoundaryTests.cs)
use only fresh synthetic roots, generated signing keys, retained synthetic
credential sets and existing test key providers. They cover custody/namespace
preservation, actual authentication before append, signer/key/input freezing,
CAS/read-lease races, malformed history, time and capacity boundaries, and
retained uncertain output. Separate integration tests cover checkpoint-backed
staging. No real credentials, key export, Azure operation, customer migration,
primary session or production recovery is qualified by this local evidence.
