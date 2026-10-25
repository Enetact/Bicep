# Credential maintenance history and recovery evidence

Credential maintenance uses the existing dedicated-cell roles, exact intent approval, append-only audit chain and scoped metadata exports. It creates no new identity registry, product role, provider consent or credential-use permission. The trusted owner composes the store-backed executor with the existing vault; no model, HTTP, AHP or MCP credential endpoint is added.

## Intent is distinct from an observed result

The schema-6 maintenance intent records public target/binding metadata and an exact digest. `Prepared` means a decision was proposed; `Approved` records a separate current human approval. Neither state proves that the vault was changed. Admission records a one-use execution as `OutcomeUnknown` before any vault mutation is admitted. It is not a retryable queue item.

Only an observed, validated result can become `Succeeded`. An exception, cancellation or interruption after admission must leave an uncertain outcome rather than infer that the credential was unchanged. A committed revocation or rewrap can outlive a failed SQLite result update. Conversely, an admitted intent can exist without a published vault change. The SQLite cell and credential custody files do not share a distributed transaction.

| Evidence | What it establishes | What it cannot establish |
| --- | --- | --- |
| Prepared/approved intent and digest | A bounded exact public maintenance decision and its recorded approval | Credential mutation or current authority to use a token |
| Admission with `OutcomeUnknown` | A specific one-use execution was recorded | Whether the vault commit happened, or permission to retry |
| `Succeeded` and its public result commitment | The executor observed the validated local metadata transition | Remote token revocation, provider completion, key retirement or atomic commit across stores |
| `Quarantined` after restore | Restored unfinished intent is held for owner investigation | A forced success/failure disposition or automatic replay permission |
| Export digest/audit prefix | Integrity of pinned local metadata bytes or the separately verified signed prefix | Independence from an owner controlling the initial response, database, custody and trust pins |

Revoke disables a local profile; it does not revoke the provider's token remotely or cancel an already admitted request. Rewrap changes the encrypted envelope's write key/generation while preserving authenticated scope, expiry and enabled state. It does not acquire/renew a token, provision or delete a key, or grant a new scope. Existing [rewrap](foundation-credential-rewrap.md), [dispatch admission](foundation-durable-dispatch-authorization.md) and [rollback custody](foundation-credential-rollback.md) boundaries continue to apply.

## Restore quarantines unfinished authority

The existing [backup/restore](foundation-service.md#backup-and-restoration) flow copies a verified standalone SQLite snapshot into a new destination. Recovery rotates the database instance identity and increments the cell policy version. In that same recovery SQLite transaction, it quarantines every `Prepared` or `Approved` maintenance intent that has no `Succeeded` execution, including an approved intent whose execution is already `OutcomeUnknown`.

Each newly quarantined intent receives a scoped `RecoveredCredentialMaintenanceHeld` audit event with `OutcomeUnknown` / `RecoveryRequiresReconciliation`. Its existing execution, approval and public result bytes are preserved. Already-quarantined intents and completed successful executions remain historical records. An `Approved` parent retained for a successful execution is history, not reusable approval: the execution is one-use and its database-instance/policy binding is stale after restore.

No vault inspection, token/key lookup, credential write, default credential scan or automatic reconciliation occurs during SQLite restore. Even a vault that happens to match a copied intent's expected generation cannot revive the old intent. Restoring this database does not stop the original host or establish that separate credential/rollback authorities were restored safely. Activation fencing and protected operational custody remain separate obligations.

Snapshot validation checks the pinned database bytes, SQLite integrity/foreign keys, cell binding and audit chain. For schema 6 it also reuses the migration's read-only maintenance history/checksum and required table/index/guard checks. This is not independent verification of every business row or the current vault. Maintenance reads/admission separately verify the typed intent/target/result commitments and state consistency. Corrupt business metadata is not repaired or promoted into execution authority by restore.

If recovery cannot append a required audit event, its quarantine/instance/policy changes roll back together. The existing restore error path removes only the new failed restore target it created. It does not delete the source backup, original cell, vault, keys, receipts or audit history. These are local transactional/process guarantees; power loss, multi-host DR, physical erasure and production recovery acceptance are not qualified by these fixtures.

## Scoped audit and immutable exports

Current scoped Auditor grants control [paged metadata exports](foundation-paged-exports.md). Maintenance audit events use that same projection. They do not export intent payloads, credential material, encrypted envelopes, key bytes, connection configuration or provider request/response bodies. Scope/actor/profile identifiers and audit metadata can still be customer-sensitive and require protected storage and transmission.

An export freezes its captured audit tail. Later maintenance or recovery events do not alter existing pages or quietly convert an earlier uncertain observation into success. Clients can recompute SHA256 from the returned canonical manifest/page bytes and pin those commitments. The source roles, creator version, policy, exact scope and snapshot lifetime are checked again on page/recovery reads; revocation or a policy change denies an old snapshot.

The existing audit-anchor API can sign the cell's verified prefix with a separately supplied trusted signing handle. Maintenance adds no signer, private-key export or parallel proof format. A scoped page omits other projects' events, so it is not by itself a complete independently verifiable cell-chain inclusion proof. A local digest, a signature verified against an untrusted replacement key, and an independently protected key/receipt have different trust boundaries.

## Disconnected qualification

[FoundationCredentialMaintenanceEvidenceTests](../tests/SelfService.Foundation.Tests/FoundationCredentialMaintenanceEvidenceTests.cs) use only new temporary cells, synthetic profiles/keys and metadata. They exercise restored unfinished intents, preserved completed/unknown history, current scoped audit/export access, canonical export digest verification and exclusion of private credential/source/provider payloads. Existing audit and backup verification are reused; no new verifier or recovery registry is introduced.

The fixtures do not touch real credentials, primary sessions, existing customer data or Azure. They establish no provider/network, hosting, commercial, production-signing or deployment acceptance. Run them with the supported .NET SDK and the repository's audited locked restore, using an isolated artifact path; retain the original TRX as the outcome record.
