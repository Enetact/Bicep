# Private worker receipt admission

The independent Foundation worker now validates and reserves an optional configured `receiptPath` before SQLite preflight, store construction, registration, heartbeat or queue access. This closes a local startup hazard: writing an otherwise fresh JSON file inside activation, installation, package or credential metadata could invalidate that protected namespace. Receipt rejection leaves the existing cell and namespace untouched.

The receipt remains `platform-studio.foundation-worker-process.v1`, with the same process/start-time/executable/assembly/configuration commitment used by existing ownership checks. It is local process metadata, not a credential, authorization grant, deployment result or proof that a workflow completed. See [independent worker operations](foundation-independent-worker.md).

## Destination policy

The parent directory must already exist and have trusted custody. Its owner must be the current Windows SID, SYSTEM or Builtin Administrators. Effective allow rules cannot grant another SID write/create/append, delete/delete-child, permission-change or ownership-change rights. Inherit-only rules do not apply to the parent and are skipped; deny rules do not broaden authority. These are read-only checks: no directory is created or existing ACL repaired for a receipt. A workspace/TEMP directory with inherited write rights for other accounts can be refused. Provision a dedicated private runtime directory through the trusted owner process first.

NULL or absent DACLs are rejected from the raw security descriptor before evaluating individual rules. Windows grants access for a NULL DACL; an empty DACL has different semantics and grants none. See [Microsoft's NULL and empty DACL guidance](https://learn.microsoft.com/en-us/windows/win32/secauthz/null-dacls-and-empty-dacls). This check does not repair an existing directory.

A simple relative JSON path such as `worker-001.receipt.json` or `ordinary.json` resolves against the configuration directory. Absolute paths must resolve to a fixed local Windows drive. Drive-relative/root-relative paths, UNC/network locations, forward-slash aliases, empty/dot/traversal segments, trailing spaces/dots, alternate data streams, device names, control characters and linked/reparse paths are rejected. The path input is bounded to 4,096 characters.

The final filename must end in `.json`. It cannot claim an activation/installation/package/custody marker or credential/DPAPI envelope filename. The configured database, its WAL/SHM/journal aliases and the configuration file are protected, including aliases involving the deterministic reservation filename. A configured activation-authority directory is also excluded explicitly.

Every ancestor is checked for these markers and existing credential envelope names, whether the sentinel is a file or directory:

| Protected namespace | Detection |
|---|---|
| Activation | `authority.json`, `.activation.guard` |
| Preview software/installations | `.installation-owner.json`, `.installation.guard`, `.package-owner.json` |
| Current or legacy credential custody | `.credential-namespace.json`, `cell-binding.json`, `.keys.lock`, `.vault.lock`, `.rollback.lock`, `*.credential.json`, `*.dpapi.json` |

The checks inspect path metadata and names; they do not open credential/key contents. An empty, unmarked directory cannot be identified as a future credential namespace from its name alone. Trusted composition must keep runtime receipts outside all intended data, authority, software and custody roots.

## Reservation and publication

For a non-null receipt path, the worker atomically creates `<receiptPath>.reserve` with a protected Windows ACL allowing only the current owner SID. The file is held exclusively with delete-on-close semantics. An existing receipt, reservation, directory or competing reservation is refused before cell access. Cooperating processes racing for one destination therefore admit one worker registration. Existing bytes and ACLs are preserved.

After registration, the worker serializes at most 32 KiB into a new owner-only temporary file, durably flushes it and renames it to the final destination without replacement. It rechecks immediate-parent custody before reservation, publication and path-based temporary cleanup, including after closing the temporary write handle. This prevents the close-to-rename window from relying on a parent that grants another account replacement rights. The final path first appears as complete JSON and retains the private ACL. A private reservation exists while the worker is alive and disappears when its original handle closes, including process termination. Existing reservations are never adopted or removed by another invocation. The worker never broadens ACLs on an operator-supplied directory or pre-existing file.

Unexpected publication/storage failure after reservation can occur after registration; the ordinary stop/error path handles that partial startup. This is not a transaction spanning SQLite and the filesystem. Ancestors above the checked immediate parent remain a trusted custody prerequisite: a hostile ancestor owner can replace a child directory. A noncooperating process running as the current trusted owner, SYSTEM or an administrator can also interfere after validation. No overwrite is allowed, and no protection against those privileged actors is claimed. Optional null `receiptPath` retains the existing foreground stdout-only behavior and creates no receipt files.

## Read-only launcher preflight

`Run-FoundationWorker.ps1` invokes the worker's shared policy before creating its output/log files. Other trusted lifecycle tooling can use the same command:

```powershell
dotnet <worker-output>/SelfService.Foundation.Worker.dll --validate-config C:\PlatformStudio\runtime\worker.json
```

This operation parses the same strict, case-sensitive, bounded configuration and validates its mode, public trust metadata, literal loopback mock endpoint and receipt destination. A configured receipt is mandatory. Qualified `ClaimHold`/`MockLoopback` metadata can be inspected without executing it; `qualificationOnly:true` remains required. Missing cells are not opened or created, no activation lease is acquired, no reservation/output file is written and no worker starts. A passing path check therefore does not mean the cell exists or the worker is ready.

Success writes exactly the small `platform-studio.foundation-worker-preflight.v1` JSON response with `receiptPathValidated:true`, `workerStartupPerformed:false`, `externalDispatch:false` and `configurationSha256` over the exact bounded input bytes. It includes no path, payload, key material or credential. Callers must bind the expected schema, hash and false execution flags and reject nonzero exit or unexpected output. The execution path separately reserves/rechecks the destination immediately before cell access; an earlier read-only observation is not a lasting filesystem lock.

The launcher retains a read handle over the exact bounded configuration through preflight and startup, clears inherited credentials/proxy/profiler/startup-hook settings, and caps preflight output at 8,192 characters per channel with a ten-second deadline. It checks the response schema, five fields, hash and false startup/external flags before log creation. Native log handles are created with protected owner-only ACLs and `CREATE_NEW`; existing files are not replaced. Configuration handles are released when startup returns. These controls preserve the launcher's existing original-process-handle ownership checks and explicit inherited-handle allowlist.

## Local qualification

On 24 October 2026, the new `FoundationWorkerReceiptTests` and existing worker process regressions passed together: **83 passed, 0 failed, 0 skipped**, with zero build warnings/errors. The isolated receipt is `artifacts/foundation-worker-receipt-check/results/worker-receipt-parent-settled.trx`. Locked restore retained normal vulnerability auditing and did not change dependency locks.

The real hidden process fixtures use new temporary owner-private cells and cleared environments. They verify exact database/audit/registry and protected namespace preservation on refusal; marker/envelope file or directory sentinels, database/sidecar/reservation aliases; noncanonical/UNC/device paths; direct/ancestor Windows junction refusal; existing-file bytes/ACL preservation; protected published/reservation ACLs; one winner for two racing workers; and valid absolute/relative JSON receipts. Eight widened-parent rights cases refuse read-only/actual startup without ACL repair or cell mutation; a real launcher case confirms no logs, and an inherit-only rule remains admissible while created files stay private. Read-only preflight cases verify the exact response/hash, qualified mock metadata and no state creation even with a missing configured database. Real PowerShell launcher cases prove protected/invalid configurations create no sibling logs or cell mutations, and normal startup returns private logs/receipt and releases the configuration handle. They wait for natural child exit through the verified process handle before cleanup. The ordinary disabled worker leaves a genuinely queued job untouched. Existing process regressions continue to cover competition, crash/recovery, activation and owned launcher/stop behavior.

No Azure/provider/model call, real credential, sign-in, primary process restart, OS identity provisioning or production service admission occurred. Native ACL/junction behavior is qualified on the tested local Windows host; production filesystem/service identities and supported-host lifecycle acceptance remain separate work.

The later Owner custody continuation passed a combined **157/157** Windows cohort, including **74 worker receipt** and **13 existing worker process** cases, under `artifacts/foundation-owner-custody-check/results/owner-worker-custody-settled.trx`. The added worker case applies a NULL DACL only to a fresh synthetic parent, verifies preflight/start/launcher refusal without cell or output changes, and restores its original access rules in `finally`. Existing `.rollback.lock` sentinel rows also pass. This is additional focused evidence, with zero build warnings/errors, rather than a new full application-suite result.
