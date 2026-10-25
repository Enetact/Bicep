#requires -Version 7.4
<#
.SYNOPSIS
Build, inspect or control the local application suite. Collect telemetry before starting producers and stop it last.
.EXAMPLE
./scripts/Manage-Suite.ps1 -Action Restart
.EXAMPLE
./scripts/Manage-Suite.ps1 -Action Start -Profile Full
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Status','Build','Start','Stop','Restart')][string]$Action='Status',
    [ValidateSet('Studio','Full')][string]$Profile='Studio',
    [switch]$InstallMissing,
    [switch]$NoBrowser,
    [switch]$ResumePending,
    [switch]$DiscardConnections
)
. (Join-Path $PSScriptRoot 'Suite.Common.ps1')
if (!$IsWindows) { throw 'The local application suite currently supports Windows only.' }
if ($InstallMissing -and $Action -notin @('Start','Restart')) { throw 'InstallMissing requires Start or Restart.' }
if ($ResumePending -and $Action -notin @('Start','Restart')) { throw 'ResumePending requires Start or Restart.' }
if ($DiscardConnections -and $Action -notin @('Stop','Restart')) { throw 'DiscardConnections requires Stop or Restart.' }
if ($DiscardConnections -and $ResumePending) { throw 'DiscardConnections and ResumePending cannot be combined.' }
$before = Get-SuiteStatus -Profile $Profile
if ($Action -eq 'Status') { $before; return }
if ($Action -ne 'Build') { Assert-SuiteControllable $before }
if (!$PSCmdlet.ShouldProcess((Get-SuiteRoot),"$Action $Profile application suite; preserve sign-ins unless DiscardConnections is explicit")) { return }

$previousEnvironment = Get-SuiteTelemetryEnvironment
$lock = $null
$phase = 'Acquire lifecycle lock'
$collectorStarted = $false
$collectorIdentity = $null
try {
    $stateRoot = New-SuitePrivateDirectory (Join-Path (Get-SuiteRoot) '.local/suite')
    # Exclusive file ownership serializes suite operations; a leftover unlocked file is safe to reuse.
    try { $lock = [IO.File]::Open((Join-Path $stateRoot 'lifecycle.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch { throw 'Another suite lifecycle operation is active or its lock is inaccessible. No process was changed.' }
    $current = Get-SuiteStatus -Profile $Profile
    if ($Action -ne 'Build') { Assert-SuiteControllable $current }
    if ($ResumePending -and (!$current.CredentialCheckpointPending -or $current.Portal.State -notin @('Stopped','StaleReceipt'))) {
        throw 'ResumePending requires an existing encrypted checkpoint and a stopped portal.'
    }
    if ($Action -eq 'Start' -and $current.Healthy) { $current; return }
    if ($Action -in @('Start','Restart') -and $current.Portal.State -eq 'Ready' -and $Action -eq 'Start' -and !$current.Portal.TelemetryConfigured) {
        throw 'The existing portal was started without this dashboard. Use Restart to attach telemetry while preserving its sign-ins.'
    }

    $build = $null
    if ($Action -in @('Build','Start','Restart')) {
        $phase = 'Build complete suite in isolation'
        if ($InstallMissing) { & (Join-Path $PSScriptRoot 'Setup-Portal.ps1') -PrerequisitesOnly -InstallMissing | Out-Null }
        if ($Profile -eq 'Full' -and $Action -ne 'Build') { & (Join-Path $PSScriptRoot 'Setup-Local.ps1') -CheckOnly:(!$InstallMissing) -WriteReceipt | Out-Null }
        $build = & (Join-Path $PSScriptRoot 'Build-Suite.ps1') -Confirm:$false
        if ($Action -eq 'Build') { $build; return }
        # Source or ownership drift after a successful build must not stop a replacement service.
        $observed = Get-SuiteStatus -Profile $Profile
        Assert-SuiteControllable $observed
        if ($current.Portal.Identity -ne $observed.Portal.Identity -or $current.Telemetry.Identity -ne $observed.Telemetry.Identity -or
            $current.LocalRuntime.Identity -ne $observed.LocalRuntime.Identity) { throw 'Suite ownership changed during build. No producer was stopped.' }
        $current = $observed
    }

    if ($Action -in @('Stop','Restart')) {
        $phase = 'Stop portal and owned MCP/agent helpers'
        if ($current.Portal.State -notin @('Stopped')) {
            if ($DiscardConnections) { & (Join-Path $PSScriptRoot 'Stop-Portal.ps1') -DiscardConnections -Confirm:$false | Out-Null }
            else { & (Join-Path $PSScriptRoot 'Restart-PortalFresh.ps1') -CheckpointOnly -NoBrowser -Confirm:$false | Out-Null }
        }
        $phase = 'Stop local Functions and emulator'
        if ($current.LocalRegistered) { & (Join-Path $PSScriptRoot 'Stop-Local.ps1') -Confirm:$false | Out-Null }
        # A failed producer stop aborts the sequence before the telemetry sink is touched.
        $stopped = Get-SuiteStatus -Profile $Profile
        Assert-SuiteControllable $stopped
        if ($stopped.Portal.State -ne 'Stopped' -or $stopped.LocalRegistered) { throw 'A producer remains registered or running. Telemetry was kept available.' }
        $phase = 'Stop telemetry last'
        & (Join-Path $PSScriptRoot 'Stop-Observability.ps1') -Confirm:$false | Out-Null
        if ($Action -eq 'Stop') {
            $result = Get-SuiteStatus -Profile $Profile
            if ($result.State -ne 'Stopped') { throw 'Suite stop verification did not establish a fully stopped suite.' }
            $result; return
        }
        $current = Get-SuiteStatus -Profile $Profile
    }

    $phase = 'Start authenticated telemetry first'
    $collector = & (Join-Path $PSScriptRoot 'Start-Observability.ps1') -NoBrowser -InstallMissing:$InstallMissing -Confirm:$false
    if ($collector.State -ne 'Running' -or !$collector.Healthy) { throw 'Telemetry did not become ready; no new producer was started.' }
    $collectorStarted = [bool]$collector.StartedByInvocation
    $collectorIdentity = $collector.Identity
    & (Join-Path $PSScriptRoot 'Use-Observability.ps1')
    if ($Profile -eq 'Full' -and $current.LocalRuntime.State -ne 'Ready') {
        $phase = 'Start local Functions and Azurite'
        & (Join-Path $PSScriptRoot 'Run-Local.ps1') -PreparedBuild $build.BuildRoot -SkipTelemetry -Confirm:$false | Out-Null
    }
    $phase = 'Start portal and restore protected sign-ins'
    if ($current.Portal.State -ne 'Ready') {
        if (Test-Path -LiteralPath (Get-SuiteCheckpoint) -PathType Leaf) {
            & (Join-Path $PSScriptRoot 'Restart-PortalFresh.ps1') -ResumePending -NoBrowser:$NoBrowser -Confirm:$false | Out-Null
        }
        else {
            if ($current.Portal.State -ne 'Stopped') { throw 'A stale portal registration requires Restart before Start.' }
            & (Join-Path $PSScriptRoot 'Setup-Portal.ps1') | Out-Null
            & (Join-Path $PSScriptRoot 'Start-Portal.ps1') -SkipTelemetry -NoBrowser:$NoBrowser -Confirm:$false | Out-Null
        }
    }
    $phase = 'Verify full profile readiness'
    $result = Get-SuiteStatus -Profile $Profile
    if (!$result.Healthy -or !$result.Portal.TelemetryConfigured) { throw 'The selected suite profile is not fully ready with telemetry. Inspect its component status.' }
    if (!$NoBrowser) { Start-Process -FilePath $result.Telemetry.shortcutPath }
    $result
}
catch {
    # Preserve the original failure and keep the collector whenever a producer may still need it.
    if ($collectorStarted) {
        try {
            $remaining = Get-SuiteStatus -Profile $Profile
            if ($remaining.Portal.State -eq 'Stopped' -and !$remaining.LocalRegistered -and
                $remaining.Telemetry.State -in @('Running','Unhealthy') -and ![string]::IsNullOrWhiteSpace($collectorIdentity) -and
                $remaining.Telemetry.Identity -ceq $collectorIdentity) {
                & (Join-Path $PSScriptRoot 'Stop-Observability.ps1') -Confirm:$false | Out-Null
            }
        }
        catch { Write-Warning 'Collector cleanup could not be verified; its ownership receipt and diagnostics were retained.' }
    }
    Write-Warning "Suite operation failed during: $phase. Existing data, logs and encrypted sign-in checkpoints are retained. Use Status to inspect the partial result."
    throw
}
finally {
    try { Restore-SuiteTelemetryEnvironment $previousEnvironment }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}
