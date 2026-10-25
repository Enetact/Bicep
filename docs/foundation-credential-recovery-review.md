# Durable review of an inactive credential recovery stage

Revision 22 adds [source-v2 signed retained capture and rehearsal](foundation-scoped-recovery.md), with no expansion of this v1 human review boundary. Scoped destination proof authentication and authorization over every present-plus-absence scope remain required. This review continues to reject unsupported/unscoped absences and empty profile sets.

Revision 21 adds [authenticated source absence provenance](foundation-absence-scope-provenance.md) and [source inspection](foundation-absence-scope-inspection.md), with no change to this review boundary. Source-v2 proofs have not yet been carried through signed retained sets and authenticated destination-stage proofs. This review therefore continues to reject unscoped/unsupported absences and empty profile sets; current database scope or membership records cannot backfill provenance.

Revision 20 adds separate [local latest-accepted-set custody](foundation-credential-recovery-checkpoints.md) and [checkpoint-backed staging](foundation-credential-checkpoint-staging.md). This schema-v8 review remains one read-only verification with complete human scope coverage. Checkpoint acceptance grants no human review, promotion or activation authority, and this review still refuses empty profile sets and unscoped absence entries.

This explicit C# composition authorizes **one read-only verification** of an
exact completed [inactive recovery stage](foundation-credential-recovery-staging.md).
It does not promote, import or adopt credentials, change a live namespace,
activate a host, renew tokens or permit provider dispatch. The successful result
is `HeldStageVerified`; `InactiveRecoveryHold` stays true and promotion, live
adoption, activation and external execution authority stay false.

The trusted owner first selects the exact signed set, source namespace,
destination path/key identity, staging plan and completion receipt. Those
existing commitments remain necessary. This review adds durable human scope
authorization and an audit trail; it supplies no independent latest-set,
key-backup, source-fencing or production recovery guarantee.

## Authorize every affected scope

The four capabilities use existing directory roles:

| Capability | Standing role and principal |
|---|---|
| `credential.recovery.prepare` | Human `Operator` |
| `credential.recovery.approve` | Human `Approver`, distinct from requester |
| `credential.recovery.verify` | Human `Operator`, distinct from approver |
| `credential.recovery.read` | Human `Operator`, `Approver` or `Auditor` |

There is no new directory app role, workload grant, support-only exception or
implicit `CustomerAdmin` override. A signed identity's roles intersect its
current persisted memberships through `CustomerContextResolver`. The store
rechecks current memberships and actor versions for every required scope.

Each call supplies an exact, nonempty list of contexts covering **all distinct
project/environment pairs** in the source inventory. All contexts belong to
the same human, customer, cell and required capability. Duplicate, missing,
extra, mixed-actor, expired and cross-bound contexts are refused. One project's
Operator or Approver cannot authorize a whole-cell set containing other scopes.
The full review read requires the same complete scope coverage; scoped audit
exports do not grant access to the entire review object.

Empty profile sets and any source absence map are refused in this increment.
The existing absence commitments contain profile hashes without authenticated
project/environment provenance. They cannot be assigned to today's connection
or membership records to manufacture a scope grant.

## Prepare and independently approve the exact target

```csharp
var request = store.PrepareCredentialRecoveryStageReview(
    requesterContextsForEveryScope, reviewedStagePlan, completedStageReceipt,
    reviewedPurpose, reviewExpiresUtc);

var approved = store.ApproveCredentialRecoveryStageReview(
    approverContextsForEveryScope, request.Id, separatelyReviewedRequestDigest);

var history = store.GetCredentialRecoveryStageReview(
    readerContextsForEveryScope, request.Id);
```

Direct store preparation records metadata from the typed plan and receipt; it
does not inspect the filesystem or resolve keys. Use the coordinator's
`PrepareAsync` composition for authorized, held current-stage metadata checks
before recording that request. Neither preparation path authenticates destination
ciphertext; that occurs only after verification admission.

The immutable target binds the plan, completion receipt, signed source set,
source inventory/namespace, staged IDs, destination-root and destination-key
commitments, rollback binding/state hashes and exact scope set. No caller-provided
replacement scope list or raw private artifact is persisted as the target.
The request also pins the current database instance, policy version, requester
and grant version, purpose digest and bounded expiry. Approval pins a distinct
human, grant version, exact request digest and the shortest approval-context
lifetime. Expiry never extends an identity's authority.

The [execution coordinator](foundation-credential-recovery-review-execution.md)
performs the held-stage verification. It validates current authority, retains
the completed-stage guard, commits one-use admission with an `OutcomeUnknown`
record and scoped audit **before** resolving the destination key, authenticates
the staged bytes, and records the typed result under current authority. No SQLite
transaction is held across the provider await. The original stage and all
source/key directories remain unchanged.

Requester and approver standing grants, their stored lifetimes, current
executor contexts, cell lifecycle, policy and instance pins are checked at the
required cutoffs. A changed scope grant or policy does not silently narrow the
review to a still-authorized subset. The executor may be the requester; it may
not be the approver. This is separation between actors, not independent
organization or host custody.

Admission freezes and validates its publication timestamp against preparation,
approval and every applicable expiry before it writes the consumed record.
A clock rewind or a forward jump beyond the executor's shorter context window
cannot publish an invalid admission and then reach the destination key.

## One-use admission and uncertainty

Admission consumes the request. A later cancellation, key/authentication error,
authority change or result/audit failure leaves an uncertain consumed record;
there is no automatic replay, takeover or retry. A separate explicit history
read can establish what durable record exists after an uncertain return. A
complete stage or a previously observed success does not itself authorize
promotion or convert a prior unknown action into a no-effect assertion.

The database transaction makes its request/admission/result and scoped audit
events atomic with each other. It is not a transaction with the filesystem,
identity issuer or key provider. Audit digests are local integrity evidence,
not independent event inclusion or protection from a privileged owner replacing
the database and its pins.

## History, exports and restored cells

Schema v8 adds retained review, approval and execution records while preserving
earlier migration definitions. History reads validate the stored target,
request, approval and result relationships and require the reader's **current**
scope grants. Historical records do not revive expired or revoked execution
authority. Scoped auditor pages expose the existing metadata-only audit events
and commitments, without private plan paths, credential material, encryption
keys, custody payloads or another scope's target details.

Verified backup admission checks the required v8 structures before restore
staging. Restore rotates the database instance and policy, quarantines unfinished
reviews including consumed unknown executions, and preserves completed receipts
and approvals as history. It makes no stage, vault, key or provider call.
Copied approvals and matching staged bytes do not restore authority; a restored
cell needs a new current review for a future verification. Restoring that cell
again rotates its authority again. Audit-capacity or validation failure rolls
back the recovery hold transaction and preserves the original snapshot.

Limits are **256 exact scopes/contexts**, **2,048 retained review requests**,
**64 live unconsumed requests**, **128 KiB target metadata** and **8 KiB result
metadata**. The request lifetime is at most 15 minutes and no later than its
shortest requester context. These limits are enforced rather than silently
dropping scopes or records. Exhausted audit or review capacity requires explicit
operational handling. There is no new default HTTP, MCP, AHP, model, worker
command or customer-facing approval UI for this flow.

## Local qualification

[Independent boundary tests](../tests/SelfService.Foundation.Tests/FoundationCredentialRecoveryReviewBoundaryTests.cs)
use generated signing/encryption keys, real synthetic signed identities and
fresh local SQLite/stage roots. They cover exact scope/context authorization,
role/version/expiry changes, target pin mismatch, current-history isolation,
metadata-only audit evidence and restore quarantine/preservation. Separate core,
coordinator and migration tests cover the held verification protocol and schema
admission. This evidence does not qualify real customer credentials, production
identities, independent custody, live adoption or deployment.
