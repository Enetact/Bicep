# Reviewed observation of uncertain credential maintenance

`FoundationCredentialMaintenanceObservationRecorder` records one narrow fact: the authenticated encrypted candidate admitted by an uncertain maintenance attempt is present and committed **at the observation point**. Its separate immutable receipt has status `ObservedPublished`. The original maintenance execution remains `OutcomeUnknown`, consumed and unretryable. The receipt does not establish historical completion, a no-effect outcome, remote revocation, future credential validity or permission to execute anything.

This is explicit trusted C# composition over the existing [maintenance executor](foundation-credential-maintenance.md), protected vault and Foundation store. It adds no HTTP, AHP, MCP, model, default worker, import, force, retry or rollback-reconciliation operation. Only the observation review, receipt and audit are written to SQLite. The recorder does not write a vault/key file, create a key or advance/repair the separate high-water authority.

## What must match

The original must have a valid durable admission with `Execution.State = OutcomeUnknown` and an immutable expected candidate SHA256. Prepared/approved requests without an admission, successful executions and different candidates are refused. The actual current envelope must authenticate with its existing source key and match:

- The original admission's exact `ExpectedAfterSha256`.
- The original canonical customer/cell/project/environment/provider/connection/profile scope and hashed profile ID.
- The same vault namespace and marker commitment, and the same optional rollback authority namespace/binding.
- Exactly the original profile generation plus one, with authenticated source key ID equal to the original destination key ID.
- The original expiry, with enabled state false for Revoke or preserved for Rewrap.

The configured current destination key is frozen as metadata in the new observation but is never looked up. It may differ from the original write key or be unavailable: this operation only authenticates existing bytes. A missing existing source key cannot be bypassed. No secret, key or ciphertext is exported by the target or receipt.

Strong mode requires the current authority to recognize the exact envelope as committed, with no Pending transaction. It never accepts or reconciles Pending state. A later global authority revision from another committed profile is valid; it need not equal the original revision plus two. The new review freezes the current revision/state checksum, and final recording requires that exact reviewed observation. Read-only observations can describe valid terminal counters because they increment none of them. Existing [rollback custody limits](foundation-credential-rollback.md) still apply.

## Fresh human authority over historical evidence

The four existing maintenance capabilities are reused. A current scoped human Operator prepares a review, a distinct current human Approver approves its exact digest, and a current human Operator records it. The recorder cannot be its own approver. A signed role alone, CustomerAdmin alone, a workload or a support delegate supplies no authority. Current membership versions, scope, policy, lifecycle, activation and database instance are checked again at the final transaction.

The original request can be expired or quarantined by a restored database; its old humans can have lost their roles. Those old permissions never authorize a new observation. The new request binds the current database instance, policy and human grants while preserving the original immutable request/admission references. Active, Suspended and Offboarding cells support this evidence path; Offboarded does not. Current profile expiry does not turn an observation into token renewal or permission to use an expired credential.

## Trusted composition

```csharp
var recorder = new FoundationCredentialMaintenanceObservationRecorder(
    store, vault, reviewedNamespaceIdentity, reviewedCurrentDatabaseInstanceId);

// Contexts come from signed-principal validation and current scoped memberships.
var review = await recorder.PrepareAsync(operatorPrepareContext,
    originalUnknownIntentId, reviewedPurpose,
    DateTimeOffset.UtcNow.AddMinutes(10), cancellationToken);
var approved = store.ApproveCredentialMaintenanceObservation(
    approverContext, review.Id, review.Digest);
var observed = await recorder.RecordAsync(operatorExecuteContext,
    approved.Id, approved.Digest, cancellationToken);

// Historical uncertainty is unchanged; the new receipt is separate evidence.
var status = observed.Receipt!.Status; // ObservedPublished
```

Do not derive a trusted instance pin from an unreviewed restored file. Restore/activation custody remains governed by the existing [cell activation contract](foundation-cell-activation.md). The public store preparation API also accepts an observation target for trusted composition, but that target is only a review constraint. Only the concrete recorder's final authenticated observation can satisfy recording. There is no public callback or public held session that can manufacture that observation.

## Locks and recording boundary

Both preparation and recording perform a current scoped store check **before any key lookup**. The internal read-only session then acquires the existing vault guard followed by the optional authority guard. It authenticates the source, disposes the decrypted secret, and retains both guards through the last metadata comparison and the short final store transaction. No SQLite transaction is held while an asynchronous key lookup waits. No encryption, temporary candidate, key provisioning or credential dispatch occurs.

The final transaction rechecks the new review's current human authority, policy, instance, activation, original static Unknown binding and exact authenticated target. Receipt and audit commit together. Cooperating credential mutation cannot interleave before that recording point because it needs the retained vault guard. After release, later approved maintenance can change the credential. The receipt's timestamp/digests remain historical evidence and do not assert that the candidate is still current when the receipt is read later.

Observation requests expire within fifteen minutes. Per-cell history and pending bounds are 2,048 requests and 64 unfinished unexpired reviews. The recorder's default cooperative deadline is twenty seconds; trusted composition can choose fifty milliseconds through thirty seconds. Cancellation before the transaction records no receipt. Filesystem/SQLite operations and an uncooperative custom key provider are not a hard whole-run scheduling guarantee.

## Ambiguity and repeated calls

A database commit attempt or lost acknowledgement can return opaque `MaintenanceObservationRecordUncertain`; it does not mean the receipt is absent. No denial/no-effect claim is appended for that uncertainty. Use a fresh authorized read context to call `GetCredentialMaintenanceObservation` for the known request ID and inspect its immutable receipt. Do not automatically repeat the recording call.

An already recorded review is refused with `MaintenanceObservationAlreadyRecorded` before another key lookup. An explicit authorized read returns the existing receipt and its original observation timestamp. This does not create a second observation or reauthorize maintenance. The review state can remain Approved while its separate receipt exists; consumers must inspect the receipt. If no receipt exists after a definite refusal, a human can review the current evidence and choose a subsequent explicit action. There is no force-success, no-effect or historical execution-reconciliation endpoint.

Source/namespace/key failures, stale observations and changed current authority leave vault and high-water bytes unchanged. Scoped denial auditing is attempted where permitted; a fenced/unavailable database can also make that audit unavailable. The original version-six intent, approval and execution rows are never updated by recording. Restored unfinished observation reviews are quarantined; preserved receipts and audit rows remain history. Fresh post-restore review uses the rotated current instance instead of replaying old approval.

## Qualification and limits

Native local fixtures use real SQLite, protected temporary Windows namespaces, synthetic signed identities, fake credentials and existing encryption. They exercise exact candidate refusal, source-only key lookup, Pending refusal, later authority revisions, expired originals with new human grants, asynchronous authority changes, guard ordering with a competing mutation, cancellation/deadline, repeated-call refusal and an ambiguous committed receipt. Tests compare original maintenance rows and vault/high-water bytes. Separate evidence fixtures verify scoped exports and restored/quarantined history.

No live sign-in, customer cache migration, cloud operation, provider pipeline, primary application restart or real credential is involved. This is a same-owner local evidence mechanism. It does not establish independent immutable authority, malicious-owner/admin or hostile-ancestor protection, full audit infrastructure, secure erasure, power-loss/storage durability, production recovery, license clearance or publication permission. SQL and local custody remain separate resources; current authority is established at the recording cutoff, not as a distributed revocation guarantee.
