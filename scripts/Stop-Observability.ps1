#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param()
. (Join-Path $PSScriptRoot 'Observability.Common.ps1')
$paths = Get-ObservabilityPaths
$before = Get-ObservabilityStatus
if ($before.State -eq 'Stopped') { $before; return }
if ($before.State -notin @('Running','Unhealthy','StaleReceipt') -or ($before.State -eq 'Unhealthy' -and !$before.OwnershipVerified)) {
    throw "Observability stop blocked: $($before.State). $($before.Detail)"
}
if (!$PSCmdlet.ShouldProcess('Owned local Aspire dashboard', 'Stop telemetry after application producers have drained')) { return }
$lock = Open-ObservabilityLock
try {
    $current = Get-ObservabilityStatus
    if ($current.State -eq 'Stopped') { $current; return }
    if ($current.State -notin @('Running','Unhealthy','StaleReceipt') -or ($current.State -eq 'Unhealthy' -and !$current.OwnershipVerified)) {
        throw "Observability ownership changed before stop: $($current.State). No process was stopped."
    }
    $receipt = Read-ObservabilityReceipt
    $inventory = Get-ObservabilityInventory $receipt
    # The suite stops producers first. An exact receipt and complete child inventory are required;
    # neither a matching port/name nor a recursive process-tree kill grants termination authority.
    $targets = @($inventory.Native, $inventory.Parent) + @($inventory.Consoles)
    foreach ($target in $targets | Where-Object { $null -ne $_ }) {
        $verified = Get-ObservabilityExactProcess -Number $target.Process.Id -Executable $target.Process.Path -Started $target.Process.StartTime -Parent $target.Info.ParentProcessId
        if (!$verified) { continue }
        Stop-Process -InputObject $verified.Process -Force -ErrorAction Stop
        if (!$verified.Process.WaitForExit(10000)) { throw 'An owned observability process did not stop. Its receipt and private configuration were retained.' }
    }
    if (@(Get-ObservabilityListeners $receipt).Count) { throw 'An observability port is still occupied. Its ownership receipt was retained.' }
    $remaining = Get-ObservabilityInventory $receipt
    if ($remaining.Parent -or $remaining.Native -or $remaining.Consoles.Count) { throw 'An owned observability process remains active. Its ownership receipt was retained.' }
    Assert-ObservabilityPrivateFile $paths.Receipt
    Remove-Item -LiteralPath $paths.Receipt -ErrorAction Stop
    $after = Get-ObservabilityStatus
    if ($after.State -ne 'Stopped') { throw 'Observability stop could not establish a stopped state. Private keys and diagnostics were preserved.' }
    $after
} finally { if ($null -ne $lock) { $lock.Dispose() } }
