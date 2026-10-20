# Getting started

[Wiki home](README.md) · [Overview](platform-overview.md) · [Workload catalog](workload-catalog.md) · [Status](completion-status.md)

Start with the local portal, then connect the services needed for your task. Browsing workloads, reading skills, viewing proposed diagrams and inspecting recovery policies require no Azure subscription or model connection.

## Prerequisites

| Component | Requirement |
|---|---|
| Operating system | Windows 11 ARM64 or x64. |
| Shell | PowerShell 7.4 or newer. |
| Source | Git checkout; run commands from the folder containing `config/workloads.json`. |
| .NET | SDK compatible with [global.json](../global.json), currently 10.0.300 with feature-band roll-forward. |
| Node.js | 22 or newer; required by source setup and saved-discovery analysis. |
| Network | Package restoration and optional Microsoft/Codex sign-in or Azure/ADO operations. |

Portal browsing does not require Docker, Azure CLI, Bicep or Functions tools locally. Infrastructure compilation/tests and the Blob copy emulator have separate prerequisites in their guides.

## Install and start

Run in order from the repository root:

```powershell
./scripts/Manage-Suite.ps1 -Action Status
./scripts/Manage-Suite.ps1 -Action Start
```

The default Studio profile checks prerequisites, builds all six executable entry projects in isolated outputs, starts the authenticated Aspire collector, then starts [Platform Studio](http://localhost:5087/). Missing prerequisites block startup unless `-InstallMissing` is explicitly supplied. Reopen PowerShell if setup reports that PATH must be refreshed. PowerShell and any required Windows App Installer/winget prerequisite must already be available.

The application is a single-user localhost service. Ownership receipts and logs live under `.local/portal/`; authenticated collector state is under `.local/observability/`, isolated suite builds under `.local/suite/builds/`, and generated reports under ignored `artifacts/`. Add `-Profile Full` to coordinate the local Blob copy Functions/Azurite runtime too. ProductFunctions' cloud-backed Service Bus sample is built without being started.

For subsequent rebuilds, use `./scripts/Manage-Suite.ps1 -Action Restart`. Preflight/build failures leave running services available; startup waits for telemetry before application producers. Eligible isolated sign-ins are checkpointed by default and reverified in the resumed browser. Use `-Action Status` to inspect without changing sessions and `-WhatIf` to preview a lifecycle action. See [suite ordering, options and recovery](local-suite.md).

## Connect only what your task needs

| Task | Connection and setup |
|---|---|
| Discover Azure resources or networks | Connections → Local authentication — Azure CLI, or browser sign-in through a separate portal Entra registration; caller needs read access to the selected scope. |
| Select discovery, read Preview or request pipelines | Azure DevOps browser sign-in, project membership, appropriate pipeline access and registered definitions. |
| Request AI review or generate a source draft | Compatible native Codex executable and the portal's separate ChatGPT browser sign-in; workflow-specific Azure/ADO evidence may also be required. |

Follow [Microsoft browser sign-in setup](local-portal.md#configure-microsoft-browser-sign-in) and [agent setup](agent-workflows.md#setup-and-dependencies). Do not reuse the deployment service connection's client ID for the portal. Sign-in does not automatically start discovery, model inference or a pipeline.

Skip registration setup for local CLI authentication. If you choose the registered-app method, create the portal registration, then stop the portal and configure the identifiers together:

```powershell
./scripts/Manage-Suite.ps1 -Action Stop
./scripts/Setup-Portal.ps1 -TenantId '<portal-tenant-guid>' -ClientId '<portal-client-guid>'
./scripts/Manage-Suite.ps1 -Action Start
```

Use **Connections** and **Agent workflows** to complete the respective browser login flows. The [portal guide](local-portal.md) covers consent, MFA, cancellation, errors and configuration overrides.

## First self-service request

1. Review the [workload catalog](workload-catalog.md) and select a product in the portal.
2. Configure the environment and approved region; inspect resources, dependencies and cost assumptions.
3. Use **Discover** after the matching ADO definitions and service connection are configured.
4. Select a successful matching discovery run and request **Preview only**.
5. Read the ADO summary, property changes, blockers and portal Preview diagram. A successful Preview does not enable a disabled target.

Platform owners must complete [onboarding and acceptance](workload-onboarding.md) before allowing **Preview and deploy**. The [ADO setup menu](ado-pipeline-registration.md) can register missing definitions in a reviewed batch; it does not configure permissions or queue those pipelines automatically.

## Update or stop

After pulling source changes, rebuild the owned suite:

```powershell
./scripts/Manage-Suite.ps1 -Action Restart
```

To stop without rebuilding, run `./scripts/Manage-Suite.ps1 -Action Stop` with the same profile used at startup. Producers stop before telemetry, and eligible sign-ins, data, reports and local configuration are preserved. A later Start detects a stopped portal's pending checkpoint automatically. `-DiscardConnections` explicitly requests portal session cleanup instead. The scripts verify process ownership before controlling a service.

## Verify and package

Repository metadata checks do not restart services:

```powershell
./scripts/Test-RepositoryHygiene.ps1
./scripts/Update-ServiceCatalog.ps1 -Check
./scripts/Update-Manifest.ps1 -Check
```

For portal tests and portable Windows packages, stop the portal first:

```powershell
./scripts/Manage-Suite.ps1 -Action Stop
./scripts/Test-Portal.ps1
./scripts/Publish-Portal.ps1
./scripts/Manage-Suite.ps1 -Action Start
```

The package script builds ARM64 and x64 ZIPs under `artifacts/portal-packages/<timestamp>/`. They are unsigned portable applications, not installers. Suite build/restart commands require the full source Git checkout; the packaged catalog is not that checkout. Node remains a prerequisite for analysis, and Codex is separate. Follow [package configuration](local-portal.md#arm64-and-x64-packages). Recorded x64 tests used Windows ARM emulation; native x64 hardware acceptance remains outstanding.

The wider `./scripts/Test-Project.ps1` suite needs the infrastructure/runtime toolchain described in [validation](validation.md) and [workload onboarding](workload-onboarding.md). Historical passes do not qualify a new checkout automatically.

## Run Blob copy locally without Azure

This is a separate workload runtime using real local Functions and Azurite.

For the portal and telemetry together with this runtime, choose `./scripts/Manage-Suite.ps1 -Action Start -Profile Full`. The component-only commands remain available:

```powershell
./scripts/Run-Local.ps1
./scripts/Test-Local.ps1
./scripts/Stop-Local.ps1
```

The launcher checks/installs missing tools into ignored project folders; Stop preserves data. This path needs no Azure subscription, Docker, Azure CLI or Bicep. It does not emulate hosted Event flow, Service Bus, Entra or private Azure networking. Read [local prerequisites and troubleshooting](local-development.md), [the execution flow](local-workflow.md) and [dispatcher methods](dispatcher/README.md).

Contributors running the complete qualification suite also need Python 3.10+ for the Private Data Platform recipe contract tests (standard library only). The portal itself does not launch Python for this offering. See [manual recipes](../workloads/private-data-platform/recipes.md).
