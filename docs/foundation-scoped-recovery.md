# Source-v2 signed retained recovery

This guide applies to General scoped-V2 namespaces. Fresh `FoundationBoundDispatchOnly` custody uses the separate [restricted source-V3 protocol](foundation-restricted-recovery.md); these V2 APIs and existing stage/checkpoint/review consumers do not accept or downgrade restricted artifacts.

The fresh [source-v2 format](foundation-absence-scope-provenance.md) can retain an original attempted scope after an abandoned first credential publication. `CredentialVault.InspectScopedRecoveryDependenciesAsync` authenticates every present envelope and committed absence proof in one guarded observation. `CaptureScopedSignedRecoverySetAsync` captures those same source bytes into a separate signed private artifact. `CredentialScopedRecoveryRehearsal` authenticates that retained material without consulting the current source folders.

These are explicit trusted C# composition APIs. The default v1 roots, signed sets, staging, human review and checkpoint consumers retain their existing types and formats. They do not accept a scoped recovery artifact. Revision 23 adds separate [scoped staging](foundation-scoped-recovery-staging.md), [human review](foundation-scoped-recovery-review.md) and [checkpoint](foundation-scoped-recovery-checkpoints.md) consumers. No HTTP, AHP, MCP, model, worker or owner CLI route automatically selects them. Source retained recovery itself adds no existing-root conversion, key export, import, credential renewal, deployment or activation.

## Capture one complete source observation

Use `CredentialVault.ProvisionStrongScoped` only to provision a separately selected fresh source-v2 root, as described in [source provenance](foundation-absence-scope-provenance.md). Existing roots are opened with their explicit source format; neither recovery API provisions or repairs them. Persist the expected namespace through trusted configuration rather than trusting an incoming artifact's metadata.

```csharp
// Scoped vault and key provider were explicitly configured earlier.
var inventory = await scopedVault.InspectScopedRecoveryDependenciesAsync(
    independentlyPinnedSourceNamespace, cancellationToken);

using var captured = await scopedVault.CaptureScopedSignedRecoverySetAsync(
    independentlyPinnedSourceNamespace, configuredSigningKey,
    configuredSignerKeyId, reviewedSetGeneration,
    notBeforeUtc, expiresUtc, cancellationToken);

// This is an explicit private artifact write. Protect the destination and its backups.
captured.WritePrivateArtifact(privateOutputStream);
```

Both source operations hold the vault guard followed by the rollback-authority guard. They require exact namespace identity and commitment, protected markers, source-v2 binding/state, no pending transaction anywhere and a complete bounded inventory. Present profile files must correspond exactly to positive high-water entries; every generation-zero entry needs its original proof and a truly absent credential path. Unknown files, links, partial writes or extra directories fail the operation. The combined positive-plus-absence limit is 256.

Source-key resolution freezes one key per actual ID across both envelope AES authentication and absence-proof HMAC authentication. A provider cannot supply different materials for the same ID within one operation. The configured current write key is irrelevant unless retained bytes actually reference it. Missing historical proof keys fail the whole result; there is no alias substitution, fallback or repair. The full guarded source names, bytes, custody, namespace and state are checked again after provider awaits and before publication. Cancellation or failure returns no partial inventory or signed artifact.

## Distinguish metadata, signature checks and authentication

`CredentialScopedRecoveryInventory` is immutable public metadata. It binds the namespace, authority state/revision, observation time, present profile dependencies, redacted absence dependencies and `CurrentRequiredKeyIds`. The key list is the exact sorted union of envelope source keys and original absence-proof keys. A parser can reconstruct this metadata from retained bytes, so the inventory itself does not attest that any AES or HMAC operation has run.

Public absence entries contain profile/scope hashes, project/environment IDs, original source key IDs and proof hashes. Private proof payloads, full canonical scopes, required scopes, `AuthProfileId`, MACs, tokens, key bytes and filesystem paths are excluded. The private artifact carries the retained encrypted envelopes and original source markers/binding/state/proofs. Its scope metadata and ciphertext require private handling even though signing is not encryption.

`CredentialScopedRecoverySets.Parse` uses a separate strict scoped artifact schema. It rejects mixed/unsupported versions, unknown or duplicate JSON properties, noncanonical encodings, oversized containers and inconsistent counts or hashes. Structural retained validation checks namespace markers, source binding/state, mandatory absence proofs and exact profile/file/proof correspondence. Envelope header metadata must match the public dependency inventory. It does not prove that a structurally valid proof has a valid MAC or that an envelope has a valid AES tag.

`CredentialScopedRecoverySets.Verify` verifies the exact artifact authority, configured public signing key and lifetime, together with those retained structural commitments. Its `EncryptedProfileAuthenticationPerformed` and `AbsenceProofAuthenticationPerformed` flags remain false. A valid signature authenticates the selected signed bytes under the configured signer; it does not authenticate source-key provenance. Re-signing a coherently altered proof cannot substitute for the original HMAC.

## Rehearse detached retained bytes

```csharp
using var selected = CredentialScopedRecoverySets.Parse(privateArtifactBytes);
var rehearsal = new CredentialScopedRecoveryRehearsal(
    independentlyPinnedSourceNamespace, configuredSourceKeys, trustedClock);
var report = await rehearsal.RehearseAsync(
    selected, independentlyReviewedExactSetAuthority,
    pinnedSignerPublicKey, cancellationToken);
```

The namespace, exact typed set authority and signer key come from separately reviewed trusted inputs. An authority constructed directly from an untrusted artifact does not provide independent selection. Selection is checked before source-key lookup. Retained validation and cryptographic authentication use only the selected private artifact, even if both source folders have since disappeared or changed.

Rehearsal authenticates every retained positive envelope with its actual source key, including expired or revoked profiles. It also authenticates every retained absence proof with its original source key and original namespace/full-scope/first-attempt commitments. One frozen per-ID key cache spans both kinds of material. Only the rehearsal report can describe the actual authentication operations it performed. Empty categories do not imply an authentication operation ran. A late failure, expiry, time before the signed capture/issue timestamps or cancellation prevents a successful result.

Authentication preserves the recorded generation, enabled state, expiry, original scope and absence. It does not make expired/revoked credentials usable, prove the selected artifact is globally latest, restore a namespace, stage output or authorize customer access. A cooperating provider can honor the operation deadline. An uncooperative provider keeps its actual await and custody held until it returns; cancellation does not detach work or provide a hard completion-time guarantee. Frozen key handles are disposed and owned key buffers zeroed when the operation ends.

## Retain keys conservatively

`CredentialScopedRecoveryRetention.Assess(current, suppliedHistory)` is a separate pure advisory assessment over scoped inventory metadata. It includes both original proof-key and positive envelope-key references. Current rotation or successful later publication cannot release a key still referenced by supplied historical inventory. Entries from a different customer/cell or namespace/binding are rejected. Repeated identical commitments add no history coverage. Both input history count and distinct key references are bounded.

The assessment performs no AES, HMAC, signing, key-provider or filesystem operation. `RetainedInventoryMetadataOnly` is true; both authentication-performed flags, `CompleteHistoricalCoverageVerified` and `KeyRetirementAuthorized` remain false. `IndependentBackupCoverage` is `NotAssessed`. A `RetentionHold` does not enforce key-provider deletion protection, prove the key is recoverable or prove the caller supplied every retained backup. Existing [v1 retention](foundation-credential-recovery-retention.md) remains a separate contract.

## Remaining recovery gates

The source-v2 retained protocol closes source capture/rehearsal and combined dependency accounting. Revision 23 adds separate destination AES/HMAC authentication, complete present-plus-absence human review and scoped retained-set checkpoints. Existing v1 customer-review refusal of unsupported/unscoped absences remains in force; v1 checkpoint selection is not widened. Scoped checkpoint custody ends when staging returns, while later human review pins that completed stage. Final adoption still needs joint current-authority checks and coordinated activation.

Separately reviewed promotion/adoption, coordinated database/credential activation, independently held latest-set/key backup custody, complete historical coverage, supported service identity/storage, clean-machine delivery, signing, independent security review and commercial rights acceptance remain open. Same-owner or administrator replacement of local roots is outside this protocol's guarantee. The [commercial roadmap](plans/platform-studio-commercial-release.md) retains those priorities and evidence limits.
