# Opt-in local credential rollback fencing

Revision 22 retains source-v2 state and original absence proofs in [separate signed recovery artifacts](foundation-scoped-recovery.md). Detached rehearsal authenticates original proof MACs and positive AES envelopes; inventory accounts for both source-key dependencies. It neither reconciles source state nor rewrites the original absence. Existing source-v1 formats and operations are preserved.

Revision 21 adds [source absence scope proofs](foundation-absence-scope-provenance.md) for an explicit fresh strong-root mode. A first pending write seals its original canonical scope; abandoning unpublished output retains that proof at generation zero. Reconciliation/retry authenticates the historical proof key and cannot retarget the slot. Positive publication removes the absence proof because the actual authenticated envelope supplies scope. Existing v1 rollback behavior and unscoped legacy absences are preserved.

Strong credential mode adds a separately protected local high-water authority to the [protected vault namespace](foundation-credential-namespaces.md). It detects an older or different encrypted profile when the current authority is retained. This is a trusted-owner C# custody API, with no HTTP, AHP, MCP, model, migration or live-provider operation. Existing default vaults retain their current format and behavior.

## Fresh opt-in and independent custody

Provision two fresh, disjoint local Windows directories whose immediate parents already exist:

```csharp
var options = new CredentialVaultOptions(newVaultRoot, customerId, cellId, existingEncryptionKeyId)
{
    RollbackProtection = new CredentialRollbackProtectionOptions(newAuthorityRoot)
};
var vault = CredentialVault.ProvisionStrong(options, trustedKeyProvider, clock);
// Reopen only; both existing namespaces and bindings must validate.
var reopened = new CredentialVault(options, trustedKeyProvider, clock);
```

`ProvisionStrong` never adopts existing roots or migrates a default vault. Both paths, parents, protected ancestors and disjointness are checked before creation. A later provisioning failure retains partial private state for owner investigation; there is no automatic deletion or repair. Ordinary `Provision` refuses the strong option. A strong root uses namespace kind `credential-vault-fenced`, so reopening it without the required authority fails rather than silently downgrading. The default `credential-vault` marker schema/checksum and four-argument options constructor remain compatible.

The vault and authority receive the existing atomic owner-only directory/file protection, exact path spelling, strict metadata and link checks. Both use `.credential-namespace.json`; the authority has `.rollback.lock`, immutable `authority.json` and mutable `state.json`, while the vault has its existing `.vault.lock` plus `rollback-binding.json`. Bindings pin customer/cell, both exact paths and both random namespace IDs. They exclude the active encryption key so reviewed [rewrap](foundation-credential-rewrap.md) remains possible.

Keep the authority under separate operational custody and **do not restore it alongside the vault**. Separate paths do not establish separate disks, backups, service accounts or administrative control. Copying or rolling back both stores can defeat this local check. A malicious current OS owner/administrator or hostile ancestor owner is outside the protection boundary. Checksums bind metadata; they are not signatures or an independent immutable audit ledger. Multi-host operation, production HSM/KMS custody, rollback-resistant hardware epochs and recovery acceptance remain unqualified.

## Commitments and ordering

The bounded authority state records at most 256 profile identifiers, stored as SHA256 values. Each committed entry binds the exact encrypted envelope's SHA256, length and profile generation. Its checksum also binds customer/cell and both namespace IDs. Same-generation ciphertext substitution is rejected as well as an older generation. Missing, untracked, foreign or mismatched profile data is never treated as a new eligible credential. Missing, widened, malformed, foreign or unknown authority state fails closed; there is no fallback to default mode or cached keys.

The state file is capped at 1 MiB. Metadata counters are bounded, and admission reserves room for both write-ahead and finalization revisions. There is no capacity cleanup, key retirement or profile deletion operation. The authority revision is a compare-and-swap input for cooperating local callers, rather than an externally rollback-proof counter.

All library operations acquire the vault guard before the one bound authority guard. Each authority mutation occurs through that vault. A normal write:

1. Validates the current committed envelope and prepares/flushed-writes a new protected temporary envelope.
2. Publishes a flushed authority **Pending** state binding the prior exact digest/generation (or prior absence), candidate digest/length/generation and random transaction ID.
3. Publishes the candidate envelope through the existing local replacement path.
4. Re-reads its exact bytes and publishes the finalized **Committed** authority entry.

No normal read, credential resolution or subsequent mutation is admitted while a pending transaction exists. Errors or caller cancellation after Pending do not silently cancel that decision. After final commitment, restoring an older vault envelope leaves it unusable against the retained authority. Expiry, enabled state, scope, connection bindings, key lookup and final dispatch authorization still apply independently.

The existing transport lease retains the vault guard through final authorization and send invocation. Because all cooperating authority mutation first requires that same guard, a revocation/rewrap cannot commit between checked resolution and send admission. The authority is not held through a remote response. This does not cancel an already admitted provider effect or establish that a remote request succeeded.

## Explicit pending reconciliation

`InspectRollbackStateAsync(profileId, cancellationToken)` returns bounded public metadata: authority revision/canonical state checksum, hashed profile ID, committed digest/length/generation and any pending transaction. It exports neither plaintext credential material nor encryption keys. The canonical state checksum is the exact review challenge returned by this API; it is not the raw JSON file's byte hash.

```csharp
var observed = await vault.InspectRollbackStateAsync(profileId, cancellationToken);
var result = await vault.ReconcilePendingWriteAsync(
    profileId, observed.AuthorityRevision, observed.AuthorityStateSha256,
    observed.Pending!.PendingSha256,
    CredentialRollbackResolution.AcceptPublished, cancellationToken);
```

The owner must review a concrete pending state before selecting a disposition. `AcceptPublished` requires the current authenticated envelope to equal exactly the pending new digest, length and generation. `AbandonUnpublished` requires the exact prior committed envelope, or true absence when the profile did not previously exist. A directory, linked entry, unknown vault data or different envelope is not absence. Both guards, the exact authority revision/checksum and pending transaction checksum are checked before advancing state. The operation never copies, deletes, adopts, renews or re-enables a credential.

The disposition name does not prove historical publication: observing the old bytes now cannot show whether new bytes existed earlier. Abandonment is an explicit owner decision to retain prior local authority after an incomplete operation; it is not an automatic revocation rollback or remote-effect finding. Stale, duplicate, mixed old/new or forced decisions are refused. No durable application audit event, human product-role grant or external approval record is created by this low-level library API; protected operational authorization/auditing remains separate release work.

## Interruption and storage limits

The implementation uses `Flush(true)` and local replacement for its tested ordering. Native owned-process fixtures stop a writer after Pending publication and after envelope publication, reopen the same files and require explicit reconciliation. These establish **process-interruption** behavior on this Windows/filesystem environment. They do not qualify directory metadata barriers, sudden power loss, disk/controller cache behavior, storage rollback, RPO/RTO or an immutable high-water service.

Recognized private `.write-<id>` and `.state-write-<id>` partial files may remain after process interruption. They are retained and bounded by entry-count/read limits; they are not adopted as committed profiles. Ordinary error handling may remove only its own unpublished temporary file. Reconciliation provides no force-accept, deletion, bulk cleanup or authority-reset feature. Physical disk quotas and secure erasure are not established by these bounds.

The [synthetic rollback tests](../tests/SelfService.Foundation.Tests/CredentialRollbackAuthorityTests.cs) use temporary namespaces, generated keys and fake provider payloads. They exercise exact ciphertext restoration/substitution, missing or mixed state, pending reconciliation, prior absence and directory refusal, stale decisions, cancellation, profile capacity, concurrent admission and actual owned-process interruption with fresh DPAPI custody. A per-instance internal fault seam is used only by these tests; no production configuration/request can select a fault phase. No real cache, primary application lifecycle, sign-in, cloud grant or deployment is involved.

The settled local qualification passed **214 tests, 0 failed and 0 skipped**, including **20 rollback cases** and **58 namespace cases** plus the existing credential regressions. The retained receipt is `artifacts/foundation-credential-rollback-check/results/credential-rollback-qualified.trx` (SHA256 `94f921dae57bee667b36543e8f34a9f95ae1893aac2ad626f162e9db5f424171`). Builds/results use that fresh isolated artifact root; earlier intermediate receipts remain retained. The native owned-process fixtures exercise this machine's Windows DPAPI, ACL and process-interruption boundaries, not power failure or production recovery acceptance.
