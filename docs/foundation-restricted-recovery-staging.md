# Stage restricted credentials without activating them

This protocol consumes an exact reviewed [restricted source-V3 artifact](foundation-restricted-recovery.md) and creates a fresh, private, inactive destination stage. It preserves `FoundationBoundDispatchOnly` through destination encryption, original absence-proof commitments and completed-stage verification. It does not adopt a live vault, activate credentials, release a restored-cell hold or grant execution authority.

## Plan an exact inactive destination

`CredentialRestrictedRecoveryStaging` has separate typed plan, receipt, verification and destination-key identity contracts. General V1/V2 stage, checkpoint and review APIs remain distinct and cannot consume these restricted types. Existing General wire formats and cryptography remain unchanged.

```csharp
var destinationKey = await CredentialRestrictedRecoveryStaging.ObserveDestinationKeyAsync(
    customerId, cellId, destinationKeyId, destinationKeyProvider, cancellationToken);
var plan = CredentialRestrictedRecoveryStaging.PreparePlan(
    verifiedRestrictedSet, reviewedSourceAuthority, reviewedSourceNamespace,
    freshStageDirectory, destinationKey, purpose, expiresUtc, clock);
```

The caller supplies an independently reviewed exact source artifact/namespace/signer selection and an existing destination key. The new destination-key identity uses a restricted-purpose commitment under the actual key; changing a public key ID cannot substitute for its material. The plan binds source authority and signature, complete positive/absence scope, preserved profile commitments, destination path/owner commitment, fixed access profile, purpose and expiry. Planning creates no stage directory and grants no database or human approval. Review the exact plan digest in trusted owner tooling before execution.

## Authenticate before reservation

```csharp
var executor = new CredentialRestrictedRecoveryStageExecutor(
    sourceKeys, destinationKeys, clock);
var receipt = await executor.StageAsync(
    retainedRestrictedSet, plan, reviewedPlanSha256,
    pinnedSourcePublicKey, cancellationToken);
```

Execution validates exact source selection and authenticates every retained source envelope and original absence proof before reserving the destination. One frozen source key per actual ID and one frozen destination key prevent inconsistent key responses within the operation. Scope, generation, revocation and full expiry semantics remain preserved. Missing/wrong keys, malformed or pending source state, expired authority and changed selection cannot reserve a successful stage.

The new positive destination envelope authenticates restricted policy and stable plan/source/binding/stage/namespace/root/key context in its AEAD associated data. Destination absence proofs use a separate purpose-derived MAC and retain the original source scope/proof/key commitments. Every completed stage—including positive-only, absence-only and empty stages—requires a destination-keyed completion MAC over its fixed policy, binding, completion core and receipt metadata. Public digests alone cannot substitute for these authentications.

Only a fresh private directory beneath a trusted local parent is reserved. The existing generic recovery-stage guard and marker names protect partial and completed roots from normal credential/key/database/installation operations. New document kinds remain strict. No normal live vault/rollback namespace markers are created. Failures after reservation report an uncertain outcome and preserve the partial root; do not retry into it or repair its permissions. Select a fresh path for a separately reviewed attempt.

The selected positive-plus-absence union is limited to 256 profiles. This new stage permits at most 265 tree entries, 40 MiB total and 4 MiB per metadata file; older General limits remain unchanged. Plans expire within one hour and within the source artifact's validity. Operations have a bounded deadline, while still awaiting an uncooperative dependency before releasing custody.

## Verify with destination custody only

```csharp
var verifier = new CredentialRestrictedRecoveryStageVerifier(destinationKeys, clock);
var verifiedStage = await verifier.VerifyAsync(
    plan, reviewedPlanSha256, expectedReceiptSha256, cancellationToken);
```

The verifier opens only the selected completed destination under its actual guard. It checks exact owner/private custody, bounded tree membership, strict metadata, plan/receipt/profile commitments and written bytes, then authenticates the completion MAC, every output ciphertext and every destination absence proof with the frozen destination key. Current bytes and custody are rechecked after asynchronous key access and before success. Source folders, source keys and the source artifact are unnecessary for this read-only completed-stage check.

Cancellation does not detach an uncooperative dependency or release its held custody before it returns. No partial verified result or plaintext/key/envelope export is returned. The result remains an inactive recovery hold: `NormalVaultUsable`, `PromotionAuthorized`, `ActivationPerformed`, `SourceHostFenced`, `ExternalExecutionAuthorized`, `KeyRetirementAuthorized` and production qualification remain false.

Private stage files contain encrypted material and readable scope/custody metadata; protect the whole directory. This cooperating-owner protocol does not fence raw-key holders, administrators, issued provider tokens or other hosts. A destination proof is not independent key-backup custody or evidence that a provider still accepts a credential.

## Next recovery gates

[Restricted checkpoint custody and held staging](foundation-restricted-recovery-checkpoints.md) constrain accepted source selection under the same policy. Separate [restricted complete-scope human review](foundation-restricted-recovery-review.md) authorizes exact-stage read-only verification. [Restricted joint verification](foundation-restricted-recovery-joint-verification.md) retains the configured current checkpoint alongside the approved stage through durable completion. Changing recovery release/adoption remains separate. Coordinated credential/database activation, supported-consumer closure, independently held latest-set/key backups and production service/storage acceptance remain separate requirements. No HTTP, AHP, MCP, model or default worker route enables staging or activation. Qualification uses disposable keys/directories and synthetic payloads, without deploying to Azure. See [current results](validation.md) and [the commercial roadmap](plans/platform-studio-commercial-release.md#next-implementation-order).
