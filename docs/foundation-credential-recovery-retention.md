# Credential recovery key-retention assessment

Revision 22 adds separate `CredentialScopedRecoveryRetention.Assess` for [source-v2 inventories](foundation-scoped-recovery.md#retain-keys-conservatively). Its metadata-only holds include both envelope and original absence-proof keys from current and explicitly supplied retained inventories. It performs no cryptographic authentication and does not prove complete history, independent backup or permission to retire a key. The v1 contract below is unchanged.

Revision 18 adds [inactive staged recovery](foundation-credential-recovery-staging.md) and [read-only output verification](foundation-credential-recovery-stage-verification.md). A verified stage still requires its destination key, while original signed sets retain source-key dependencies. No hold is released and no key retirement is authorized. Independently recoverable key copies, complete retained history, latest custody and promotion remain separate decisions.

Revision 17 adds [signed recovery sets](foundation-credential-recovery-set.md) and [retained-byte rehearsal](foundation-credential-recovery-rehearsal.md). A successful rehearsal proves authentication of one explicitly selected set with the supplied keys at that time. It does not establish independent key backup, complete historical coverage or permission to release these retention holds.

`CredentialRecoveryRetention.Assess` is a pure metadata operation over a current
`CredentialRecoveryInventory` and explicitly supplied retained inventory objects.
It unions their authenticated **source** key references. Each referenced key has
a `RetentionHold`, with its current-reference flag and sorted retained-inventory
SHA256 commitments. A key can have both kinds of reference.

`RetentionHold` is advisory metadata; this API does not enforce deletion
prevention in a key provider or backup store. `KeyId` is a custody reference,
not key material, a key-version fingerprint, or an attestation that the key is
available or recoverable.

This is a trusted local C# composition API. It adds no key lookup, vault write,
key deletion, import, restore, database operation, HTTP/AHP/model route, role or
registry. It does not discover backups or enumerate arbitrary keys. The caller
must supply inventories created by the existing authenticated vault inspection.
There is no public inventory parser or approval callback in this assessment.

## Use and interpretation

```csharp
var identity = vault.DescribeNamespace();
var retained = await vault.InspectRecoveryDependenciesAsync(identity, cancellationToken);

// The application's separately authorized maintenance flow may run between observations.
var current = await vault.InspectRecoveryDependenciesAsync(identity, cancellationToken);
var assessment = CredentialRecoveryRetention.Assess(current, new[] { retained });

foreach (var hold in assessment.KeyHolds)
    Console.WriteLine($"{hold.KeyId}: {hold.Decision}");
```

After rewrapping from key 1 to key 2, a current envelope can depend on key 2 while
a retained inventory still references key 1. Both keys receive a hold. Expired
and revoked envelopes retain their source-key dependency: inactivity does not
make encrypted recovery material readable without its source key.

`KeyRetirementAuthorized` is always `false`. An absent reference, an empty current
inventory, or no supplied history never means that a key is unused or safe to
retire. The assessment does not establish that the input labelled current is the
latest observation, impose chronological ordering, or infer missing history.
`CompleteHistoricalCoverageVerified` remains `false`.

`IndependentBackupCoverage` is always `NotAssessed`, and
`RetainedInventoryMetadataOnly` is `true`. Retained metadata is not verification
of a SQLite snapshot, a copied credential envelope, a recoverable key, an
independently held latest checkpoint, or an actual backup's custody and contents.
Use the separate [signed-backup verification](foundation-signed-backups.md) and
protected operational custody processes for their documented scopes. This
assessment supplies no production retention policy or retirement approval.

## Binding, bounds and commitments

All inputs must have the same full vault namespace identity: customer, cell,
vault namespace ID and commitment, plus optional rollback-authority namespace ID
and binding. An authority's revision and state hash may change across valid
observations; they remain committed by each inventory's own digest. A different
authority binding is refused. No in-place namespace rebind is adopted.

The assessor revalidates each immutable inventory's integrity before using or
deduplicating it. This checks committed object metadata; it does not repeat vault
authentication or prove that on-disk state is unchanged. Identical inventory
commitments collapse; a copy of the current
inventory does not add historical coverage. Malformed or conflicting committed
content is rejected rather than silently merged. Distinct inventories observed
at the same time remain distinct inputs.

At most **64 retained inventory inputs**, before deduplication, and **4,096
distinct referenced keys** are accepted. Each inventory retains the inspection
API's own profile and byte limits. The assessment's canonical JSON payload is
bounded to **4 MiB**. Oversized or cross-scope inputs throw a coded
`CredentialException`; no partial assessment is returned.

Inventory commitments, key IDs and per-key inventory references use ordinal
sorting. The assessment contains copied read-only collections. `CanonicalPayload()`
returns a new byte array in fixed JSON field order; its SHA256 is `Sha256` and
excludes that digest field itself. Reordering identical inputs does not change
the result. The assessor adds no wall-clock timestamp.

These digests bind the observed metadata; they are not signatures or independent
attestation. Same-owner code can construct or replace local evidence. Reported
key IDs identify custody references, not encryption-key bytes. The assessment
contains no token, ciphertext, raw profile ID, credential path, or private key.

## Local verification

[CredentialRecoveryRetentionTests](../tests/SelfService.Foundation.Tests/CredentialRecoveryRetentionTests.cs)
uses fresh synthetic vaults and keys for current/retained rewrap dependencies,
expired and revoked envelopes, empty inputs, deterministic deduplication,
immutable output, scope/integrity refusals and explicit bounds. The pure
assessment is checked without further key-provider calls or vault changes.
The inventory inspection's own authentication and custody boundaries are tested
separately. These fixtures do not use real credentials, cloud services or actual
customer backups, and do not qualify production recovery or key retirement.
