# Local customer-cell activation authority

Implemented source, 24 October 2026. The foundation can require one active database binding for cooperating API and worker processes on a single machine. An independently protected authority directory binds the customer/cell, canonical database path and persistent database instance identity. Shared runtime guards prevent an activation switch until all participating source runtimes stop. This adds a local activation boundary to [signed backup verification](foundation-signed-backups.md) and the existing recovery holds; it supplies no cloud action or automatic disaster-recovery activation.

## Scope and configuration

Protected API and worker configuration may set:

```json
{
  "requireActivationAuthority": true,
  "activationAuthorityDirectory": "C:\\protected-cell-authority\\customer-cell"
}
```

These are host configuration fields, independent of customer feature configuration, entitlement documents and application roles. The default remains false for legacy local flows. Enable the requirement consistently on every participating API and worker, with the same authority directory for that customer cell. A process that bypasses required mode or selects a different authority root is outside this coordination boundary.

Required startup reads the existing database identity without creating or migrating it, acquires a matching authority lease, and then opens the store without initialization. The identity is checked again after opening. Runtime dispatch must use the current database identity from its store transaction when asserting the lease. A missing authority, invalid metadata, wrong customer/cell, wrong path/instance or unavailable guard fails closed. Required mode never silently provisions an authority or falls back to legacy startup.

Schema version 5 adds `database_instance_id`, a persistent lowercase 32-hex identity. Normal reopening retains it; restore rotates it while advancing recovery policy and holding uncertain work. This separates an original database from a recovered copy, including a recovered copy later placed at the same filesystem path. The identity is neither a credential nor an approval token.

## Explicit owner workflow

The activation commands are trusted-owner C# operations. There is no HTTP, AHP or MCP activation/signing endpoint and no model approval tool.

1. Initialize and validate the intended cell through the existing trusted setup flow. Provision a separate authority directory explicitly:

   ```csharp
   var authority = FoundationCellActivationAuthority.Provision(
       protectedAuthorityDirectory, customerId, cellId);
   ```

   The directory must be new and its immediate parent must already exist with trusted custody. Fresh provisioning is Windows-only and atomically reserves the directory with the current Windows SID as owner and a protected owner-only DACL. Guard and marker files are also created with private custody from their first creation. An existing empty, partial or competing directory is refused without adoption, ACL repair or deletion. A reservation failure leaves existing data unchanged; a failure after this command's reservation retains the new partial directory for inspection.

   Parent checks reject NULL/absent DACLs, untrusted owners and effective foreign write/delete-child/delete/ACL-control permissions. Broad read access and inherit-only permissions that do not apply to the parent do not imply write access. Provisioning refuses known activation, software-installation, publisher, cell-initialization, credential/key/rollback and legacy-envelope ancestor namespaces, including partial sentinels. Existing authority opens validate their own protection. API and worker processes therefore need the same admitted local OS identity for this local profile. Supporting multiple service identities or another provisioning OS needs separate implementation and qualification.

2. Read the actual existing database identity and current authority state, then activate with an exact compare-and-swap:

   ```csharp
   var identity = FoundationStore.ReadCellInstanceIdentity(databasePath, customerId, cellId);
   var current = authority.ReadCurrent();
   var active = authority.Activate(databasePath, identity.DatabaseInstanceId,
       current?.Generation ?? 0, current?.Sha256);
   ```

   An unactivated authority returns null. Generation zero requires a null prior digest. Later commands must supply the exact current generation and checksum. Activation verifies the actual existing database's customer/cell/schema/instance under the exclusive guard before committing. Incorrect instance IDs, stale generation/digest and duplicate current bindings are rejected.

3. Runtime composition opens the existing authority and retains its lease for the full API or worker lifetime:

   ```csharp
   var authority = new FoundationCellActivationAuthority(
       protectedAuthorityDirectory, customerId, cellId);
   var lease = authority.AcquireLease(databasePath);
   // Retain lease until the runtime stops; before dispatch, pass its actual transaction identity.
   lease.AssertCurrent(databasePath, actualDatabaseInstanceId);
   ```

   This overload rejects nonlocal paths before any database access, then reads the actual instance under the held shared guard. The explicit `AcquireLease(databasePath, expectedInstanceId)` overload also remains available and rejects a stale expected identity.

4. Before activating a restored database, stop every participating original API and worker and release their leases. Read the restored database's new instance ID and the current authority state. Activate the restored binding with that exact prior generation/digest, then start only the restored runtimes. Reopening the original binding now fails. Recovered queue entries still require the existing reconciliation process; activation does not approve or replay them.

The module checks exact current generation continuity. It does not choose a recovered snapshot, validate its signature on behalf of the operator, infer which backup is newest or stop processes forcibly. Use the [signed restore path](foundation-signed-backups.md) and independently protected latest-backup authority before considering activation.

## Guard and metadata behavior

Each runtime lease holds `.activation.guard` open read-only with shared read access. Activation needs an exclusive read/write handle and returns `ActivationAuthorityBusy` while any lease holder remains. Releasing one API guard while another worker remains cannot admit the switch. OS process exit releases that process's handle; the activation record remains until an explicit subsequent command.

`AssertCurrent` checks retained lease generation/checksum, authority identity, current binding and the supplied actual database path/instance. Disposed leases reject further use. Runtime guards are ordinary non-inheritable file handles; the owned process test opens separate handles explicitly rather than inheriting an operator's credentials or application session.

Metadata uses a checksummed authority marker plus fresh generation files, `activation-0001.json` onward. Every record commits to the previous checksum and exact binding. Writes use a new temporary file, flush it, and atomically rename it to a new final filename while holding the exclusive guard; prior records are never overwritten. Readers reject gaps, modified checksums, unknown files, abandoned partial writes, unsupported schemas, duplicate/case-ambiguous/unknown JSON fields and future or unordered timestamps. An interrupted write needs trusted owner review; startup never deletes evidence or rewinds the active generation automatically.

The authority holds at most 1,024 generations and each metadata file is limited to 16 KiB. Paths must be absolute, canonical and local; Windows UNC/mapped-network paths, root paths, linked ancestors/entries and ambiguous trailing dot/space segments are rejected. Database files must already exist and cannot be placed inside the authority directory. Fresh provisioning checks the immediate parent's custody and known ancestor markers; the caller remains responsible for independent protected storage, higher ancestor custody and a reviewed filesystem. Metadata contains database paths and customer/cell bindings, so retain it privately even though it contains no bearer token, private key or inherited login cache.

## Limits and evidence

This is single-machine coordination among cooperating processes using the same protected root. It is not a distributed lease, multi-host fence, OS-administrator defense, WORM store or cryptographic proof that the authority directory was not rolled back. An owner able to clone/replace authority history, change application binaries/configuration or bypass required mode can defeat the coordination. Different authority directories do not coordinate. Windows ACL and file-sharing behavior are the intended local qualification boundary; Unix permissions and advisory sharing require separate deployment acceptance.

The control does not cancel external work already submitted, revoke provider credentials, protect copied encryption keys or establish production RPO/RTO. Existing `OutcomeUnknown` recovery holds, independent backup/signer/latest-pin custody and external-effect reconciliation remain necessary. Azure deployment, live pipeline submission, real sign-in and the primary desktop portal are unchanged by the local tests.

[FoundationCellActivationTests](../tests/SelfService.Foundation.Tests/FoundationCellActivationTests.cs) uses new temporary real SQLite cells and restored copies. It covers shared guards, stop-before-switch, stale CAS, persisted reopen, wrong binding, changed/partial metadata, path/instance checks, independent authorities and owner-only Windows ACL validation. Its Windows process fixture starts two owned hidden PowerShell processes with an explicit environment allowlist and separate shared guard handles, verifies that both must stop before activation succeeds, and cleans up only those original process handles. This verifies the local guard mechanism; actual customer service identities, deployment topology and production recovery operations still require acceptance.
