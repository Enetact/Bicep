#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/local-common.ps1"
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw 'Expected unsafe lifecycle input to be rejected.' }
}
$safe = Join-Path $localRoot 'data'
if ((Assert-LocalPath $safe) -ne [IO.Path]::GetFullPath($safe)) { throw 'Valid local path was rejected.' }
Assert-Rejected { Assert-LocalPath (Join-Path $localRoot '../src') }
Assert-Rejected { Assert-LocalPath ($localRoot + '-another/data') }
$current = Get-Process -Id $PID
$record = New-ProcessRecord $current
if ((Get-OwnedProcess $record).Id -ne $PID) { throw 'Owned process lookup failed.' }
$record.startTicks = '0'
Assert-Rejected { Get-OwnedProcess $record }
$record = New-ProcessRecord $current
$record.path = 'C:\not-the-recorded-process.exe'
Assert-Rejected { Get-OwnedProcess $record }
Write-Host 'PASS: 6 local lifecycle path/process ownership checks. No process was stopped and no data was removed.'
