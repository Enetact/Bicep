# Stop, build and start the local application suite

Use PowerShell 7.4 or later on Windows ARM64 or x64, from the full source Git checkout's repository root. The read-only catalog inside a portable application ZIP is not sufficient for suite builds or source lifecycle control. [Manage-Suite.ps1](../scripts/Manage-Suite.ps1) coordinates the application and its authenticated Aspire telemetry collector. It defaults to read-only status.

```powershell
./scripts/Manage-Suite.ps1 -Action Status
./scripts/Manage-Suite.ps1 -Action Restart -WhatIf
./scripts/Manage-Suite.ps1 -Action Restart
```

The `Studio` profile starts the Aspire dashboard and portal. Use `Full` when the local Blob copy Functions/Azurite runtime is also wanted:

```powershell
./scripts/Manage-Suite.ps1 -Action Start -Profile Full
./scripts/Manage-Suite.ps1 -Action Stop -Profile Full
```

The Functions profile runs the dispatcher, queue worker and timers against the fixed local emulator account. It creates the local containers/queues but preserves existing emulator data. It does not connect those triggers to Azure storage.

## What is built and what runs

Both profiles build the six maintained executable entry projects and their referenced libraries in separate `.local/suite/builds/<run-id>/` outputs. Generated `obj/WorkerExtensions.csproj` projects are not independent suite applications.

| Component | Studio | Full |
|---|---|---|
| Aspire dashboard and OTLP collector | Starts first; authenticated, loopback only | Same |
| Platform Studio backend and HTML/CSS/JavaScript UI | Starts after telemetry is ready | Same |
| Blob copy Functions host | Built; not started | Starts against Azurite after telemetry is ready |
| Azurite Blob, Queue and Table | Not started | Starts before the local Functions host |
| ProductFunctions HTTP/Service Bus samples | Built; not started | Built; not started |
| TransferTool, tagging CLI and operations CLI | Built; run only when explicitly requested | Same |
| Codex and MCP provider subprocesses | Start through the portal's browser-owned connection/workflow handlers | Same |
| Fabric, managed pools and other Azure workload resources | No creation or deployment | No creation or deployment |

`ProductFunctions` contains a Service Bus trigger and Azure receipt adapter. There is no qualified local Service Bus profile here, so starting that worker automatically could consume an existing queue. A successful suite build does not establish Azure trigger or deployment readiness.

The portal normally uses `http://localhost:5087`; its local configuration can select another allowed port. Aspire endpoints come from [config/observability.json](../config/observability.json): dashboard `127.0.0.1:18888`, OTLP HTTP `127.0.0.1:4318` and OTLP gRPC `127.0.0.1:4317`. Full adds Functions on `127.0.0.1:7071` and Azurite on ports `10000`–`10002`.

## Actions and failure handling

| Action or option | Behavior |
|---|---|
| `Status` | Reports component/process ownership and readiness. It does not sign in, download tools, build or launch services. Provider availability and account authorization remain separate. |
| `Build` | Builds all executable projects in isolated outputs without stopping running services or overwriting their binaries. Restore and dependency auditing can require network access. |
| `Start` | Preflights the selected profile, builds, starts or reuses the verified owned collector, supplies its private OTLP settings to application processes, and waits for component readiness. A stopped portal's existing checkpoint is detected automatically. |
| `Stop` | Quiesces the owned portal and any local runtime registered to this checkout before the collector, regardless of the requested start profile. Approved isolated CLI/Codex sign-ins use a protected checkpoint by default. Local data, source settings, reports and logs remain. |
| `Restart` | Performs preflight/build before stopping owned producers, preserves eligible sign-ins, then starts the selected profile with telemetry ready first. Studio does not restart a previously running Functions profile; select Full to restart that producer too. |
| `-WhatIf` | Describes the operation after read-only checks. Does not install, build, write keys/checkpoints, stop or start processes. |
| `-NoBrowser` | Suppresses browser opening. The protected local shortcuts can be opened manually. |
| `-InstallMissing` | Explicitly allows supported prerequisite installation during Start/Restart. Without it, missing tools block the operation with setup instructions. Build expects prerequisites to be installed already. |
| `-DiscardConnections` | Explicitly selects the ordinary portal connection-cleanup behavior instead of preserving eligible sign-ins. It does not revoke grants or delete terminal CLI accounts. |
| `-ResumePending` | Resumes an existing protected checkpoint when the portal is stopped. It does not replace a checkpoint or take over another browser session. |

The scripts use terminating errors and explicit native exit-code checks. `catch` reports failure without replaying builds, deployment actions or sign-ins. `finally` releases lifecycle resources and restores process-scoped environment changes, including OTLP authentication settings. These follow Microsoft's [try/catch/finally](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_try_catch_finally) and [ShouldProcess](https://learn.microsoft.com/en-us/powershell/scripting/learn/deep-dives/everything-about-shouldprocess) guidance.

Unknown ownership, inaccessible process metadata, reused PIDs, foreign listeners on selected component ports, escaped/linked state paths and ambiguous signed-in profiles block control. Studio does not adopt another project's unregistered emulator merely because it occupies a Full-only port. A port number alone never authorizes terminating a process. Rollback is limited to newly started, independently verified owned processes; it does not stop a collector that was already running before this invocation. If a registered producer cannot be stopped, the collector stays available and the operation reports the partial failure.

## Preserve connections across separate stop/build/start steps

```powershell
./scripts/Manage-Suite.ps1 -Action Stop
./scripts/Manage-Suite.ps1 -Action Build
./scripts/Manage-Suite.ps1 -Action Start
```

The stop creates the same Windows-user-protected credential handoff used by [the fresh portal restart](portal-fresh-restart.md). Start detects it and opens a private one-time resume shortcut; explicit `-ResumePending` is also available for recovery and requires an existing checkpoint. The browser independently verifies Microsoft audiences, Codex readiness and the active GitHub CLI account before reconnecting eligible MCP catalogs. Registered-app MSAL memory cannot be migrated. Expiry, consent, Conditional Access and provider availability can still require reconnecting a service.

The preserving stop uses the protected helper's isolated preflight build before quiescing the portal; it does not rebuild/start the application afterward. If that helper build or checkpoint validation fails, the stop fails with the original app available and original credentials retained. `-DiscardConnections` is the explicit cleanup path when preservation is not wanted.

After startup, review **Connections** and **Agent workflows**. Successful process health, a saved credential file or a telemetry connection does not prove cloud permissions. A cancelled browser resume request can leave tool catalogs disconnected even when account sign-ins recovered; reconnect those providers individually. No old workflow evidence, plans or approvals authorize a new operation.

Never print, share or commit `.local` authentication files, private dashboard login links, resume shortcuts or OTLP headers. The dashboard starts before producers so startup/failure spans can be ingested, and remains available until owned producers have stopped. Forced process termination cannot guarantee export of unfinished spans or pending telemetry batches. Aspire MCP exposes the dashboard's telemetry reads through the existing exact-plan review flow; it does not manage app resources or grant execution authority.

## Component scripts and verification boundary

[Manage-Portal.ps1](../scripts/Manage-Portal.ps1) retains its portal status view and forwards its lifecycle actions to the Studio suite. [Get-Portal.ps1](../scripts/Get-Portal.ps1) is the read-only component status command. The individual portal and Functions scripts remain available for component diagnostics. Direct `Stop-Portal.ps1` stops only the portal and preserves eligible connections by default; `-DiscardConnections` selects session cleanup. Direct `Start-Portal.ps1` starts/verifies the collector unless the suite is already managing that prerequisite. Use the suite controller when all selected components must stop or start together.

The suite does not control desktop Codex, unrelated services or separate review instances, and it does not publish portable application packages. Local Functions workflow verification remains an explicit `./scripts/Test-Local.ps1` operation; that test uploads synthetic emulator data and is not run merely by suite startup. See [local runtime behavior and limits](local-development.md).

Synthetic lifecycle tests cover ordering and expected failures without stopping a live app. Isolated builds, real listener readiness, authenticated telemetry ingestion and cloud/model acceptance are separate evidence; consult the task's recorded results rather than treating this guide as a live readiness receipt.
