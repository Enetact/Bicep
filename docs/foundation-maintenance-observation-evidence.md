# Maintenance observation history and restore evidence

An exact reviewed `ObservedPublished` receipt records that the authenticated current credential envelope matched the previously admitted candidate at observation time. It is separate from the original maintenance execution, which remains `OutcomeUnknown`. It does not prove when the candidate was first published, retroactively report success, establish that an earlier attempt had no effect, or authorize a retry. No provider or remote token outcome is inferred.

The observation flow reuses the four existing human maintenance capabilities and standing Operator, Approver and Auditor roles. Exact proposal, separate approval, current scope/version/policy and concrete vault custody checks remain required. It introduces no parallel role registry, generic verifier or default HTTP/AHP/MCP/model operation. See the [maintenance contract](foundation-credential-maintenance.md) and [original maintenance evidence](foundation-credential-maintenance-evidence.md).

## Evidence states and limits

| Record | Meaning | Limits |
| --- | --- | --- |
| Prepared/Approved observation request | An exact public observation proposal and, when present, separate approval | No vault mutation, current finding or original success conclusion |
| Immutable `ObservedPublished` receipt | A recorder observed the exact current candidate under the approved bindings | An observation, not historical execution proof, remote effect, token-use permission or future validity |
| Quarantined restored request without receipt | Old unfinished review authority is held | No automatic observation, credential scan, forced reconciliation, replay or no-effect conclusion |
| Original `OutcomeUnknown` execution | Its admission remains consumed and its original uncertainty/history remains intact | Never rewritten to Succeeded because an observation exists |

Current bytes that do not match the candidate are not evidence of no earlier effect. A pending protected rollback authority still requires its separate explicit reconciliation; observation cannot bypass that fence. Hashes, local audit history and a protected review record have different trust boundaries from independently protected keys/receipts or production custody.

## Schema-7 backup and restore

Snapshot verification reuses the migration's read-only schema-7 observation history/checksum, table/index and immutable-guard checks, alongside the existing database digest, cell binding, SQLite integrity/foreign-key and audit-chain validation. No backup constructor or migration runs against the source snapshot. These checks do not independently attest every business record or the current vault. Typed maintenance/observation reads separately check their commitments and state consistency.

Restore retains the existing fresh-target workflow, database-instance rotation and policy increment. In the same recovery SQLite transaction it quarantines every Prepared/Approved observation request **without an immutable receipt**, with a scoped `RecoveredCredentialMaintenanceObservationHeld` audit event. Existing approvals, receipt bytes, observed timestamps and original execution records are retained. A completed request/receipt is historical; its copied approval cannot supply current recorder or mutation authority. Already-quarantined requests are not revived on a later restore.

The existing schema-6 recovery hold still quarantines unfinished original maintenance intents, while preserving their unknown executions and completed local-result receipts. Schema-7 observation handling does not rewrite those original executions. A fresh post-restore review, if explicitly prepared through the supported flow, must bind the current restored database instance and policy and obtain a new exact approval. A copied observation request is insufficient even when the credential still matches its observed candidate.

There are no vault calls, token/key lookups, default profile scans, credential writes or automatic observations during restore. A required recovery-audit failure rolls back its state/identity/policy transaction; the existing error path removes only the fresh failed destination, preserving the source cell, backup, custody and history. Repeated restores do not turn unknown execution into known success. No operation stops an original host or establishes a multi-host/power-loss recovery guarantee.

## Scoped metadata exports

Current scoped Auditors use the existing [paged export](foundation-paged-exports.md) and audit-anchor APIs. Canonical page/manifest hashes remain independently recomputable for the pinned metadata bytes. A snapshot captured before an observation does not gain that later receipt/event, and later restoration does not rewrite old pages into current authority. Creator/version/policy/scope/lifetime checks continue to fence reads.

Audit projections contain typed metadata and commitments, excluding tokens, key bytes, encrypted envelope bodies, raw source artifacts, connection configuration and provider payloads. Scope/actor identifiers and operational history are still customer-sensitive. The observation creates no signing key, new proof format or independent full-chain inclusion proof for a scoped page.

## Disconnected qualification

The [observation evidence fixtures](../tests/SelfService.Foundation.Tests/FoundationCredentialMaintenanceObservationEvidenceTests.cs) use fresh temporary SQLite cells, authenticated synthetic profiles, generated test keys and signed synthetic principals. They cover preserved original unknown history, immutable scoped exports, current role/scope isolation, restored unfinished requests, retained observed receipts, repeated restore, schema-7 rejection and recovery-audit failure without touching vault state. Exact receipts and outcomes belong in the dated validation record.

No real credential cache, primary session, provider pipeline, directory grant or Azure resource is involved. Local evidence grants no independent security, commercial, production, signing, publication or supported-host recovery acceptance.
