#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/local-common.ps1"
$state = Get-LocalState
if (!$state) { Write-Host 'No local run is recorded.'; return }
$records = @($state.processes)
[array]::Reverse($records)
foreach ($record in $records) {
    $process = Get-OwnedProcess $record
    if ($process) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        if ($LASTEXITCODE -ne 0 -and (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { throw "Could not stop owned process $($process.Id)." }
    }
}
Remove-Item -LiteralPath (Join-Path $localRoot 'run.json')
Write-Host 'Local host and Azurite stopped. Data, tools, and logs retained.'
