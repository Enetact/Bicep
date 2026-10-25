# Trusted local cell-owner commands

`SelfService.Foundation.Owner` provides bounded, explicit Windows owner operations over the existing Foundation store, signed-backup and activation-authority APIs. It is separate from customer HTTP/AHP commands and from the host/worker runtime. Possession of this executable grants no Entra, Azure, GitHub, Azure DevOps or pipeline permission. This increment is for one trusted Windows owner and local dedicated-cell files; it is not a production service installer or an operating-system administrator boundary.

Selecting a restored database with `authority.activate` does not release its [restore execution hold](foundation-recovery-execution-hold.md). Schema 11 keeps fresh ordinary execution blocked while inspection and recovery preparation remain available. Existing operational status distinguishes execution hold from storage readiness; there is no recovery-release command yet.

Run the published `runtime/SelfService.Foundation.Owner.dll` with the supported .NET runtime, or build its project into an isolated artifacts directory. The command accepts exactly these arguments, in this order:

```powershell
dotnet <package-root>/runtime/SelfService.Foundation.Owner.dll --request C:\PlatformStudioOwner\requests\inspect.json --output C:\PlatformStudioOwner\results\inspect-001.owner-result.json
```

Replace the example paths with absolute local paths. The request must already exist; the result parent must already exist, and the result file must be new and end with `.owner-result.json`. No existing result is replaced. Requests are UTF-8 JSON, at most 64 KiB and depth eight, with exact camel-case property names. Unknown, duplicate and case-ambiguous properties are rejected. Requests accept public metadata only; there is no private-key, PFX/password, credential-import, backup-signing or login command.

The process writes one fixed-schema JSON receipt to standard output, containing only the operation, success/rejection state, safe code and `externalExecutionAuthorized:false`. Exit code zero means that local command completed; exit code two means it was rejected. Standard output does not contain paths, identities, request values or exception text. The detailed result, including scoped public metadata, is written to the explicitly named fresh result file. Result JSON is limited to 128 KiB. A rejected command can leave a private rejection result; malformed requests rejected before reservation leave no result.

Before any operation or result reservation, the existing request file and immediate request/result parent directories must have trusted Windows custody. Owners must be the current SID, SYSTEM or Builtin Administrators. Effective allow rules cannot grant another SID write, append, create, delete, delete-child, permission-change or ownership-change rights. Broad read-only access is allowed for public requests. Inherit-only rules do not apply to the checked object and are skipped; deny rules do not broaden authority. The command reads at most 64 KiB through a read handle held until command completion, excluding ordinary writers and replacement while it executes. These checks apply to read-only inspection and identity proposals too. Refusal does not repair ACLs, reserve a result or touch a cell or authority.

NULL or absent DACLs are rejected explicitly. They grant access and must not be confused with an empty DACL, which grants none; see [Microsoft's NULL and empty DACL guidance](https://learn.microsoft.com/en-us/windows/win32/secauthz/null-dacls-and-empty-dacls). The checks never install or rewrite a request or parent ACL.

Result files, new initialization directories/markers and new restore staging directories are created with protected ACLs allowing only the current Windows owner SID. Database files inherit their private directory. A different service identity will need a separately reviewed file-access arrangement; this tool does not add service accounts or broaden permissions. Operators must protect snapshots, manifests and ancestor directories before use. Only the immediate request/result parents receive the explicit owner/write ACL check; ancestor custody, the current owner, SYSTEM and administrators remain trusted. Filesystem path checks do not claim protection from a hostile process running as that same owner or administrator.

Local paths reject UNC/network drives, root paths, linked/reparse ancestors, alternate data streams, device names and ambiguous Windows path suffixes. Results cannot use SQLite database/sidecar names, initialization destinations or activation-authority directories. Even an unrelated command refuses results beneath activation, installation, package or credential namespace markers, including `.credential-namespace.json`, `.vault.lock`, `.keys.lock`, `.rollback.lock`, `cell-binding.json` and legacy `*.credential.json`/`*.dpapi.json` sentinels. Marker and envelope directories also cause refusal. Initialization, new authority provisioning and restore likewise refuse to create unrelated state inside those protected namespaces. Keep authority metadata, immutable software, credential custody, runtime state and result files in separate dedicated directories.

## Read-only inspection

```json
{
  "operation": "inspect",
  "customerId": "customer-example",
  "cellId": "cell-example",
  "databasePath": "C:\\PlatformStudioOwner\\cells\\example\\cell.sqlite"
}
```

Inspection opens SQLite read-only, binds the exact requested customer/cell and performs `quick_check`. It reports `CurrentSchema`, `LegacySchemaRequiresStagedUpgrade` or `UnsupportedSchema` with the observed/supported versions. It does not construct a migrating `FoundationStore`, change the database, verify the complete audit history or establish runtime readiness. Legacy databases remain unchanged; there is no in-place migration command.

## Initialize a new owner-designated cell

```json
{
  "operation": "initialize",
  "customerId": "customer-example",
  "cellId": "cell-example",
  "destinationDirectory": "C:\\PlatformStudioOwner\\cells\\new-example",
  "administratorActorKey": "user:11111111-1111-1111-1111-111111111111:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "reviewerActorKey": "user:11111111-1111-1111-1111-111111111111:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "projectId": "default",
  "environmentId": "dev"
}
```

All GUIDs above are synthetic. Supply the reviewed tenant/object-ID pair for each distinct human administrator and independent reviewer. Actor keys must use the canonical lowercase `user:<tenant-guid>:<object-guid>` form. These local memberships do not create Entra users or grant cloud permissions.

The destination directory must not exist, even if empty; its parent must already exist. Native creation atomically reserves a new owner-only directory, then writes an initialization marker and a current-schema `cell.sqlite`. Existing directories and zero-byte databases are never claimed or migrated. The command bootstraps the explicit administrator and reviewer through the existing store API. It does not activate the database or start a runtime. If initialization fails after reservation, its new partial directory remains for owner inspection; the command does not recursively remove it.

## Provision, inspect and activate an authority

Provision a separate, nonexistent authority directory explicitly:

```json
{
  "operation": "authority.provision",
  "customerId": "customer-example",
  "cellId": "cell-example",
  "authorityDirectory": "C:\\PlatformStudioOwner\\authorities\\example"
}
```

Use the same fields with `operation:"authority.status"` to read the current binding. A newly provisioned authority has a null binding. Provisioning reuses the core owner-protected authority API; it is not a host startup fallback. Never place receipt files or databases in its directory.

After reviewing the exact database instance and current authority status, request compare-and-swap activation:

```json
{
  "operation": "authority.activate",
  "customerId": "customer-example",
  "cellId": "cell-example",
  "authorityDirectory": "C:\\PlatformStudioOwner\\authorities\\example",
  "databasePath": "C:\\PlatformStudioOwner\\cells\\new-example\\cell.sqlite",
  "databaseInstanceId": "11111111111111111111111111111111",
  "expectedGeneration": 0,
  "expectedBindingSha256": null
}
```

Replace `databaseInstanceId` with the actual current identity, never the example. Only an unbound authority uses generation zero/null digest. Later activations require the exact current generation and SHA-256 from the reviewed status result. A stale head, wrong scope/instance or held runtime lease rejects activation. The CLI does not kill processes, release another process's lease, change host/worker configuration, automatically activate a restored database or switch running processes. Stop only owned runtimes through their existing lifecycle tooling, activate the reviewed instance, update trusted runtime configuration separately and perform readiness checks. See [activation and recovery](foundation-cell-activation.md).

## Public verification and staged signed restore

Both backup commands require explicit `customerId`, `cellId`, `snapshotPath`, `manifestPath`, `pinnedPublicKeyPem`, `pinnedPublicKeySha256` and `restoreAuthority`. `restoreAuthority` is the independently trusted latest `FoundationBackupRestoreAuthority` object, including its customer/cell, manifest ID, generation, manifest SHA-256, key ID and public-key SHA-256. Obtain both public-key pin and latest-authority commitment through a trusted channel separate from the incoming packet; copying these values from that packet defeats the anti-substitution/rollback boundary.

`pinnedPublicKeyPem` accepts exactly one RSA `PUBLIC KEY` SPKI block, bounded to 16 KiB. Private-key material and an incorrect public fingerprint are rejected. The manifest is parsed with the existing strict signed-backup contract. `backup.verify` calls the core verification API to check the signature, latest-authority binding, scope/time constraints, exact snapshot byte hash/length, SQLite integrity/foreign keys, schema commitment and audit history. It has no destination field and creates no recovery database. Successful verification establishes those local packet checks at that time; it does not authorize activation or future work.

`backup.restore` additionally requires a fresh absolute `destinationDirectory`, whose parent must already exist. Full verification occurs before this directory is reserved with an owner-only ACL. The fixed database filename inside it is `cell.sqlite`. A read-only source handle excludes concurrent writers/deletion on Windows throughout preflight, directory creation and the existing signed restore API. Supported migration occurs only in the staged copy. Existing directories, files and SQLite sidecars are not overwritten. Invalid signature/source preflight creates no staging directory; a later failure retains the new owned directory for inspection. Source database identity remains unchanged. The new copy receives a new database instance, changed policy epoch, fenced worker registrations and pending work held as `OutcomeUnknown` for reconciliation. The command does not update the independent latest-authority record, fence the source host, replay work or activate the staged copy. Review the private result, recovery evidence and required reconciliation before a separate exact activation. See [signed backups](foundation-signed-backups.md).

## Additional custody checks

Revision 14's `authority.provision` delegates to atomic private Windows reservation: the destination must be absent and its immediate parent already present with trusted custody. It cannot adopt a competing empty directory or place authority files inside known credential/software/cell namespaces. Backup commands also use read-only versioned schema admission before staging; malformed source structures are rejected rather than reconstructed. The detailed [activation](foundation-cell-activation.md) and [backup](foundation-signed-backups.md) contracts describe these boundaries and remaining production limits.

## Entra identity proposal

Use `operation:"identity.propose"`, matching outer `customerId`/`cellId`, and nested `identityContract` containing the reviewed [public Entra input](../config/foundation-entra-onboarding.example.json). The detailed private result contains `publicCanonicalJson`, its SHA-256 and compatible public `hostIdentity` metadata. Definition IDs and caller identities are explicit inputs; the CLI does not allocate replacement IDs. All identity-grant, external-execution, provider-permission and runtime-acceptance flags remain false. See [Entra proposal review](foundation-entra-onboarding.md) before merging metadata or making any independently authorized directory change.

## Local qualification

The owned-process `FoundationOwnerProcessTests` exercise the real executable with cleared environment variables and synthetic local stores, ephemeral signing keys and explicit public pins. They cover private creation, duplicate initialization, read-only legacy/future inspection, exact activation/held leases/stale heads, receipt and mutation namespace refusal, staged recovery/held work, rejected authorities, scoped identity proposals and strict bounded requests. Test processes are hidden and are waited/disposed through their original process handles. No Azure resource, provider pipeline, Graph call, real sign-in, primary portal process or OS certificate store is involved. Passing these local checks is not production identity, service-account, recovery-drill or deployment acceptance.

On 2026-10-24 the focused Windows cohort passed **21/21**, with zero build warnings/errors, under isolated `artifacts/foundation-owner-check`. The receipt is `artifacts/foundation-owner-check/results/owner-namespace-final.trx`; it covers the fresh-directory restore contract, tampered-snapshot refusal and preservation of all three installation/package-marker namespaces. Existing dependency lock files were unchanged by the normal audited restore; the new Owner project has its own lock file. This is a focused command qualification, not a new full application-suite result.

The later custody continuation passed **157/157** native Windows checks: **70 Owner**, **74 worker receipt** and **13 existing worker process** cases, with zero failures/skips or build warnings/errors. Its isolated receipt is `artifacts/foundation-owner-custody-check/results/owner-worker-custody-settled.trx`. Owner cases include 24 foreign-write-right combinations across the request file and both immediate parents, read-only public access, inherit-only parent rules, a retained request handle during a blocked read-only operation, and protected namespace file/directory sentinels. Three native NULL-DACL cases reject before result creation or authority changes; test-only DACL changes are restored in `finally`, comparing owner, protection and actual access-rule bytes because Windows may normalize inheritance control flags. Existing cell/authority bytes remain unchanged on refusal. The combined run also covers the worker's NULL-DACL preflight/start/launcher refusal and `.rollback.lock` regressions. The first combined receipt is retained with four test-fixture restoration failures; the settled receipt records the corrected fixtures without changing admission policy.
