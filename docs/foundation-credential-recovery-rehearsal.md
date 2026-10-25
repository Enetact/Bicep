# Read-only credential recovery rehearsal

Revision 22 adds `CredentialScopedRecoveryRehearsal` for [source-v2 signed artifacts](foundation-scoped-recovery.md#rehearse-detached-retained-bytes). It authenticates every retained positive envelope and original absence proof without consulting current source folders. Its types, signature kind and private format are separate from this v1 rehearsal. Neither protocol grants destination staging, customer access, activation or key retirement.

Revision 18 adds a separate [inactive staging executor](foundation-credential-recovery-staging.md) and [completed-stage verifier](foundation-credential-recovery-stage-verification.md). This rehearsal API remains read-only and does not create that stage. Successful rehearsal alone is not an exact staging-plan review, destination-key pin, restore/adoption permission or independent key-backup qualification.

`CredentialRecoveryRehearsal` authenticates the actual retained credential envelopes in an explicitly selected [signed private recovery set](foundation-credential-recovery-set.md). It uses the same AES-GCM, authenticated metadata, customer/cell binding and source-key lookup helper as ordinary vault reads. It does not construct, reopen, modify or adopt the current vault. A current vault can have changed or become unavailable after capture without changing which historical bytes are rehearsed.

This is explicit trusted C# composition. It adds no HTTP, AHP, MCP, model, worker or default host route; no product role or automatic task; and no key import, restore, renewal, retirement, namespace adoption, activation or database migration. Authentication of retained material grants no permission to dispatch a pipeline or use a token.

## Select independently reviewed inputs

```csharp
using var retainedSet = CredentialRecoverySets.Parse(privateArtifactBytes);
var rehearsal = new CredentialRecoveryRehearsal(
    independentlyPinnedSourceNamespace, configuredSourceKeyProvider, trustedClock);

var result = await rehearsal.RehearseAsync(
    retainedSet, independentlyReviewedExactSetAuthority,
    pinnedSignerPublicKey, cancellationToken);
```

The source namespace, exact set authority and public signing key come from separately governed inputs. The exact set authority binds customer/cell, set ID, generation, complete artifact SHA256, signer key ID and public-key fingerprint. The rehearsal never derives this selection from an incoming artifact by calling `CredentialRecoverySets.Authority`. That convenience belongs only to trusted capture/review composition.

This pin selects one reviewed historical artifact. It is not a durable latest-credential checkpoint, independent backup witness or assertion that the selected generation is the most recent. Rehearsal cannot discover omitted backups, newer credentials or missing historical sets. The separate [SQLite backup checkpoint authority](foundation-backup-checkpoint-custody.md) does not automatically bind a credential set to a database snapshot.

The artifact is **not wholly encrypted**. Credential values remain AES-encrypted; scope, original paths, Windows SID, custody metadata and signatures are private plaintext metadata. Base64 is a transport encoding. Preserve private custody for `privateArtifactBytes`, any explicit `WritePrivateArtifact` output, temporary streams and retained artifacts. Ordinary JSON serialization of the recovery-set object is blocked. The rehearsal result is a separate public metadata projection.

## Authenticate every retained envelope

Before requesting any source encryption key, the shared verifier checks the expected namespace, exact externally supplied artifact pin, configured public signer and signature lifetime, then validates retained names, byte lengths/hashes, inventory commitments and any captured committed rollback metadata. Original paths and SIDs are validated as historical metadata; they are never used to reopen or adopt current paths.

The rehearsal retains a private detached copy of the validated artifact throughout all source-key awaits. Disposing the caller's original set while a lookup is pending cannot replace or clear that copy. Each envelope is authenticated against its own retained profile hash and the configured customer/cell. The shared helper requests its recorded source key ID. It does not request a current write/destination key or consult the current vault, database, rollback authority or live inventory.

Expired and revoked envelopes are still authenticated: those conditions do not remove the key dependency of their encrypted bytes. They remain expired or revoked metadata in the result. The ordinary current-credential authorization check is not used to turn this historical material into a usable credential lease.

For each successful authentication, the actual hashed profile/scope metadata, generation, enabled state, expiry, source key and envelope byte commitment must equal the signed inventory dependency. Any missing key, wrong key bytes, invalid AES tag, wrong binding or inconsistent entry refuses the whole rehearsal. A valid first entry does not produce partial success when a later entry fails. A valid artifact signature alone does not prove that retained ciphertext decrypts.

After every key await completes, the shared signature lifetime policy is checked again. The final trusted-clock observation is sampled once and used both for that check and `CompletedUtc`. A set that expires, becomes not yet valid or appears future-issued at that observation is refused. No earlier signature-verification result overrides that final check.

## Results and cancellation

`CredentialRecoveryRehearsalResult` contains the structural `SignedSetVerification`, exact `SignedSetSha256`, `InventorySha256`, sorted immutable `ProfileDependencies`, `AuthenticatedProfileCount`, `CompletedUtc` and a metadata commitment `Sha256`. It contains no credential plaintext, encryption-key bytes, ciphertext, raw profile ID, audience/scopes, original path or SID. A result timestamp and checksum are local evidence, not an independent signature or human/CI attestation.

`AllRetainedEnvelopesAuthenticated` is true only on a successful return. `TemporaryInMemoryDecryptionPerformed` is true when the profile count is greater than zero. An empty set authenticates no credential envelope and requests no encryption key; it does not prove that any key is available. The nested structural verification's `EncryptedProfileAuthenticationPerformed` remains false for that earlier verification step; the outer rehearsal reports the subsequent actual authentication.

`CurrentVaultConsulted`, `PlaintextCredentialMaterialExported`, `EncryptionKeyMaterialExported`, `RetainedEnvelopeBytesExported`, `PlaintextPersisted`, `RestorePerformed`, `NamespaceAdopted`, `ExternalExecutionAuthorized`, `KeyRetirementAuthorized`, `IndependentKeyBackupVerified` and `ProductionRecoveryQualified` remain false. `IndependentBackupCoverage` remains `NotAssessed`. No credential lease or material-export method is returned.

The default deadline is a cooperative 20-second cancellation token covering validation and key authentication. Caller cancellation returns a sanitized `OperationCanceledException` carrying the caller token. Deadline expiry returns an opaque coded `CredentialException`; provider exception text and private paths are not forwarded. No automatic retry or partial result is provided.

An uncooperative key provider is still awaited until its actual completion. The operation may remain pending beyond cancellation or 20 seconds, while retaining its private validated buffers; it then refuses success. There is no detached task, hard scheduling guarantee or forced provider termination. The supplied provider owns its lookup behavior, permissions, possible external calls and latency; this API does not attest those dependencies or its environment. Current-vault mutations do not wait for a rehearsal because it holds no current-vault/authority guard.

Temporary secret/key byte buffers use the shared disposal and clearing paths. This prevents plaintext file output by this API, but does not promise forensic erasure of every managed string/copy, dump protection or operating-system memory isolation.

## Recovery remains a separate qualification

Successful rehearsal shows that the selected retained envelopes authenticated using the supplied source-key provider at that observation. It does not back up or recover that provider, DPAPI user/machine protection material, service identity, signing private key, credential namespace, rollback authority or a SQLite database. A live source-key handle is not evidence of an independently recoverable key backup.

Moving to another account or machine, original-host fencing, independently protected latest custody, coordinated database/credential recovery, retention, secure storage, restore/adoption and production recovery objectives remain separate gates. Same-owner or administrator replacement of artifacts, reviewed pins and keys is outside this local evidence boundary. See the [credential dependency inventory](foundation-credential-recovery-inventory.md), [advisory key retention](foundation-credential-recovery-retention.md) and [signed database recovery](foundation-backup-checkpoint-recovery.md) for their distinct scopes.

## Local verification

[CredentialRecoveryRehearsalTests](../tests/SelfService.Foundation.Tests/CredentialRecoveryRehearsalTests.cs) uses fresh private Windows vaults, synthetic tokens, ephemeral signing keys and an isolated fresh DPAPI key namespace. It tests both providers and weak/strong custody; changed/removed current vaults; expired, revoked and mixed-key retained material; unavailable/wrong/failing keys; exact selection and scope refusal before key reads; trusted-signer ciphertext corruption without partial success; caller-set disposal during lookup; cooperating live mutation; cooperative deadline/cancellation and uncooperative provider waiting; final signature lifetime; immutable metadata and byte/name/ACL preservation.

These checks use no real credential cache, customer backup, provider sign-in, Azure deployment or primary portal lifecycle. They establish local rehearsal behavior and no production recovery or publication clearance.
