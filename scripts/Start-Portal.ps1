#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([switch]$NoBrowser,[switch]$SkipTelemetry)
. (Join-Path $PSScriptRoot 'Suite.Common.ps1')
if (!$IsWindows) { throw 'Portal lifecycle currently supports Windows only.' }
$root = Get-SuiteRoot
$project = Join-Path $root 'src/SelfService.Portal'
$dll = Join-Path $project 'bin/Release/net10.0/SelfService.Portal.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Build the portal with Setup-Portal.ps1 or use Manage-Suite.ps1 -Action Start.' }
$receipt = Assert-SuitePath (Join-Path $root '.local/portal/process.json')
if (Test-Path -LiteralPath $receipt) { throw 'A portal ownership receipt exists. Use the preserving suite Restart.' }
$port = [int]([IO.Path]::GetFileName([IO.Path]::GetDirectoryName((Get-SuiteCheckpoint))))
$url = "http://localhost:$port"
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port)
if ($listeners.Count) { throw "Port $port is already occupied. No process was adopted or stopped." }
if (!$PSCmdlet.ShouldProcess($url,'Start telemetry first, then the owned portal')) { return }
$previousEnvironment = Get-SuiteTelemetryEnvironment
$startLock = $null
try {
    $state = New-SuitePrivateDirectory (Join-Path $root '.local/portal')
    $startLock = [IO.File]::Open((Join-Path $state 'start.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if ((Test-Path -LiteralPath $receipt) -or @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port).Count) { throw 'Portal ownership changed before startup. No process was started.' }
    if (!$SkipTelemetry) {
        $collector = & (Join-Path $PSScriptRoot 'Start-Observability.ps1') -NoBrowser -Confirm:$false
        if ($collector.State -ne 'Running' -or !$collector.Healthy) { throw 'The telemetry collector is not ready.' }
        & (Join-Path $PSScriptRoot 'Use-Observability.ps1')
    }
    $expected = (Get-Content -LiteralPath (Join-Path $root 'config/workloads.json') -Raw | ConvertFrom-Json -AsHashtable).workloads.Count
    $exe = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $process = Start-Process -FilePath $exe -ArgumentList @('"'+$dll+'"') -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $state 'stdout.log') -RedirectStandardError (Join-Path $state 'stderr.log')
    @{pid=$process.Id;startedUtc=$process.StartTime.ToUniversalTime().ToString('O');executable=$exe;assembly=$dll;url=$url} | ConvertTo-Json | Set-Content -LiteralPath $receipt
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw 'Portal exited. Its ownership receipt and private logs were retained for inspection.' }
        $health = $null
        try { $health = Invoke-RestMethod "$url/api/bootstrap" -TimeoutSec 2 -MaximumRedirection 0 } catch { }
        if ($health -and $health.products.Count -eq $expected) {
            $ownedListeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port)
            if (!$ownedListeners.Count -or @($ownedListeners | Where-Object OwningProcess -ne $process.Id).Count) { throw 'Portal listener ownership differs from its receipt.' }
            if (!$SkipTelemetry -and !$health.observability.dashboardUrl) { throw 'Portal started without its expected telemetry connector.' }
            if (!$NoBrowser) { Start-Process -FilePath $url }
            Write-Host "Portal ready: $url. Use Manage-Suite.ps1 for the preserving stop/build/start lifecycle."
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Portal did not become ready. Inspect its private logs and suite Status; its receipt was retained.'
}
catch { Write-Warning 'Portal startup did not establish readiness. Existing credentials, diagnostics and ownership receipts were preserved.'; throw }
finally {
    try { Restore-SuiteTelemetryEnvironment $previousEnvironment }
    finally { if ($null -ne $startLock) { $startLock.Dispose() } }
}
