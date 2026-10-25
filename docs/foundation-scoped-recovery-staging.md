# Authenticate scoped recovery at an inactive destination

`CredentialScopedRecoveryStageExecutor` carries [source-v2 retained recovery](foundation-scoped-recovery.md) into a fresh, owner-private inactive stage. Every retained positive envelope and original absence proof must authenticate before reservation. The executor re-encrypts positive material under one separately pinned existing destination key and creates new destination-key absence MACs. `CredentialScopedRecoveryStageVerifier` later authenticates those written AES and MAC bytes without source folders or source keys.

This is explicit trusted C# composition. It does not adopt a normal vault, activate a customer, renew credentials, retire keys, grant access or submit a pipeline. V1 recovery types and wire formats remain separate. No default HTTP, AHP, MCP, model or worker route selects these scoped APIs.

## Pin the exact inputs

```csharp
var selected = CredentialScopedRecoverySets.Verify(
    retainedSet, reviewedExactSetAuthority, pinnedSignerPublicKey,
    independentlyPinnedSourceNamespace, clock);
var destinationKey = await CredentialScopedRecoveryStaging.ObserveDestinationKeyAsync(
    customerId, cellId, existingDestinationKeyId, configuredDestinationKeys,
    cancellationToken);
var plan = CredentialScopedRecoveryStaging.PreparePlan(
    selected, reviewedExactSetAuthority, independentlyPinnedSourceNamespace,
    freshInactiveDirectory, destinationKey, reviewedPurpose, expiresUtc, clock);
```

The observed key identity commits to the actual destination key material. Preparation binds exact set/namespace/signer, source inventory, complete profile hashes, original absence dependencies, fresh destination path/owner commitment, purpose and expiry. `plan.Sha256` is a review challenge; its presence is not proof of human approval. The private destination path and full scopes are excluded from public projections.

The plan's `AffectedScopes` is the sorted distinct union of present profile and absent proof project/environment pairs. It cannot acquire an absence scope from current memberships or connections. Empty sets may be staged for protocol completeness, but cannot obtain a [human review](foundation-scoped-recovery-review.md) with no actual scope.

## Execute and retain failures

```csharp
var executor = new CredentialScopedRecoveryStageExecutor(
    configuredSourceKeys, configuredDestinationKeys, clock);
var receipt = await executor.StageAsync(
    retainedSet, plan, reviewedPlanSha256, pinnedSignerPublicKey,
    cancellationToken);
```

Source authentication uses the exact retained artifact and original keys. The destination key is resolved once and must match its pin. The executor does not create a missing key or substitute the configured current source key. Complete profiles retain their canonical scope, generation, enabled state and original expiry; expired or revoked credentials remain expired or revoked.

Fresh reservation is one-use and protected by the existing generic stage guard/marker filenames with distinct scoped document kinds. No pre-existing root is overwritten or adopted. A failure before reservation leaves no stage. A failure after reservation retains partial inactive output and its sentinels; it does not automatically delete, repair or retry it. Normal vault, key, database, installation, activation and checkpoint operations continue to refuse protected stage custody.

The destination absence proof preserves the full original scope privately and binds its source proof hash/key/namespace, selected source set, plan, new stage/vault/authority IDs, destination root/key commitments and rollback binding. Its purpose-separated HKDF/HMAC domain differs from the source proof domain. The MAC also binds `CompletionCoreSha256`, which covers completion timestamps and the complete positive/absence high-water commitments. The final receipt binds that core, actual proof MAC hashes and final state hash. Keeping the core independent of the final MACs avoids a circular digest dependency.

## Authenticate completed output

```csharp
var verification = await new CredentialScopedRecoveryStageVerifier(
    configuredDestinationKeys, clock).VerifyAsync(
        plan, reviewedPlanSha256, independentlyReviewedReceiptSha256,
        cancellationToken);
```

Completed verification holds the concrete stage read guard, validates exact names/bytes/owner custody and plan/receipt/core/state/profile/proof correspondence, then authenticates every destination AES envelope and absence MAC with the pinned destination key. Signature or public digest checks cannot substitute for those operations. All required entries must pass before any successful report is returned.

The source key provider is unnecessary after staging. Verification still needs the independently selected plan, exact receipt hash and destination key. It preserves the inactive hold and does not turn a stage into a usable vault. Cancellation is cooperative: an uncooperative provider retains its real await and guard until it returns, and cannot yield a successful result after cancellation.

The [scoped checkpoint wrapper](foundation-scoped-recovery-checkpoints.md) constrains selection to one accepted local head through stage completion. The [durable complete-scope review](foundation-scoped-recovery-review.md) adds separate human approval and one-use verification admission. Neither grants promotion/adoption authority. Final adoption must jointly recheck current checkpoint and review authority and coordinate credential/database activation; that protocol remains future work.
