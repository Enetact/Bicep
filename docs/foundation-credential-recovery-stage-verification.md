# Read-only verification of an inactive credential recovery stage

Revision 19 adds [durable reviewed execution](foundation-credential-recovery-review-execution.md) around the same held verifier. It authorizes all scopes before filesystem/key access, consumes one admission before destination-key resolution and rechecks current authority before recording success. Direct trusted-owner verification remains a read-only inspection API; neither path activates credentials.

Implemented local composition, 25 October 2026. `CredentialRecoveryStageVerifier` independently inspects the completed output of [reviewed inactive staging](foundation-credential-recovery-staging.md). It requires the exact reviewed plan and separately selected completion-receipt commitment, checks private filesystem custody and authenticates the actual staged ciphertext with the explicitly bound destination key. It performs no stage write, source-vault lookup, adoption or activation.

This is a trusted-owner C# API. A plan digest is a review challenge supplied by trusted composition; the library does not authenticate a human reviewer, enforce a two-person database approval or add an HTTP, AHP, MCP, model or default worker route. Verification grants no permission to retry staging, restore a live vault, import keys or submit a pipeline.

## Use the exact completed stage

```csharp
var verifier = new CredentialRecoveryStageVerifier(
    configuredExistingDestinationKeys, trustedClock);
var verified = await verifier.VerifyAsync(
    reviewedPlan, separatelyPinnedPlanSha256,
    separatelyPinnedCompletionReceiptSha256, cancellationToken);
```

The plan was prepared through `CredentialRecoveryStaging.PreparePlan` using a verified [signed private source set](foundation-credential-recovery-set.md), exact set selection, expected source namespace, fresh destination root and explicitly observed destination-key identity. Its digest binds the source signature and inventory, physical-root/owner commitment, destination key identity/commitment, purpose, validity window, full profile metadata hashes and exact absent-profile IDs. The completion receipt is separately pinned after staging; the verifier does not derive its expected receipt commitment from the stage it is asked to trust.

No source set, signing private key, current source vault, source-key provider or live rollback directory is required by this verifier. Original source paths can be unavailable after completion. The destination provider must already have the exact configured destination key available. The API does not provision it or discover a replacement.

## What is checked

Before any destination-key lookup, the shared read-only stage reader validates the reviewed plan, current Windows owner/root commitment, trusted immediate parent, private native ACLs, unlinked canonical local path, exact held-stage tree and complete receipt/state commitments. The guard must already exist and be zero length. It is opened read-only and retained; no missing guard, marker, directory or metadata is created or repaired.

Only the completed inactive format is admitted:

```text
inactive-stage/
  .credential-recovery-stage.guard
  .credential-recovery-stage.json
  completion.json
  profiles/<profile-hash>.credential.json
  rollback/binding.json
  rollback/state.json
```

The `profiles` and `rollback` directories are required even when empty. Unknown children, partial files, active namespace markers, missing profiles, malformed/oversized metadata, untrusted permissions and changed commitments refuse the whole verification. Staged IDs must be distinct from one another and from either original source namespace ID; swapping a source vault ID into an authority role is also refused. No live namespace marker or usable active-vault format is adopted.

The source dependencies in the receipt must exactly match the signature-bound immutable source inventory held by the plan. Preserved-profile hashes are derived from the full signed source profile metadata and bound into the reviewed plan before staging; they are not accepted solely because the completion receipt asserts them. This preserves scope, kind, generation, enabled state and exact expiry offset/ticks. The inventory's Unix-millisecond expiry alone would lose offset and sub-millisecond precision. Committed absent-profile entries are also bound from the signed source state into the plan, receipt and staged state.

The destination provider is called once. The returned key must match the plan's domain-separated commitment to customer, cell, key ID and actual key bytes. An independently owned frozen copy is used for all profile authentication, preventing a provider that changes material behind one key ID from mixing keys between profiles. Provider handles and temporary key/material buffers use the existing disposal/clearing paths. No key bytes are returned.

Every retained output envelope is then authenticated with the existing shared AES-GCM/AAD helper. Its actual dependency metadata, key ID, exact full-profile hash, encrypted byte hash and length must match the plan and receipt. Updated file/receipt/state hashes cannot make an invalid AES tag pass. Expired or revoked profiles can still authenticate as historical encrypted material; they retain their original metadata and gain no usable credential lease.

The completed-stage guard and private detached output bytes remain retained across the external key-provider await. A full tree/name/byte/ACL recheck runs immediately after that await and again after all frozen-key authentication, before return. The frozen-key helper performs no further external provider lookup or current-vault access. This avoids rehashing the entire stage after each synchronous profile authentication. These are boundary observations under a cooperative guard, not continuous monitoring or protection against malicious same-owner/admin interference between observations.

## Historical time and failure semantics

Completed output can be inspected after the plan, source-set signature or contained profiles have expired. This is historical read-only inspection. The receipt's reservation/completion times must be ordered, fall within the reviewed plan and signed source-set validity windows, and not be future at the verifier's trusted-clock observation. The final observation is sampled once for both shared time validation and `ObservedUtc`. Later expiry does not authorize a new staging attempt or turn a completed receipt into activation authority.

The verifier has a cooperative 20-second deadline. Caller cancellation returns a sanitized `OperationCanceledException` carrying the caller token; a deadline failure returns an opaque coded `CredentialException`. No partial result is returned. Provider exception text and private paths are not forwarded.

An uncooperative provider is awaited until it actually returns, retaining the stage guard and private buffers even after cancellation or deadline expiry. It then cannot return a successful canceled result. There is no detached task, forced termination or hard scheduling guarantee. The supplied provider owns its own permissions, latency and possible external behavior; those dependencies are not attested by this API.

A refusal does not repair ACLs, recreate metadata, delete evidence, reconcile a partial stage or repeat staging. Files, names and permissions are preserved for explicit owner inspection. A returned failure is not authority to adopt an incomplete directory. The verifier's own code performs no filesystem write; key-provider operations remain governed by that separately supplied component.

## Public result and remaining gates

`CredentialRecoveryStageVerification` exposes immutable receipt metadata, exact plan/receipt/source-set commitments, authenticated profile count, the sampled observation time and a result metadata SHA256. It includes no raw path, Windows SID, credential plaintext, encryption-key bytes, ciphertext body or credential lease. An empty stage authenticates no profile; `TemporaryInMemoryDecryptionPerformed` is false even though the configured destination key is still resolved and checked.

`OutputCiphertextsAuthenticated` and `InactiveRecoveryHold` are true only on a successful return. `NormalVaultUsable`, `CurrentSourceVaultConsulted`, `PlaintextCredentialMaterialExported`, `EncryptionKeyMaterialExported`, `RetainedEnvelopeBytesExported`, `StageWritten`, `NamespaceAdopted`, `ActivationPerformed`, `SourceHostFenced`, `ExternalExecutionAuthorized`, `KeyRetirementAuthorized`, `IndependentKeyBackupVerified` and `ProductionRecoveryQualified` remain false. `IndependentBackupCoverage` remains `NotAssessed`.

The guard coordinates cooperating local callers. Checksums and exact review pins do not defeat an owner/admin who can replace the stage, plan and the configuration that selects them. Higher-ancestor custody and the operating system remain trusted. Verification does not establish independently protected latest custody, WORM storage, source-host fencing, database/credential coordination, portable DPAPI recovery, service-account qualification, key retention enforcement or measured production recovery objectives. Temporary clearing does not promise forensic erasure of all managed strings or process/storage copies.

The original signed artifact contains encrypted credential values together with unencrypted private scope/path/SID metadata; it is not wholly encrypted. These stage checks do not make that artifact public or back up its source keys. See [read-only source rehearsal](foundation-credential-recovery-rehearsal.md), [key-retention assessment](foundation-credential-recovery-retention.md) and [separate SQLite recovery](foundation-backup-checkpoint-recovery.md) for their distinct limits.

## Local verification

[CredentialRecoveryStageVerifierTests](../tests/SelfService.Foundation.Tests/CredentialRecoveryStageVerifierTests.cs) uses fresh private Windows fixtures, synthetic tokens, ephemeral signers and existing synthetic memory/DPAPI destination keys. It tests both providers and weak/strong source custody, removed source paths, historical expiry/revocation, exact offset/tick preservation, exact plan/receipt selection, missing/unknown/active/partial entries, widened and late-changed custody, single frozen key lookup, source-metadata relabeling, committed absence, structurally consistent invalid AES output, fully rehashed cross-role namespace aliases, cooperative/uncooperative cancellation, future receipt rejection, immutable public metadata and source/output byte/name/ACL preservation.

These fixtures do not access real credential caches, customer recovery directories, live pipelines, Azure deployments or the primary portal. They qualify local inactive-stage inspection and grant no production recovery or publication clearance.
