#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/local-common.ps1"
$tools = Get-LocalTools
$state = Get-LocalState
if (!$state -or !$state.ready) { throw 'Run scripts/Run-Local.ps1 first.' }
foreach ($record in $state.processes) { if (!(Get-OwnedProcess $record)) { throw 'A recorded local process is no longer running.' } }
$evidence = Join-Path $state.logs ('smoke-' + [guid]::NewGuid().ToString('N') + '.json')
& $tools.dotnet (Join-Path $localRoot 'operator/TransferTool.dll') local-smoke --evidence $evidence
if ($LASTEXITCODE -ne 0) { throw "Local transfer smoke test failed. See $($state.logs)" }
$smoke = Get-Content $evidence -Raw | ConvertFrom-Json
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
do {
    $log = Get-Content (Join-Path $state.logs 'functions.log') -Raw
    $timers = @('ReconcileHeartbeat','LedgerAuditHeartbeat','PoisonMonitorHeartbeat')
    $missing = @($timers | Where-Object { !$log.Contains($_) })
    $missing += @($smoke.requestIds | Where-Object { !$log.Contains("BlobDispatched request=$_") })
    if (!$missing.Count) { break }
    if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Transfers completed, but dispatcher/timer execution evidence is incomplete. Inspect the Functions logs.' }
    Start-Sleep -Seconds 1
} while ($true)
Write-Host "PASS: actual BlobTrigger dispatch, QueueTrigger transfer, and all three timer heartbeats observed. Evidence: $evidence"
