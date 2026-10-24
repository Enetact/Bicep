# Independent customer-cell worker

`src/SelfService.Foundation.Worker` is a separate .NET 10 process that reuses the foundation host's `FoundationJobRunner`, durable SQL queue, exact-plan approval checks, reviewed feature configuration and fenced completion methods. It provides local worker separation and process-level qualification. Provider credentials, customer pipeline acceptance and production deployment remain separately qualified work. No worker mode in this build can call a real GitHub, Azure DevOps, Azure or model endpoint.

## Existing cell required

An administrator must provision and migrate the customer cell through the normal application lifecycle first. The worker opens the configured database read-only, checks its customer/cell binding and exact `FoundationStore.CurrentSchemaVersion` (schema 4), then opens the normal store. A missing database, older/newer schema, mismatched binding or linked path is rejected. The worker never bootstraps an administrator, creates a cell, installs a migration, signs in, reads the portal's cached tokens or acquires a provider credential.

Give the worker's eventual service identity access only to its assigned cell/configuration/runtime directory. The local script clears inherited credential, proxy, profiler and startup-hook environment variables. It does not provision an OS identity, change filesystem ACLs or claim that a shared developer account provides production identity isolation. One customer/cell/store is fixed for the lifetime of the process. Keep these configurations and receipts private and outside source control.

## Start and stop

Run the foundation test/build profile or publish the worker project before launching. A worker needs its complete publish output, including referenced assemblies, dependency metadata and SQLite native runtime files. Copying only the worker DLL is insufficient.

Create a private runtime directory and a JSON configuration such as `.local/foundation-workers/worker.json`. Paths in the configuration resolve relative to that file. Replace the example IDs and database path with an already provisioned disposable cell for initial qualification:

```json
{
  "customerId": "customer-a",
  "cellId": "cell-a",
  "databasePath": "../cells/customer-a/cell.sqlite",
  "mode": "Disabled",
  "pollIntervalMilliseconds": 250,
  "heartbeatIntervalMilliseconds": 1000,
  "runSeconds": 0,
  "receiptPath": "worker-001.receipt.json"
}
```

The receipt directory must already exist and the receipt filename must be fresh. Configuration defaults to `Disabled`; this mode registers, heartbeats and stops without claiming queued work. An explicit `workerId` is optional; the default is a new random identity. Use a fresh receipt path after every process restart.

From the repository root in PowerShell 7.4 or later:

```powershell
./scripts/Run-FoundationWorker.ps1 -ConfigurationPath .local/foundation-workers/worker.json
./scripts/Run-FoundationWorker.ps1 -Stop -ReceiptPath .local/foundation-workers/worker-001.receipt.json
```

`-DotNetPath` selects the .NET executable. `-WorkerDllPath` selects a built/published worker; its default is the foundation profile's `artifacts/foundation-build/bin/SelfService.Foundation.Worker/release/SelfService.Foundation.Worker.dll`. The launcher starts a hidden helper, waits up to ten seconds for its durable registration receipt, then returns. Unique stdout/stderr log files beside the receipt keep the worker independent of the launcher's capture pipes; existing log paths are never deliberately reused. It leaves the primary host and all sign-ins running.

The private `platform-studio.foundation-worker-process.v1` receipt contains PID, full-precision process start time, executable/assembly/configuration paths, configuration SHA-256, worker instance nonce and generation. The stop script verifies these against the current process and its complete command line before stopping it. A changed PID owner, start time, command or configuration refuses the stop. The script force-stops only the verified helper, so its heartbeat becomes stale. A direct foreground invocation supports Ctrl+C for cancellation and a recorded graceful stop:

```powershell
dotnet ./artifacts/foundation-build/bin/SelfService.Foundation.Worker/release/SelfService.Foundation.Worker.dll --config .local/foundation-workers/worker.json
```

## Synthetic execution modes

Both execution modes require `qualificationOnly: true` in the JSON **and** `-EnableMockExecution` in the launcher (or `--allow-mock-execution` in a direct invocation). A mode switch alone cannot consume a pending queue.

| Mode | Behavior |
|---|---|
| `Disabled` | Registers/heartbeats only; never claims. No endpoint or execution flag is accepted. |
| `MockLoopback` | Uses the shared job runner and sends only to a literal loopback HTTP fixture. Set `mockEndpoint`, for example `http://127.0.0.1:55123/`. The fixture must already exist; the worker does not start a provider emulator. |
| `ClaimHold` | Qualification-only crash probe: claims one queued job with a two-second pre-effect lease and waits. It never calls `StartDispatch` or a transport. `runSeconds` must be between 1 and 600; no endpoint is accepted. |

`MockLoopback` validates the official upstream request through the existing pipeline adapter, then rebases it onto the configured literal loopback address. Redirects, proxy use, cookies and credential headers are disabled. Requests carry `X-Platform-Studio-Mock: true`. Hostnames, HTTPS/public fixture origins, URI credentials/query/fragment and invalid root paths are rejected. Explicit synthetic mode is for disposable fixtures; it does not distinguish a test cell from a real cell automatically.

Reviewed licensed feature configuration is rechecked in the same SQL transaction that admits mock dispatch. A `Licensed` cell requires an explicit `entitlementTrustKeys` array of issuer/key-ID/`subjectPublicKeyInfoPem` objects in the worker configuration. Set the trusted host configuration's `requireSignedEntitlement: true` for a licensed installation; customer feature changes or rollback then cannot restore unrestricted development preview mode. Its default `false` supports the explicit local preview qualification profile. Only public keys belong here. The worker does not infer trust from a license, download keys or read a private signing key. Feature availability grants no human role, approval or provider authority.

## Heartbeats and recovery

Schema 4 adds a durable worker registry. Trusted register/heartbeat/stop methods require the cell/customer binding and a per-instance nonce/generation fence. A live worker ID cannot be replaced within thirty seconds. A stale or stopped registration may be replaced with a new generation; old handles cannot heartbeat or stop the replacement. Registration and stop events enter the append-only audit; periodic heartbeats do not fill the audit log. Registry limits are sixteen live processes per cell and 1,024 retained registrations, with obsolete metadata pruned after one day; audit history remains.

Authorized `PlatformOperator` health contains aggregate registered/live/disabled/stopped/stale counts, latest heartbeat time, active leases and unknown-outcome jobs. It excludes worker IDs, PIDs, paths, credentials and customer payloads. The default stale threshold is thirty seconds. Background heartbeats continue while a fixture request is pending.

The shared queue retains its existing limits and recovery rules: a pre-effect expired lease can be reclaimed with an incremented fence, with at most three claim attempts. An expired dispatched lease becomes `OutcomeUnknown`; it is never automatically replayed. A successful mock dispatch receipt states `mock: true` and `deploymentVerified: false`; it confirms fixture acceptance only. Force-stopping a worker does not reconcile an uncertain effect.

## Qualification and evidence

```powershell
./scripts/Test-FoundationWorker.ps1
```

This profile restores locked packages with vulnerability auditing enabled and writes `artifacts/test-results/foundation-worker/foundation-worker.trx` plus `summary.json`. Acceptance requires every discovered test to pass with no skips. It starts only disposable synthetic cells, owned hidden worker/PowerShell processes and an in-process TCP loopback fixture. It tests competing processes, once-only dispatch, pre-effect crash recovery, post-effect unknown-outcome/no-replay behavior, schema/binding rejection without migration, disabled defaults, explicit public license trust, stale/fenced heartbeat metadata and launcher/stop ownership. The post-effect crash scenario accelerates only the disposable fixture's lease deadline; production lease duration is unchanged.

Use normal Windows filesystem/process permissions for this profile. A restricted app-container may deny the atomic receipt rename or process metadata inspection; that failure does not establish acceptance. No Azure deployment, provider request, model call, new sign-in or primary-host restart occurs.

See [the foundation service](foundation-service.md), [security and operations](foundation-security-operations.md), and [the foundation architecture](plans/platform-studio-foundations-and-customer-isolation.md) for the surrounding authorization and commercial readiness boundaries.
