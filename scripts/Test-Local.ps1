#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param()
. "$PSScriptRoot/local-common.ps1"
$state = Get-LocalState
if (!$state -or !$state.ready) { throw 'Run scripts/Run-Local.ps1 first.' }
foreach ($record in $state.processes) { if (!(Get-OwnedProcess $record)) { throw 'A recorded local process is no longer running.' } }
if (!(Get-LocalStatus).Healthy) { throw 'The local runtime ownership/readiness check failed.' }
if (!$PSCmdlet.ShouldProcess('Owned Azurite profile','Upload local smoke-test objects and verify their transfer')) { return }
$savedEnvironment=@{}
foreach($name in @('PATH','DOTNET_ROOT','DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT')) { $savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name) }
try {
$tools = Get-LocalTools -ConfigureEnvironment
$evidence = Join-Path $state.logs ('smoke-' + [guid]::NewGuid().ToString('N') + '.json')
$operator = if ($state.PSObject.Properties['operator']) { Assert-LocalPath $state.operator } else { Assert-LocalPath (Join-Path $localRoot 'operator') }
$process = Start-Process -FilePath $tools.dotnet -ArgumentList @(('"'+(Join-Path $operator 'TransferTool.dll')+'"'),'local-smoke','--evidence',('"'+$evidence+'"')) -Environment (Get-LocalChildEnvironment -Telemetry) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $state.logs 'smoke.log') -RedirectStandardError (Join-Path $state.logs 'smoke-error.log')
try {
    $processDeadline=[DateTimeOffset]::UtcNow.AddMinutes(5)
    while (!$process.WaitForExit(1000)) { if ([DateTimeOffset]::UtcNow -gt $processDeadline) { throw 'Local transfer smoke test timed out.' } }
    if ($process.ExitCode -ne 0) { throw 'Local transfer smoke test failed. Its diagnostics were retained.' }
} finally {
    try { if (!$process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(10000) } } catch { Write-Warning 'The local smoke process could not be fully cleaned up.' }
    $process.Dispose()
}
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
} finally {
    foreach($name in $savedEnvironment.Keys) {
        if ($null -eq $savedEnvironment[$name]) { if (Test-Path -LiteralPath "Env:$name") { Remove-Item -LiteralPath "Env:$name" } }
        else { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    }
}
