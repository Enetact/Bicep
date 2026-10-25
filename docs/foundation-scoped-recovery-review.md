# Complete-scope review of an inactive scoped credential stage

This explicit C# composition reviews one completed [scoped recovery stage](foundation-scoped-recovery-staging.md). It covers the sorted union of project/environment pairs from present profiles and authenticated absence-scope provenance. An absence-only stage is eligible; a completely empty stage has no customer scope and is refused. Preparation checks structure and current authority without resolving any destination key. A successful result requires actual destination AES authentication and destination absence-proof MAC authentication under the retained stage guard.

This is local held evidence. It does not adopt a namespace, activate a vault, recover or export keys, fence the source host, execute a pipeline, or grant production recovery authority. There is no new HTTP, AHP, MCP, model, worker, or default host route. The existing v1 review still refuses unscoped absences and empty sources. Its types, wire format, eligibility and schema 1–8 migration definitions remain unchanged.

## Explicit composition and human authority

Use `FoundationCredentialScopedRecoveryStageReviewCoordinator(store, verifier, expectedDatabaseInstanceId)` with a concrete `CredentialScopedRecoveryStageVerifier` configured for the already approved destination key provider. The caller supplies the exact reviewed `CredentialScopedRecoveryStagePlan.Sha256` and completed `CredentialScopedRecoveryStageReceipt.Sha256`. Neither today's connections nor caller-selected scope labels can add or replace provenance.

Each operation needs exactly one `CustomerContext` per affected project/environment pair. All contexts must identify the same current human principal, customer, cell and actor version, carry the operation's capability, and remain within their expiry. Extra, missing, duplicate, foreign, workload or support-delegated contexts are refused. Contexts must come from the existing signed identity validator and membership resolver; application code must not construct identities from a request body.

| Operation | Capability | Current standing role |
|---|---|---|
| Coordinator `PrepareAsync` | `CredentialRecoveryPrepare` | Operator in every scope |
| Store `ApproveCredentialScopedRecoveryStageReview` | `CredentialRecoveryApprove` | Approver in every scope |
| Coordinator `ExecuteAsync` | `CredentialRecoveryVerify` | Operator in every scope |
| Store `GetCredentialScopedRecoveryStageReview` | `CredentialRecoveryRead` | Operator, Approver or Auditor in every scope |

The approver must differ from both requester and executor. Approval binds the exact request digest. Request validity is at most 15 minutes and cannot outlive the earliest requester context. The approval captures its earliest context expiry. Fresh signed-role intersection is enforced when resolving contexts, and the durable store rechecks all standing grants, enabled state and actor versions. Durable distinct human-principal authorization is local evidence; it does not attest physical human presence or independent organizational or host custody.

## Admission, verification and interruption

Preparation authorizes the complete scope union before opening private stage metadata. It then holds the concrete stage read lease while persisting a typed target with plan, source set, inventory, namespace, destination key commitment, stage IDs, binding, state, completion-core and receipt hashes; present and absence counts; and the complete scope union. Public records contain redacted hashes and scope IDs, not private root paths, full credential scopes, raw proofs, MACs, credentials or keys.

Execution snapshots the context list, rereads the exact stored review and opens the same strict stage lease before admission. One transaction rechecks current activation, instance, policy, lifecycle, requester, approver and executor authority, expiry and actual target. It appends admission and audit with `OutcomeUnknown` before destination-key access. The one-use admission cannot be reclaimed or automatically retried.

The concrete held verifier resolves the destination key once, freezes its bytes after commitment matching, authenticates every retained positive ciphertext and every destination absence proof, and rechecks the complete tree, custody, names and bytes after the external key await and before returning. It does not read current source vaults or original source keys. No database transaction spans a key-provider await.

While that same guard remains held, completion again checks every current executor context and all original standing requester/approver grants and versions, lifecycle, policy, instance, activation and frozen completion-time bounds. `Succeeded` means the typed result is `HeldStageVerified`, with promotion, adoption, activation and external execution flags false. A change during the await or between verification and commit prevents that result. Each affected scope receives an audit event, so a sibling scope cannot be hidden behind the first scope's database anchor.

Cancellation, verification failure, lost authority, process interruption or unavailable final audit after admission leaves a consumed `OutcomeUnknown`. An ambiguous admission commit returns `ScopedRecoveryStageReviewAdmissionUncertain`; do not interpret it as no admission or retry blindly. If a successful final commit becomes ambiguous, reread protected history instead of attempting another verification. A recorded success is immutable, and unknown recording cannot demote it. The default operation budget is a cooperative 20 seconds; an uncooperative provider can retain the caller and guard until it actually returns. Cancellation never releases custody while that dependency is still using retained inputs.

## Schema 9 and recovery

Schema 9 adds separate `credential_scoped_recovery_stage_review_requests`, `credential_scoped_recovery_stage_review_approvals` and `credential_scoped_recovery_stage_review_executions` tables, immutable authority fields, one-use execution and retained success guards. Earlier DDL and checksums remain fixed. Current runtime opening with `initializeSchema:false` refuses an older schema; it does not migrate or repair it.

Use the existing trusted-owner signed backup and fresh staging flow for reviewed upgrades. This increment exercises upgrades only on disposable fixtures; it does not migrate an existing customer database. Backup validation requires the exact versioned schema and checksum chain before creating a restore destination. Restoring rotates database instance and policy, preserves completed history, and quarantines every unfinished scoped review, including consumed unknown executions. Restored history is evidence rather than replay authority.

The native synthetic tests cover actual mixed/absence-only stages, key-free preparation, exact context sets, revocation and expiry during provider awaits, one-use concurrency, cancellation, tree mutation, audit failure, schema upgrade/rollback/refusal, and restore quarantine. These checks establish the stated local protocol boundaries. They do not establish independent key backup, full historical coverage, malicious-owner resistance, source-host fencing, live deployment, legal clearance or production acceptance.
