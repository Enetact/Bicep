# Verify an approved scoped stage while its checkpoint is held

`FoundationCredentialScopedRecoveryJointReviewCoordinator` joins an existing [complete-scope human approval](foundation-scoped-recovery-review.md) with the actual current [scoped retained-set checkpoint](foundation-scoped-recovery-checkpoints.md). It holds both the checkpoint lease and the completed stage guard through one-use admission, destination AES/MAC authentication, current authorization checks and the durable result commit.

The human approval covers the exact inactive stage. The configured checkpoint is an additional execution-time constraint: this flow does not claim that its identity or head was approved, or that it was current when the human approved the stage. There is no second approval layer. Existing schema-9 review APIs and wire formats retain their behavior; callers explicitly select this stronger execution composition.

The result is local held evidence. It does not adopt a namespace, make a stage usable by a normal vault, activate a customer, recover or export keys, fence a source host, grant provider access or deploy anything. There is no default HTTP, AHP, MCP, model or worker route for this coordinator.

## Compose the trusted boundary

Prepare the exact plan and completed receipt through the existing scoped review coordinator, then obtain a distinct human approver's approval of that request digest. Both people need their current standing roles in every present and absent project/environment pair. An absence-only stage is eligible; an empty scope union is refused. The existing review guide describes context resolution, role intersection, separation and expiry.

```csharp
var joint = new FoundationCredentialScopedRecoveryJointReviewCoordinator(
    store, configuredScopedCheckpointAuthority,
    independentlyPinnedCheckpointIdentity,
    new CredentialScopedRecoveryStageVerifier(configuredDestinationKeys, clock),
    expectedDatabaseInstanceId);

var evidence = await joint.ExecuteApprovedAsync(
    currentExecutorContexts,
    approvedReview.Id, approvedReview.Digest,
    plan, reviewedPlanSha256, independentlyReviewedReceiptSha256,
    expectedCheckpointSequence, expectedCheckpointSha256,
    cancellationToken);

var history = store.GetCredentialScopedRecoveryJointVerification(
    currentReaderContexts, approvedReview.Id);
```

The authority instance, expected full identity, destination key provider and database instance pin come from trusted application composition. An incoming packet cannot choose an authority root or supply a nominal checkpoint object as custody. The caller's expected sequence and head hash are compared with that configured authority's actual current head. Identity includes customer/cell, full source namespace, authority ID, signer ID, signer public-key fingerprint and the authority's custody commitment.

Execution uses the existing `CredentialRecoveryVerify` capability and current human Operator role in every affected scope. Reading uses `CredentialRecoveryRead` and current Operator, Approver or Auditor authority in every scope. All supplied contexts must describe the same current human actor, customer, cell and actor version with exactly the required scopes. Workloads, support-delegated identities, incomplete scope lists and a distinct-approver violation cannot enter this execution path. No new role, grant or capability is introduced.

## Hold custody through both database transitions

1. Snapshot the context list and authorize every scope in the durable store before opening private checkpoint or stage files. Recheck the configured database instance and exact approved review, plan and receipt pins.
2. Acquire the concrete checkpoint read lease, then the concrete stage read guard. This checkpoint-to-stage order is fixed. Match the actual checkpoint identity, head, source namespace, exact typed set authority, inventory hash and signer pins against the plan and completed stage.
3. In one SQLite transaction, recheck activation, lifecycle, policy, instance, current executor authority and the original requester/approver grants, versions and expiry. Insert the ordinary schema-9 execution and the new checkpoint-bound execution as `OutcomeUnknown`, with scoped audit, before resolving any destination key.
4. Retain both guards while the existing verifier resolves the destination key once, matches and freezes its bytes, authenticates every destination AES envelope and absence MAC, and checks the completed stage snapshot. Original source folders and source keys are unnecessary here. No database transaction spans an external key-provider await.
5. Recheck the actual checkpoint and stage snapshots, cancellation and database instance. While both guards remain held, one transaction rechecks all current authority and publication-time bounds, records both successful results and appends audit for every scope.

Cooperating checkpoint writers cannot advance the head during this held interval. The new target persists the full public checkpoint identity and head together with the exact approved stage target. Public records contain commitments, scope IDs and hashes; they exclude private paths, full credential scopes, raw proofs, MACs, ciphertext, credentials and keys.

The existing lifecycle policy permits this read-only verification while Active, Suspended or Offboarding, and refuses Offboarded cells. The executor must remain separate from the approver. Authority lost during a destination-key await or before the final commit prevents successful publication.

## Results, cancellation and uncertainty

Both execution rows share the same admission and outcome. Successful ordinary history remains `HeldStageVerified`; the additional result has status `CurrentCheckpointHeldStageVerified`. Its checkpoint-read-lease flag describes the interval just completed. The inactive hold remains true; promotion, live adoption, activation, external execution and independent-key-backup flags remain false, and independent backup coverage is `NotAssessed`.

After admission, cancellation, failed authentication, changed custody or lost authorization consumes the operation as `OutcomeUnknown`. Neither the ordinary coordinator nor the joint coordinator can automatically retry that review. Unknown recording also updates both rows and their scoped audit together. An already committed success remains immutable and cannot be demoted by an uncertain completion handler.

An ambiguous admission commit reports `ScopedRecoveryJointAdmissionUncertain`. An unavailable unknown-recording attempt reports `credential-scoped-recovery-joint-review-outcome-unknown`; do not interpret either as no admission or no effect. Reread protected history rather than replaying an operation whose outcome is uncertain. The default budget is a cooperative 20 seconds. An uncooperative provider retains the real await and both guards until it returns; cancellation does not release inputs that dependency is still using.

## Schema 10, backups and remaining recovery work

Schema 10 adds `credential_scoped_recovery_joint_executions`, with a unique link to the existing schema-9 execution, immutable authority fields, retained-success guards and a state index. The earlier schema definitions and checksums remain fixed. Fresh schema migration and read-only backup admission validate these objects and history; `initializeSchema:false` still refuses an older database without migrating or repairing it. Local migration tests use disposable databases, not existing customer state.

Restore rotates the database instance and policy, preserves successful history, and quarantines unfinished schema-9 reviews, including consumed joint unknowns. The original execution outcomes remain retained, and an additional recovery audit records the held joint evidence. Old approvals and admissions cannot be replayed against the restored instance. Historical reads require current scope authorization and static integrity; they do not revive execution authority.

The local tests exercise mixed and absence-only scopes, stale heads, both real guards across awaits, one-use contention, authority drift, tamper, audit failure and restore quarantine. After the guards are released, the checkpoint may advance: this receipt does not promise perpetual latestness. A valid-prefix rollback of the entire checkpoint root, malicious operating-system owner/administrator, independent latest/key custody, source fencing and coordinated credential/database activation remain outside this protocol. Actual promotion/adoption and production recovery qualification remain separate work in the [commercial roadmap](plans/platform-studio-commercial-release.md).
