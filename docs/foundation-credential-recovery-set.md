# Signed private credential recovery sets

Revision 22 adds [separate source-v2 signed retained recovery](foundation-scoped-recovery.md). `CaptureScopedSignedRecoverySetAsync`, `CredentialScopedRecoverySet` and `CredentialScopedRecoverySets` carry exact scoped state and proof bytes under distinct types/schemas; retained rehearsal authenticates both AES and original proof MACs. This v1 capture/parser contract remains unchanged and refuses source v2. Destination staging, review and checkpoint acceptance still require their own scoped protocol.

Revision 21 introduces [fresh source-v2 absence proofs](foundation-absence-scope-provenance.md). The existing v1 signed-set capture/parser/rehearsal contract is unchanged and does not carry those proofs. Source-v2 capture is explicitly refused before signing/key work; versioned retained-proof capture, rehearsal and checkpoint/stage/review propagation remain required before source-v2 recovery eligibility. Legacy hash-only absences remain unscoped.

Revision 20 adds [durable local selection of the current accepted signed set](foundation-credential-recovery-checkpoints.md). The authority verifies the exact artifact, fixed namespace/signer and retained encrypted profiles before append. [Checkpoint-backed staging](foundation-credential-checkpoint-staging.md) consumes that separately held head. Set generation remains source metadata; an accepted local chain is not proof of global latest state or independently recoverable keys.

Revision 18 consumes separately selected sets through [fresh inactive recovery staging](foundation-credential-recovery-staging.md). The staged destination uses an explicitly reviewed existing key and remains unusable as a normal vault. [Completed-output verification](foundation-credential-recovery-stage-verification.md) authenticates destination ciphertext and exact profile commitments; neither staging nor verification supplies independent latest/key custody or activation authority.

`CredentialVault.CaptureSignedRecoverySetAsync` captures exact credential-envelope
and custody bytes under the existing vault/rollback guards and signs their
commitments. `CredentialRecoverySets.Parse` and `Verify` admit an explicitly
selected retained artifact without reopening or adopting the source namespace.
This is an explicit trusted-owner C# API; it adds no filesystem output command,
HTTP/AHP/MCP/model route, role, database migration or default worker operation.

Credential values remain AES-encrypted. **The whole artifact is not encrypted.**
It also contains unencrypted private custody and scope metadata, including
original paths and Windows SIDs inside retained namespace/binding files. Base64
encoding supplies no confidentiality. The caller must protect artifact storage,
transport and access; ordinary JSON serialization of the opaque set is blocked.
No encryption key, DPAPI key file or signing private key is included.

## Capture and select exact bytes

Use an already provisioned vault, its exact governed namespace identity, and an
already available signing handle. A public-only signer is insufficient. Signing
preflight precedes source-key reads; the actual completed signature is checked
before a successful set is returned. No private-key export or key provisioning
is performed.

```csharp
using var captured = await vault.CaptureSignedRecoverySetAsync(
    configuredSourceNamespace, signingHandle, reviewedSignerKeyId,
    reviewedGeneration, validFromUtc, validUntilUtc, cancellationToken);

// Creator-side only: retain this selection through separately governed custody.
var exactSelection = CredentialRecoverySets.Authority(captured);

// An explicitly supplied private destination; the API does not choose a file path.
captured.WritePrivateArtifact(ownerControlledPrivateStream);
```

The names in this example are trusted composition inputs, not customer/model
parameters or embedded secrets. The caller owns the destination stream and its
permissions, confidentiality, publication policy and lifetime. A failed stream
write can leave partial output for the caller to preserve and inspect; it is not
a successful recovery artifact and must not be automatically adopted.

Capture authenticates the complete admitted profile set, including expired and
revoked envelopes, using their existing source keys. It retains the vault and
optional rollback-authority guards through collection, authentication, signing
and final source checks. Unknown entries, partial writes, global Pending,
changed custody or unavailable source keys refuse the whole capture. No source
file is repaired, cleaned up, rewrapped or revoked by capture. No successful
partial set is returned.

The signed metadata includes the exact namespace identity, captured inventory,
profile/file counts, content hash/length, explicit generation and validity window.
RSA-PSS/SHA256 uses the `credential-recovery-set` signature kind; it cannot be
substituted for a signed SQLite snapshot. Generation is metadata supplied by the
trusted creator, not a monotonic register or a claim of latest custody.

## Parse and verify a retained set

```csharp
using var retained = CredentialRecoverySets.Parse(privateArtifactBytes);
var verified = CredentialRecoverySets.Verify(
    retained, separatelySuppliedExactSelection, pinnedPublicKey,
    configuredSourceNamespace, trustedClock);
```

`separatelySuppliedExactSelection` is a `CredentialRecoverySetAuthority` containing
customer/cell, set ID, generation, exact serialized artifact SHA256, signer ID and
public-key SHA256. Obtain it from independently selected configuration/custody.
Do not call `Authority(incomingSet)` to invent the authority for that same
unreviewed packet. Both the exact set pin and full namespace identity, including
optional rollback-authority binding, must match before public-key access.

Parsing enforces bounded structure and commitments; parsing alone does **not**
verify a signature or authorize a set. Verification additionally checks the
configured public signer, canonical RSA-PSS signature and current issue/validity
window. Canonical Base64, exact fields, duplicate/case-ambiguous properties,
file order, names, lengths and hashes are checked. Retained namespace and
rollback metadata must agree with the signed inventory and exact profile set.
Paths inside retained metadata are provenance, never instructions to open,
import or rebind a live directory.

Verification has no key provider. A valid signature/content result does not
demonstrate that retained ciphertext can be decrypted now, that a key is backed
up, or that the artifact is the newest valid capture. An older set can still be
verified when its own exact selection is deliberately supplied and its lifetime
is valid; a newer set's pin cannot be substituted for it.

The [read-only recovery rehearsal](foundation-credential-recovery-rehearsal.md)
authenticates the retained envelope bytes with already available source-key
handles. It does not use a healthy current vault as a substitute for old-byte
verification. Rehearsal remains separate from import, restore, namespace adoption,
key recovery, token use, activation and execution authority.

## Bounds and failure preservation

The artifact is limited to **64 MiB**, its decoded content to **47 MiB**, with at
most **256 profiles and 264 files**. The existing inventory's total custody-byte
and individual envelope/metadata limits remain enforced. Only exact admitted
vault and optional rollback filenames are permitted; no key file, arbitrary
attachment or filesystem traversal path is adopted. Unknown or inconsistent
content is rejected even when its outer content digest is recomputed.

The returned set owns private serialized bytes and implements `IDisposable`.
Parsing copies the supplied bytes, and explicit stream export uses a detached
copy. Disposal clears the owned serialized byte array; export/validation of a
disposed set is refused. This is not a guarantee that every managed string,
caller buffer, stream, dump or storage copy has been erased. The caller retains
custody of any exported copies.

Capture uses the existing cooperative operation deadline and cancellation model.
An uncooperative key/signing dependency can retain the caller and source guards
until it returns; this is not a hard process-termination deadline. Final source
and validity checks must pass before success. A failure grants no restore or
retirement authority and does not justify deleting retained evidence or retrying
another mutation as reconciliation.

## Remaining recovery gates

`CredentialRecoverySetVerification` exposes signed public commitments and an
immutable inventory, not private file bodies. `IndependentBackupCoverage` stays
`NotAssessed`. `KeyBackupVerified`, `KeyRetirementAuthorized`, `RestoreAuthorized`,
`ActivationPerformed`, `ExternalExecutionAuthorized` and
`ProductionRecoveryQualified` are always false.

Signed retained bytes are useful recovery evidence, but do not supply independent
storage, greatest-ever/latest-set proof, rollback-resistant witness custody,
source encryption keys, DPAPI user/machine recovery material or a signing-key
backup. They do not defeat a malicious owner/admin or qualify another account,
machine, operating system, power-loss event or production disaster recovery.
The [local SQLite checkpoint authority](foundation-backup-checkpoint-custody.md)
has a separate format and still does not independently resist whole-root rollback.
Do not treat the two mechanisms as an atomic backup of database, credentials and
keys. [Advisory key-retention holds](foundation-credential-recovery-retention.md)
remain references, not deletion enforcement or key availability proofs.

## Local qualification

[CredentialRecoverySetBoundaryTests](../tests/SelfService.Foundation.Tests/CredentialRecoverySetBoundaryTests.cs)
uses fresh synthetic vaults and ephemeral signing keys for exact private
roundtrip, detached/disposed bytes, strict parsing/Base64/signatures, selection
and namespace refusals, resigned inconsistent contents, signer preflight/output,
capture preservation, guard races and bounds. Actual retained-byte decryption is
qualified by the separate rehearsal tests. These fixtures access no real
credentials, customer recovery directories, cloud service or primary session.
