# Reviewed local credential maintenance

The Foundation library now composes its durable store with the existing protected credential vault for two explicit operations: `Revoke` and `Rewrap`. A scoped human Operator prepares an exact metadata commitment, a different human Approver approves its digest, and a current human Operator executes that request through the concrete `FoundationCredentialMaintenanceExecutor`. No customer HTTP, AHP, MCP, model tool, default worker or live transport selects this executor. Production onboarding and service custody remain separate work.

`Revoke` disables the local stored profile. It does not contact a provider, revoke a remote token or cancel an already admitted request. `Rewrap` encrypts the same material with an already provisioned destination key. Both increment the profile generation once and preserve its scope and expiry; rewrap preserves its enabled state. Neither operation imports, renews or exports a credential, provisions a key, retires a key, changes a profile's customer connection, or migrates an existing cache.

## Scoped review and immutable observations

The four capabilities are `credential.maintenance.prepare`, `.approve`, `.execute` and `.read`. Prepare/execute require Operator; approve requires Approver; scoped read permits Operator, Approver or Auditor. A signed role alone is insufficient: the existing validated `CustomerContext`, matching current membership/version and exact customer/cell/project/environment are required. Workloads, support delegates and CustomerAdmin alone cannot acquire maintenance authority. Requester and approver must differ, and the approver cannot execute their own approval.

`CredentialVault.DescribeNamespace()` returns validation-only namespace IDs and commitments. It exposes no root path, token or key. `InspectMaintenanceAsync(profileId)` authenticates the exact encrypted envelope under the existing vault guard and returns the immutable `FoundationCredentialMaintenanceTarget`. Its strings/primitives bind:

- Canonical authenticated scope JSON and digest, hashed profile ID, customer/cell/application scope and source connection binding.
- Vault namespace ID and marker commitment, including the existing path/owner binding without exposing that path.
- Exact profile generation, enabled state, expiry, source envelope SHA256/length, source key ID and configured destination key ID.
- When enabled, the separate rollback-authority namespace/binding, revision and canonical state checksum.

The target has no exposed mutable scope array. Observation is not an authorization grant. Fabricated, edited or stale observations can be recorded for review, but cannot substitute for the actual gate-held observation at execution. Inspection performs no provisioning or ACL repair, returns no decrypted material and has a twenty-second cooperative deadline. Namespace description uses bounded existing-file validation without key lookup.

The store independently captures the current registry connection generation/configuration checksum, policy version, database instance and exact requester/approver versions. Rewrap requires an enabled current connection and a matching normalized source connection digest. Revocation may address an older source generation or disabled connection while preserving the same current profile reference and scope. Both actions pin **source and destination key IDs**: the existing revocation write also encrypts with the configured destination key, so any implicit key move is visible in the reviewed target.

## Trusted composition

```csharp
// All contexts come from the existing signed-principal/scoped-membership resolver.
// The store, namespace and database instance are supplied by reviewed host composition.
var namespacePin = vault.DescribeNamespace();
var target = await vault.InspectMaintenanceAsync(reviewedProfileId, cancellationToken);
var request = store.PrepareCredentialMaintenance(operatorPrepareContext,
    CredentialMaintenanceOperation.Revoke, target, reviewedPurpose,
    DateTimeOffset.UtcNow.AddMinutes(10));
var approved = store.ApproveCredentialMaintenance(approverContext, request.Id, request.Digest);
var executor = new FoundationCredentialMaintenanceExecutor(store, vault,
    namespacePin, reviewedDatabaseInstanceId);
var recorded = await executor.ExecuteAsync(operatorExecuteContext,
    approved.Id, approved.Digest, cancellationToken);
```

For rewrap, construct the vault with the independently reviewed existing destination `EncryptionKeyId` before inspecting its target; use `CredentialMaintenanceOperation.Rewrap`. A metadata/key ID or namespace checksum never supplies key material or product authority. Do not derive a new trusted database instance from an unreviewed restored file. Existing [runtime activation](foundation-cell-activation.md) and [namespace custody](foundation-credential-namespaces.md) still apply.

Revocation is available while Active, Suspended or Offboarding, with current standing human grants; it never reactivates a cell and is refused after Offboarded. Rewrap is Active-only. This safe administration is separate from disconnected pipeline dispatch entitlement. An intent expires within fifteen minutes. History and pending-intent bounds are enforced by the store; this increment adds no force, cleanup or maintenance-reconciliation command.

## Admission, local effect and audit

The executor pins its exact existing vault namespace and database instance. Its internal one-use prepared session holds the vault guard while it authenticates the source, resolves the destination key and encrypts the candidate entirely in memory. Preparation creates no temporary vault file, high-water update or admission. Plaintext/key buffers are disposed or zeroed; no new public callback/session/permit constructor bypasses the concrete store check.

After asynchronous key lookup, the executor rechecks the exact observation. The store then rechecks current context/role versions, distinct approval, lifecycle, policy, connection, instance, activation and target in one SQLite transaction. It commits a one-use execution record with `Execution.State = OutcomeUnknown`, the exact expected candidate SHA256 and admission audit **before** any local mutation. No database transaction is held across key lookup or filesystem writes.

The retained vault guard orders this admission with cooperating dispatch, replacement, revocation and rewrap. Publication uses the existing private temporary-file and local replacement protocol. Strong mode retains vault-then-authority guard order and the existing Pending/Committed protocol. The candidate bytes admitted in SQLite are exactly those published; no key lookup or second encryption occurs after admission.

Only a successfully verified publication and required strong-state finalization can produce a typed completion result. The result contains operation/target commitments, before/after generations and envelope digests, namespace commitment and optional authority revision/checksum. It contains no token, ciphertext, raw scope or path. Completion atomically records its result/audit using the consumed immutable admission. It does not demand that a role remain granted after admission: subsequent revocation must not prevent recording a known local result.

## Failure and recovery boundaries

Preadmission failure is a definite refusal: the existing envelope/high-water bytes remain unchanged, and a bounded scoped denial audit is attempted. Caller cancellation/deadline during key preparation releases the guard without consumption. The default executor deadline is twenty seconds; trusted composition can select fifty milliseconds through thirty seconds. Native filesystem/SQLite calls are not a whole-operation scheduling or hard CPU guarantee.

After durable admission, cancellation, process termination, publication/finalization failure or completion-audit failure remains `Execution.State = OutcomeUnknown`. A request with any execution record is consumed; it is never requeued, reclaimed, automatically retried or treated as a fresh approval. The intent's separate review state can remain Approved while its execution is unknown. An old or new observed generation alone cannot prove that a prior attempt never ran. Review the exact evidence before considering any new explicitly approved maintenance request.

If completion storage is unavailable, the API can return an opaque unknown-outcome exception; the already committed unknown admission remains authoritative. Strong Pending state separately blocks normal credential use until the existing explicit [rollback reconciliation](foundation-credential-rollback.md) is reviewed. That low-level reconciliation does not retroactively complete the product maintenance record. Restored copies quarantine the records and rotate the database instance; historical approvals/results remain evidence, not fresh mutation authority.

A separate [current-publication observation](foundation-maintenance-observation.md) can record that the authenticated current envelope exactly equals the originally admitted candidate. It requires a fresh review and independent approval bound to current authority. Its schema-v7 receipt is read-only evidence: the original execution remains `OutcomeUnknown` and consumed. [Observation restore/export rules](foundation-maintenance-observation-evidence.md) preserve those separate meanings.

SQLite and the vault are separate resources. The committed admission is the authority cutoff: a membership or policy change after it can race the already admitted effect. There is no claim of distributed atomic revocation, independent immutable custody, remote revocation, power-loss durability, secure erasure, or protection against the current OS owner/admin or hostile ancestor owner. Existing low-level vault APIs remain trusted custody primitives; this adapter does not force every same-owner C# call through product audit. A host must explicitly choose this approved path and protect its composition and operational evidence.

## Local qualification

Synthetic tests use real SQLite, local signed principals, protected Windows namespaces, fake token payloads and mock HTTP handlers. They test exact observation/key/namespace pins, authority changes while destination key lookup is actually awaiting, cancellation/deadline, consumed replay, strong Pending failures, completion after role revocation and completion-audit failure. Native owned-process fixtures use fresh temporary DPAPI keys and terminate only the original owned child handle after durable admission and after local mutation. No real credential cache, sign-in, provider pipeline, cloud deployment or primary application lifecycle is used. Local focused receipts qualify these tested boundaries, not production admission.
