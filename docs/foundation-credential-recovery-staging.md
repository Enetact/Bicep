# Reviewed inactive credential recovery staging

Revision 20 adds [checkpoint-backed staging](foundation-credential-checkpoint-staging.md) as an explicit composition over this unchanged executor. Its read lease rejects an older still-valid set after a newer accepted checkpoint and prevents checkpoint advancement until the staging operation finishes. The trusted owner must still supply the exact reviewed stage-plan digest and existing destination key. No promotion or activation authority is inferred.

Revision 19 adds a separate [durable human review](foundation-credential-recovery-review.md) for one read-only verification of this completed inactive output. It requires standing grants across all authenticated profile scopes, rejects unscoped absences, and adds no staging, promotion or activation authority. The original trusted-owner staging selection remains distinct.

This trusted cell-owner C# flow stages an explicitly selected signed recovery
set into one **fresh, private, inactive** Windows directory. It authenticates
retained source envelopes with existing source keys and encrypts their original
profiles under the separately observed destination key. It creates no usable
`CredentialVault`, credential lease, provider token renewal or active rollback
authority. There is no default HTTP/AHP/MCP/model/worker route or database
approval workflow for this operation.

The plan digest is an exact trusted-owner review selection. It does not attest
an authenticated human approval, Entra role, scoped Operator permission or
independent reviewer. A whole recovery set can include multiple project scopes;
do not present this cell-owner API as approval granted by a single project role.

## Observe, plan and review

Use already available providers. Observation and staging never provision,
import, retire or export encryption keys.

```csharp
var destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(
    expectedCustomerId, expectedCellId, destinationKeyId,
    configuredDestinationKeys, cancellationToken);

var selected = CredentialRecoverySets.Verify(
    retainedSet, separatelySelectedSetAuthority, pinnedSourceSigningPublicKey,
    configuredSourceNamespace, trustedClock);

var plan = CredentialRecoveryStaging.PreparePlan(
    selected, separatelySelectedSetAuthority, configuredSourceNamespace,
    newPrivateStageDirectory, destination, reviewedPurpose,
    planExpiresUtc, trustedClock);
```

The exact set authority and source namespace come from separately governed
configuration, not from an incoming artifact. The caller also knows and reviews
the exact destination path supplied to planning. The plan binds that path and
Windows owner through its root commitment, plus the source set/signature,
customer/cell/namespace, destination key identity, purpose, lifetime and full
preserved-profile commitments. Planning does not create the stage directory.

The destination identity includes a domain-separated material commitment. It
detects a different key returned under the same key ID between observation and
execution. It is not key material, a provider version identifier, a backup, or a
promise of future availability. Protect the plan and its separately reviewed
SHA256 through the trusted owner process; do not automatically approve a newly
created or changed plan by copying its current digest into execution.

## Execute only the reviewed plan

```csharp
var executor = new CredentialRecoveryStageExecutor(
    configuredSourceKeys, configuredDestinationKeys, trustedClock);
var receipt = await executor.StageAsync(
    retainedSet, plan, separatelyReviewedPlanSha256,
    pinnedSourceSigningPublicKey, cancellationToken);
```

The constructor fixes both provider instances. Execution verifies the exact
plan, signed set, source scope and lifetime before using keys or creating output.
It fetches the destination key once for the operation, checks the reviewed
material commitment and retains that handle/copy through encryption. A provider
rotation after that fetch cannot silently retarget the generated output.

Source credential values are decrypted only in temporary memory and encrypted
under the destination key with fresh AES-GCM nonces. The original `CredentialProfile`
is preserved: full scope and kind, generation, Enabled value and original expiry
precision. Inventory's millisecond expiry projection is not used to reconstruct
the original profile. Expired and revoked profiles stay expired/revoked; staging
does not increment generations, refresh tokens or grant execution authority.

Source absence/generation-zero commitments remain in the staged high-water
metadata without inventing credential files. New stage/vault/authority IDs bind
the output's separate purpose. They do not revive the original namespace or
transfer its live rollback authority.

## Fresh private output and uncertainty

The canonical local destination must be absent and its immediate parent must
already exist with trusted effective-write custody. Linked/ambiguous paths,
protected operational namespaces, existing files/directories and unsafe or NULL
parent DACLs are refused without repair. Paths, custody, source/plan lifetime
and absence are rechecked after awaited dependencies before reservation.

One atomic private root reservation consumes the planned destination. The stage
guard and reservation hold precede credential files; completion is written last.

```text
new-stage/
  .credential-recovery-stage.guard
  .credential-recovery-stage.json
  profiles/<profile-sha256>.credential.json
  rollback/binding.json
  rollback/state.json
  completion.json
```

Rollback files have stage-only schemas. There is no live
`.credential-namespace.json` marker. Normal vault opening/provisioning and other
operational output guards refuse this layout, including partial stage namespaces.
No result returns a live vault or credential lease, switches a configuration
pointer, activates a runtime or dispatches a pipeline.

Before reservation, the API creates no destination and preserves any already-existing entry. After any
root reservation, a later failure is `credential-recovery-stage-outcome-unknown`.
Private partial output is preserved without recursive cleanup, repair, adoption
or automatic retry. This can include a completed file when the caller's final
observation failed. Preserve the stage for explicit owner inspection; a failed
call supplies no successful staging receipt and must not be treated as no effect.

The source set, current vault/key directories and existing destination data are
not rewritten. The source artifact and output filesystem do not share a database
transaction. No database actor approval/audit or remote key/provider transaction
is implied by the exact plan pin.

## Verify, protect and retain

The [completed-stage verifier](foundation-credential-recovery-stage-verification.md)
separately pins the plan and receipt, checks native custody/names/bytes under its
read guard and authenticates output envelopes with the destination key. It does
not open the current source vault, import the stage or authorize live adoption.
An ordinary constructor refusal is the intended inactive boundary, not an error
to fix by relabeling markers or removing the recovery hold.

Encrypted credential values coexist with private unencrypted scope/custody
metadata. Protect the entire directory and any exported plan/receipt copies.
No key or DPAPI recovery material is included. Digests and material commitments
are not signatures or independent custody. The same owner/admin, higher
ancestors, offline replacement, power-loss durability, other service identities
and production disaster recovery remain separate qualifications.

The bounded flow admits at most **256 current plus absent profile entries**,
**264 filesystem entries**, **40 MiB of staged custody bytes** and a **1 MiB
receipt**. The existing signed-set limits remain in force. The operation has a
20-second cooperative deadline; an uncooperative key provider can retain the
operation and private buffers until it returns. This is not hard process
termination or a background cleanup service.

Successful receipts keep `InactiveRecoveryHold` true and `NormalVaultUsable`,
`ActivationPerformed`, `SourceHostFenced`, `ExternalExecutionAuthorized`,
`KeyMaterialExported`, `KeyRetirementAuthorized` and `ProductionRecoveryQualified`
false. `IndependentBackupCoverage` remains `NotAssessed`. Staging and verification
do not prove future key availability, independent latest-set custody or permission
to restore into a live namespace. See the [signed recovery set](foundation-credential-recovery-set.md)
and [advisory retention](foundation-credential-recovery-retention.md) boundaries.

## Local qualification

[CredentialRecoveryStageBoundaryTests](../tests/SelfService.Foundation.Tests/CredentialRecoveryStageBoundaryTests.cs)
uses fresh synthetic roots, signing keys and source/destination encryption keys.
It checks exact plan/key/path bindings, full profile preservation, native custody
and path refusals, reservation races, partial/uncertain preservation and ordinary
live/operational refusal. Separate verifier tests cover completed-stage tampering
and destination-byte authentication. No real credential directory, customer
backup, cloud resource or primary session is changed by these fixtures.
