# Keep a restored cell from starting fresh execution

A restored database needs protection against fresh work as well as replayed jobs. The existing restore protocol rotates the database instance and policy, stops copied worker registrations, quarantines copied approvals and holds recovered jobs. Schema 11 adds a durable execution hold for the entire restored cell instance. Selecting that database in the activation authority does not release the hold.

This is a prerequisite for reviewed recovery and adoption. It does not adopt credentials, activate a credential namespace, stop another host, revoke an issued provider token or establish independent latest-set/key custody. Existing direct vault consumers remain outside the Foundation execution boundary.

## What the hold protects

The hold is bound to the actual customer, cell and database instance. Ordinary execution checks it before admitting fresh work or obtaining changing-operation credential authority. Current grants, an enabled customer, an Active lifecycle, valid entitlements and a selected activation instance do not bypass it. An idempotency key for earlier work does not authorize new execution after restore.

| Operation | Behavior while held |
|---|---|
| Ordinary plan enqueue, worker claim, lease renewal and pipeline dispatch | Refused with `RecoveryExecutionHeld` before admission or external execution. |
| Current credential dispatch permits and changing credential maintenance | Refused before keys/effects, with current authority checked again at publication boundaries. |
| Authorized reads, audit/history and status | Remain available under existing current customer/scope authorization. |
| Administrative recovery preparation and inactive read-only recovery verification | Remain available; they cannot release the hold or activate credentials. |
| Safe cancellation, unknown-outcome handling and inactive reconciliation | Retain their existing recovery semantics; they cannot automatically replay a changing effect. |

Use `store.GetRecoveryExecutionHold(currentContext)` to inspect the current hold. The read requires existing standing human recovery-read, audit-read or lifecycle-read authority, with the corresponding roles and exact customer/cell context. It does not expose another customer's hold inventory or create a public unauthenticated status route.

Existing operational status adds `RecoveryExecutionHeld` and a fixed `ExecutionHoldReason`; these flags expose no backup or namespace details. Storage readiness and worker heartbeat liveness keep their existing meanings. A worker can remain alive while execution is held; its existing bounded retry/backoff does not create dispatch authority.

## Durable origin and upgrades

`recovery_execution_holds` retains immutable hold records. A new restore binds its actual source database instance, newly rotated destination instance, source backup hash, policy version and creation time. Restore holds and their audit commit in the existing recovery transaction. Repeated restores retain history and create a hold for each new instance; history is bounded.

Snapshots from versions 1–4 predate database instance IDs. Their source identity is explicitly unknown; an ID generated during upgrade is never presented as the snapshot's source instance.

An already restored schema-10-or-earlier cell must not escape this protection merely by upgrading. Migration 11 checks verified historical `BackupRestored` audit evidence and creates a conservative hold for the actual current instance when needed. Historical source instance identity is explicitly unknown when the old record did not preserve it. The implementation does not invent that identity. Fresh cells without restore history remain unheld.

Earlier migration definitions and checksums remain fixed. Backup admission validates the new schema and retained hold integrity without repairing the incoming database. A missing or malformed current hold in a database with restore evidence fails closed. Existing customer databases are not changed by building or testing this code; migration and restore tests use disposable cells.

## Release is a separate recovery operation

This increment has no hold-release API, expiry, startup repair or lifecycle bypass. Restored cells remain suitable for inspection and recovery preparation. A later recovery-release protocol must independently authorize the exact instance, credential namespace and current checkpoint, establish the supported source-use fence, and coordinate credential/database activation with one-use admission and explicit uncertainty handling.

The existing schema-9 human review and schema-10 joint verification authorize read-only verification. Their successful results cannot release a restore hold or supply promotion authority. See the [joint verification guide](foundation-scoped-recovery-joint-verification.md), [security/operations](foundation-security-operations.md) and [commercial roadmap](plans/platform-studio-commercial-release.md).
