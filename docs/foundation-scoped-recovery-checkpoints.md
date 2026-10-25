# Scoped retained-set checkpoints

`CredentialScopedRecoverySetCheckpointAuthority` records the current locally accepted [source-v2 retained set](foundation-scoped-recovery.md) under separately configured source namespace and signer pins. Acceptance authenticates every retained positive AES envelope and original absence HMAC before appending a checkpoint. It does not consult current source folders or infer scope from the database.

This is a separate typed protocol. It reuses the protected generic `.credential-recovery-checkpoint.guard` and `.credential-recovery-checkpoint.json` filenames, with scoped authority/record kinds. V1 constructors reject scoped roots and scoped constructors reject v1 roots. There is no fallback, in-place conversion, automatic provisioning or default HTTP/AHP/MCP/model route.

## Provision and accept explicitly

```csharp
var authority = CredentialScopedRecoverySetCheckpointAuthority.Provision(
    freshPrivateCheckpointDirectory, independentlyPinnedSourceNamespace,
    configuredSignerKeyId, configuredSignerPublicKeySha256, clock);
var expectedIdentity = authority.DescribeIdentity();

var head = await authority.AcceptNextAsync(
    retainedSet, reviewedArtifactSha256, pinnedSignerPublicKey,
    configuredSourceKeys, expectedSequence, expectedHeadSha256,
    cancellationToken);
```

Provisioning requires a fresh owner-private root. Existing-only construction validates that exact custody and configuration. First acceptance uses sequence zero and a null head; later acceptance must match the exact prior sequence/hash and increase the signed set's source generation. An older still-valid set cannot replace a newer accepted head. The authority freezes the actual selected artifact and signer key before external key awaits, then runs retained AES/HMAC rehearsal under an exclusive guard through final record readback.

The bounded chain permits 1,024 checkpoints, 16 KiB per metadata document and 16 MiB history. Strict JSON, exact filenames, source/signer/authority bindings, previous hashes, ordering and timestamps are validated together. Pending, partial, corrupt, missing-key, invalid-MAC or cancellation failures do not return an accepted checkpoint. An uncertain append remains explicit; it is not automatically retried.

## Keep selection held through destination staging

```csharp
var staging = new CredentialScopedRecoveryCheckpointStageExecutor(
    authority, independentlyPinnedCheckpointIdentity,
    new CredentialScopedRecoveryStageExecutor(sourceKeys, destinationKeys, clock));
var result = await staging.StageLatestAsync(
    retainedSet, plan, reviewedPlanSha256, pinnedSignerPublicKey,
    expectedHeadSequence, expectedHeadSha256, cancellationToken);
```

The concrete wrapper obtains a shared checkpoint read lease and checks the selected set, plan, namespace, signer and inventory against that head before source-key or destination access. The actual lease remains held through source authentication, destination reservation, AES/HMAC output, completion receipt and final checkpoint revalidation. Cooperating checkpoint writers cannot advance the head in that interval. Arbitrary caller callbacks or nominal lease objects cannot stand in for it.

The lease ends when staging returns. A later [human review](foundation-scoped-recovery-review.md) pins that exact completed stage and its full scope union; it does not silently extend checkpoint custody through its database approval or execution. Read-only review of an explicitly selected historical stage does not establish that it remains the latest head. Future promotion/adoption must jointly recheck both authorities under its own activation protocol.

This is cooperating local custody, not an independent/global monotonic witness or administrator-resistant backup. Restoring the whole checkpoint root can restore an older chain. It does not prove independent key recoverability, complete history, key retirement eligibility, source-host fencing, promotion, adoption, activation or production recovery qualification. Those [commercial gates](plans/platform-studio-commercial-release.md) remain open.
