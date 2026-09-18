# Run the transfer pipeline locally

The local mode runs the real .NET isolated Functions host, polling BlobTrigger dispatcher, QueueTrigger worker, and all three timers against Azurite. It requires no Azure subscription, Azure login, Docker, Azure CLI, or Bicep. Bicep remains the Azure provisioning path.

See [the exact local workflow](local-workflow.md) for diagrams, method-by-method calls, storage operations, failure paths, timer behavior, and the recorded smoke-run timeline.

## Start, verify, stop

From the repository root in PowerShell:

```powershell
./scripts/Run-Local.ps1
./scripts/Test-Local.ps1
./scripts/Stop-Local.ps1
```

`Run-Local.ps1` checks prerequisites, installs missing compatible tools, builds a separate local runtime copy, starts storage, creates the containers/queues, and starts the Functions host. It returns when the owned host reports Running on loopback. `Test-Local.ps1` verifies the complete transfer workflow. The host has no business HTTP endpoint or web UI; port 7071 is the local Functions runtime.

If only Windows PowerShell 5.1 is available, start with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Run-Local.ps1
```

Setup installs a portable PowerShell when needed and re-launches under it. On a machine without `pwsh` on PATH, subsequent commands can use `.\.tools\pwsh\pwsh.exe -NoProfile -File ./scripts/Stop-Local.ps1` (or `Test-Local.ps1`). The process-only execution policy option does not change machine policy.

Separate setup/check commands:

```powershell
./scripts/Setup-Local.ps1
./scripts/Setup-Local.ps1 -CheckOnly
```

`-CheckOnly` runs version checks and reports missing tools without downloading them or changing the saved tool selection. Once prerequisites and NuGet packages are cached, startup reuses them; restore still performs the repository's configured dependency advisory checks, so initial setup and fresh auditing need network access.

## Prerequisites and installation

| Component | Required/selected version | Behavior |
|---|---|---|
| Windows | Windows 11 x64 or ARM64 | Lifecycle scripts use Windows process/listener APIs. |
| PowerShell | 7.4+; portable fallback 7.6.2 | Windows PowerShell 5.1 bootstrap reuses installed PowerShell 7 or downloads it. |
| .NET SDK | `global.json`: 10.0.300 with allowed stable feature roll-forward | Reuse compatible installed SDK; otherwise install the pinned SDK ZIP locally. |
| Node.js | 22.x or 24.x; portable fallback 24.16.0 | Reuse a compatible installation; npm must accompany it. |
| Functions Core Tools | v4, at least 4.14.0 | Reuse a compatible installation or download the 4.14.0 minimal Windows distribution for the isolated .NET host. |
| Azurite | 3.37.0 | npm installs an exact project-local version under `.tools/npm`. |

Portable downloads come from official Microsoft/.NET, Node.js, PowerShell, and Azure Functions release sources. ZIP downloads are checked against published hashes; npm records its dependency lockfile in the ignored tool folder. Existing global tool installations are not replaced, and no machine-wide PATH change is made. The selected paths are saved in `.local/tools.json`.

Core Tools 4.12.0 was present on the initial workstation but failed host startup with the chosen Storage extension's `Microsoft.Extensions.Options` 10 dependency. Core Tools 4.14.0 successfully started the same application and also supports explicit loopback binding. Compilation and direct invocation tests alone did not expose this compatibility issue. The optional Python-worker notice from older ARM64 tooling is unrelated to this C# application.

## What starts

| Service | Address | Contents |
|---|---|---|
| Azurite Blob | `127.0.0.1:10000` | `incoming`, `transfer-ledger`, `local-destination`, plus host-created runtime containers |
| Azurite Queue | `127.0.0.1:10001` | `transfer-work`, both poison queues, plus host-created internal queues |
| Azurite Table | `127.0.0.1:10002` | Local runtime storage service |
| Functions host | `127.0.0.1:7071` | Dispatcher, worker, reconciliation, ledger audit, poison monitor |

All use the emulator's public development account. The local source, ledger, and destination share that one emulator account in different containers; this does not emulate the Azure account/network/RBAC separation. Local development configuration sets `UseDevelopmentStorage=true` and never accepts a configurable cloud connection string through local mode.

`LocalDevelopment__Enabled=true` is accepted only with the Development Functions environment, no Azure site/instance markers, the fixed emulator binding connections, and version enumeration disabled. Invalid/mixed local and cloud settings fail startup. With local mode absent or false, the existing Azure identity/HTTPS configuration remains in effect. The launcher clears inherited cloud storage/site settings only for its child host process.

Configuration is copied from `src/BlobTransfer/local.settings.azurite.example.json` into `.local/app/local.settings.json`. Source-folder Azure development settings are not overwritten. The launcher uses the checked-in local defaults on each start; treat changes to that example as reviewed source changes. Local timers run every 15 seconds. Azure schedules remain unchanged.

## Evidence and data

```text
.tools/                    Downloaded portable tools and npm emulator installation
.local/tools.json          Selected executable paths
.local/run.json            Owned process IDs, start times, paths, and run log directory
.local/app/                Built local Function application and generated settings
.local/operator/           Built seeding/smoke tool
.local/data/               Persistent Azurite data
.local/logs/<run-id>/       Host/emulator logs and synthetic smoke evidence
```

These paths, local settings, `.env` variants, node modules, logs, and test outputs are ignored by Git. The example settings remain tracked and contain only the emulator shorthand, not production secrets.

The smoke test uploads three differently named files with identical bytes using ordinary Blob SDK uploads. It never sends a queue message or calls a Function method directly. It waits for three completed request records, verifies one expected destination and byte equality, checks that sources remain, and requires matching dispatcher log records plus all three timer heartbeats. Run logs and a JSON receipt identify the synthetic requests.

Queue delivery and reconciliation can overlap; a lease-contention exception may be retried by the host. A transient failed invocation is not automatically a lost transfer. Review final ledger state and poison queues. Do not infer a production SLA from the local smoke run.

## Lifecycle safeguards

- A second run or occupied port is rejected; existing services are not adopted or stopped.
- Stop validates each recorded PID against its executable and start time before terminating that process tree. It stops the owned host before the owned emulator.
- Data and logs survive stop/restart. Reset is separate and requires an explicit data-erasure switch:

```powershell
./scripts/Reset-Local.ps1 -ClearData
```

Reset stops the owned run, refuses occupied ports, validates that the data path remains under this checkout's `.local` directory, rejects junctions/symbolic links, and removes only `.local/data`. Tools and logs remain. Never use reset to erase real Azure data; the script has no Azure operations.

The existing `Test-Recovery.ps1` suite also uses ports 10000–10002 and deliberately refuses a running emulator. Stop local mode before running that suite, then restart afterwards.

## Validation limits

Local host tests exercise actual listeners, dispatch, queue processing, timer scheduling, leases, deduplication, and content verification. Azurite does not qualify Azure retained-version enumeration/overwrite recovery, ADLS HNS, Entra identity, RBAC propagation, private endpoints/DNS, Azure Monitor alert delivery, or regional capacity. Version scanning is disabled only in the generated local configuration. Those checks remain Azure acceptance requirements.

The installed-SDK/Node reuse path, project-local Azurite installation, Core Tools 4.14 download, and Windows PowerShell 5.1 bootstrap reusing installed PowerShell 7 were exercised on Windows ARM64. The missing-SDK, missing-Node, missing-PowerShell download, and Windows x64 installation branches still require clean-machine coverage.

References: [local Functions development](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local), [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite), and [Core Tools 4.14 release](https://github.com/Azure/azure-functions-core-tools/releases/tag/4.14.0).
