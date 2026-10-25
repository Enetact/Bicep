# Authenticated credential recovery dependencies

Revision 22 adds `InspectScopedRecoveryDependenciesAsync` as the separate [complete source-v2 observation](foundation-scoped-recovery.md). It authenticates both present envelopes and original absence proofs and includes their exact source-key union. The v1 API described below still refuses source v2. A parser-reconstructed scoped inventory is metadata only; authentication claims belong to actual capture/rehearsal operations.

Revision 21 adds a separate [source absence proof inspector](foundation-absence-scope-inspection.md) for fresh source-v2 roots. It reports authenticated absence dependencies only and does not authenticate positive credential envelopes or replace this v1 recovery inventory. This inventory and signed capture explicitly refuse source v2 until their versioned retained-proof contracts are implemented. No absence scope is inferred from current connections, memberships or caller metadata.

Revision 17 uses the existing authenticated inventory and retained source guards to create an explicit [signed encrypted recovery set](foundation-credential-recovery-set.md). A separate [read-only rehearsal](foundation-credential-recovery-rehearsal.md) authenticates the captured bytes using existing source keys; current inventory alone does not prove a retained backup can be read. Independent storage/key backup coverage and key retirement remain unqualified.

`CredentialVault.InspectRecoveryDependenciesAsync` observes every current encrypted
profile in one existing customer/cell vault. It authenticates the profiles and
returns immutable public metadata identifying their source-key dependencies.
Expired and revoked profiles remain dependencies: they are not silently omitted
because they cannot currently supply a dispatch credential.

This is an explicit trusted-owner C# custody API. It has no HTTP, AHP, MCP, model,
default-worker or Owner CLI registration, product role grant or SQLite operation.
It neither grants credential-use permission nor records a product audit event.
The result spans the configured cell's application scopes; an application must
not expose it as a customer-facing scope-filtered API without its own reviewed
authorization and private evidence handling.

## Existing custody and explicit identity

```csharp
// Select an already provisioned, reviewed vault/key provider through trusted composition.
// Obtain the expected namespace pin from that trusted setup, not an incoming inventory.
var expectedNamespace = vault.DescribeNamespace();
var inventory = await vault.InspectRecoveryDependenciesAsync(
    expectedNamespace, cancellationToken);
```

In a long-lived composition, retain the reviewed namespace pin instead of
silently accepting a replacement namespace on every call. The supplied customer,
cell, vault namespace/commitment and optional authority namespace/binding must
match exactly before any source-key lookup. The normal constructor remains
existing-only: this operation provisions no directory or key, repairs no ACL,
adopts no cache and discovers no browser, CLI or desktop login.

The method retains the existing vault guard followed by the optional separate
rollback-authority guard throughout enumeration, asynchronous authentication,
source revalidation and report creation. Cooperating replacement, revoke,
rewrap and reconciliation operations therefore cannot cross that observation.
There is no database transaction held during key lookup.

## Complete current observation

Only the exact required marker/guard/binding files and canonical hashed profile
filenames are admitted. Every entry receives existing namespace, owner/ACL,
file and link checks. Unknown files or directories, missing required metadata,
interrupted `.write-*` or authority `.state-write-*` files, oversized files and
inconsistent bindings refuse the entire inventory unchanged. Normal rollback
operations may preserve interrupted files for owner investigation; this stricter
inventory does not treat them as a complete recovery observation.

Strong mode requires a structurally valid committed authority with no pending
write anywhere. Every current profile must match its committed envelope hash,
length and generation, and every committed present profile must exist. A
committed absent generation-zero entry does not create a profile dependency.
The inventory never accepts, abandons or reconciles a Pending write.

Each profile is decrypted and authenticated through the existing source-key
lookup. The configured destination/write key is not looked up or reported as a
stored dependency. An unavailable source key, altered envelope or invalid public
metadata refuses the whole operation. An abstract `ICredentialKeyProvider` can
establish only the source lookups it performs: this does not inventory an entire
key namespace, key versions, DPAPI master-key recovery or an external KMS.

After the awaited reads, all vault/authority entry names, exact bytes, namespace
bindings and committed state are checked again under the retained guards.
There is no partial-success report or automatic cleanup. Limits are 256 profiles,
264 combined custody entries, 128 KiB per envelope, 16 KiB per small metadata
file, 1 MiB for authority state, a 40 MiB aggregate source-byte budget and a 1 MiB
serialized report. Generation and authority-revision counters permit the terminal
safe JSON integer `9007199254740991`; observation reserves no future write.

The linked operation deadline is twenty seconds and is **cooperative**. Caller
cancellation returns a sanitized `OperationCanceledException` with the caller's
token; the operation's own deadline returns an opaque inventory-timeout code.
A trusted key provider must honor cancellation. If it does not, the inventory
retains its guards until that awaited lookup finishes, then refuses to return a
successful report. It does not detach work, release custody around an unfinished
lookup or promise a hard scheduling deadline. Filesystem failures use fixed
opaque errors instead of returning paths or exception/provider bodies.

## Report and commitments

`CredentialRecoveryInventory` has no public constructor or mutable collections.
Its sorted `ProfileDependencies` contain hashed profile/scope identities,
project/environment, generation, enabled state, Unix-millisecond expiry,
authenticated source-key ID and exact encrypted-envelope SHA256/length.
`CurrentRequiredKeyIds` is the sorted distinct union derived from **all** those
source-key IDs. Secret-shaped public identifiers are refused. Raw profile IDs,
scope JSON, tokens, encrypted envelope bodies, key material and filesystem paths
are absent from the result.

Expiry is projected to Unix milliseconds, including valid pre-1970 instants.
Existing offsets and sub-millisecond precision are preserved in the authenticated
envelope and bound by its exact SHA256; the displayed millisecond value truncates
that finer precision. `ObservedUtc` is normalized to UTC milliseconds.

The canonical digest uses UTF-8 JSON with camel-case names in this fixed order:
schema, customer/cell, vault namespace/commitment, optional authority
namespace/binding/revision/state hash, `observedUnixMilliseconds`, sorted profile
dependencies, sorted required keys, independent-backup coverage and retirement
flag. Profile properties use their declared contract order. `Sha256` is the
lowercase SHA256 of that payload, excluding `Sha256` itself. The internal
integrity check recomputes the complete digest and key union before the
[retention assessor](foundation-credential-recovery-retention.md) accepts it.
This is local content consistency, not a signature or independently retained
proof of origin/freshness. Metadata remains customer-sensitive.

Every inventory unconditionally states:

- `IndependentBackupCoverage = "NotAssessed"`.
- `KeyRetirementAuthorized = false`.

An empty inventory or disappearance of a key from current profiles cannot prove
that retained encrypted backups no longer need that key. There is no public
inventory parser, backup discovery, credential/key export, restore, import,
renewal, key retirement, deletion or service-identity migration in this API.
Independent key/authority backup, protected retention and supported recovery
remain prerequisites to any retirement decision. The existing
[namespace](foundation-credential-namespaces.md), [rollback](foundation-credential-rollback.md)
and [maintenance](foundation-credential-maintenance.md) trust limits continue to
apply: these local guards do not defeat the current OS owner/administrator,
hostile ancestors, rollback of all custody stores or power-loss/storage faults.

## Local qualification

The inventory and boundary fixtures use fresh private Windows namespaces,
synthetic profile material, generated keys, real temporary DPAPI custody and
paused key providers. They cover complete inactive/rotated/empty observations,
exact namespace/source-key binding, metadata privacy and immutable digests,
capacity and safe counters, concurrent custody ordering, cancellation and
changed/partial/tampered source refusal. Existing file bytes, entries and ACLs
are compared around inspection. Exact results belong in the dated validation
record; these fixtures touch no real credential cache, cloud provider, primary
session or application lifecycle and grant no production or commercial clearance.
