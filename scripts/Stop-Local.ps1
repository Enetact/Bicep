#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param()
. "$PSScriptRoot/local-common.ps1"
$state = Get-LocalState
if (!$state) { Write-Host 'No local run is recorded.'; return }
if (!$PSCmdlet.ShouldProcess('Owned local Functions and Azurite','Stop the local run; preserve emulator data, tools, builds and logs')) { return }
$lock = $null
try {
    $lock = [IO.File]::Open((Assert-LocalPath (Join-Path $localRoot 'local-lifecycle.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $state = Get-LocalState
    if (!$state) { Write-Host 'The local run was already stopped.'; return }
    Stop-LocalOwnedRun $state
    Remove-Item -LiteralPath (Assert-LocalPath (Join-Path $localRoot 'run.json')) -ErrorAction Stop
} finally { if ($lock) { $lock.Dispose() } }
Write-Host 'Local host and Azurite stopped. Data, tools, and logs retained.'
