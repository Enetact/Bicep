[CmdletBinding(SupportsShouldProcess)]
param([string]$PreparedBuild='', [switch]$SkipTelemetry, [switch]$InstallMissing)
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    if ($WhatIfPreference) { Write-Host 'What if: bootstrap PowerShell 7.4 and start the local runtime.'; return }
    & "$PSScriptRoot/Setup-Local.ps1" -CheckOnly:(!$InstallMissing) -WriteReceipt
    $tools = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) '.local/tools.json') -Raw | ConvertFrom-Json
    $forward = @('-NoProfile','-File',$PSCommandPath)
    if ($PreparedBuild) { $forward += @('-PreparedBuild',$PreparedBuild) }
    if ($SkipTelemetry) { $forward += '-SkipTelemetry' }
    if ($InstallMissing) { $forward += '-InstallMissing' }
    & $tools.powershell @forward
    if ($LASTEXITCODE -ne 0) { throw 'Local runtime startup failed.' }
    return
}
. "$PSScriptRoot/local-common.ps1"
if (!$IsWindows) { throw 'Local lifecycle currently supports Windows only.' }
$before = Get-LocalStatus
if ($before.State -eq 'Ready') { Write-Host 'The owned local Functions and Azurite run is already ready.'; $before; return }
if ($before.State -notin @('Stopped','StaleReceipt')) { throw 'Local runtime is active or its ownership is unverified. Inspect Get-Local.ps1 before changing it.' }
if ($PreparedBuild) { $null = Get-LocalPreparedBuild $PreparedBuild }
if (!$PSCmdlet.ShouldProcess('Local Functions and Azurite','Build/use an immutable local profile, start telemetry first, then initialize local storage and start the worker')) { return }
$lock = $null; $runtimeCreated = $false; $runtimeStopped = $false; $collectorNew = $false; $collectorIdentity = $null
$statePath = Assert-LocalPath (Join-Path $localRoot 'run.json')
$exporterKeys = @('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS',
    'PATH','DOTNET_ROOT','DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT')
$savedExporter = @{}; foreach ($key in $exporterKeys) { $savedExporter[$key]=[Environment]::GetEnvironmentVariable($key) }
try {
    $null = New-Item -ItemType Directory -Path (Assert-LocalPath $localRoot) -Force
    $lock = [IO.File]::Open((Assert-LocalPath (Join-Path $localRoot 'local-lifecycle.lock')),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $current = Get-LocalStatus
    if ($current.State -notin @('Stopped','StaleReceipt')) { throw 'Local runtime changed before startup; no process was started.' }
    Assert-LocalPortsFree
    & "$PSScriptRoot/Setup-Local.ps1" -CheckOnly:(!$InstallMissing) -WriteReceipt | Out-Null
    $tools = Get-LocalTools -ConfigureEnvironment
    $root = Get-ProjectRoot
    if (!$PreparedBuild) {
        $PreparedBuild = Assert-LocalPath (Join-Path $localRoot ('builds/'+[guid]::NewGuid().ToString('N')))
        $null = New-Item -ItemType Directory -Path $PreparedBuild
        Push-Location $root
        try {
            foreach ($entry in @(@('BlobTransfer','blob-transfer'),@('TransferTool','transfer-tool'))) {
                & $tools.dotnet publish (Join-Path $root "src/$($entry[0])/$($entry[0]).csproj") -c Release -m:1 -p:RestoreLockedMode=true --artifacts-path (Join-Path $PreparedBuild '.artifacts') --no-self-contained -o (Join-Path $PreparedBuild $entry[1])
                if ($LASTEXITCODE -ne 0) { throw 'Local runtime publish failed. Existing builds and data were preserved.' }
            }
        } finally { Pop-Location }
        Write-LocalBuildReceipt -BuildRoot $PreparedBuild
    }
    $prepared = Get-LocalPreparedBuild -BuildRoot $PreparedBuild
    $runId = [guid]::NewGuid().ToString('N')
    $run = Assert-LocalPath (Join-Path $localRoot ('runtime/'+$runId))
    $app = Assert-LocalPath (Join-Path $run 'app')
    $data = Assert-LocalPath (Join-Path $localRoot 'data')
    $logs = Assert-LocalPath (Join-Path $localRoot ('logs/'+$runId))
    foreach ($directory in @($run,$data,$logs)) { $null=New-Item -ItemType Directory -Path $directory -Force }
    Copy-Item -LiteralPath $prepared.App -Destination $app -Recurse
    # The immutable publish bundle stays untouched. Only this generated run receives the checked local-only profile.
    $settingsPath = Join-Path $root 'src/BlobTransfer/local.settings.azurite.example.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in @('AzureWebJobsStorage','UploadStorage','TransferQueueStorage')) {
        if ($settings['Values'][$name] -cne 'UseDevelopmentStorage=true') { throw 'The local runtime profile must use only Azurite storage.' }
    }
    if ($settings['Values']['LocalDevelopment__Enabled'] -cne 'true' -or $settings['Values']['FUNCTIONS_WORKER_RUNTIME'] -cne 'dotnet-isolated') { throw 'The local-only isolated worker profile is required.' }
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $app 'local.settings.json')
    $null = Get-LocalPreparedBuild -BuildRoot $PreparedBuild
    if (!$SkipTelemetry) {
        $collectorStart = & "$PSScriptRoot/Start-Observability.ps1" -NoBrowser -InstallMissing:$InstallMissing
        $collectorNew = $collectorStart.StartedByInvocation -eq $true
        $collectorIdentity = $collectorStart.Identity
        & "$PSScriptRoot/Use-Observability.ps1"
    }
    $receipt = @{schema='platform.local-run/v2';runId=$runId;root=[IO.Path]::GetFullPath($root);startedUtc=[DateTimeOffset]::UtcNow.ToString('O');
        processes=@();logs=$logs;app=$app;operator=$prepared.Operator;build=$prepared.Root;ready=$false}
    if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -ErrorAction Stop }
    $azurite = Start-Process -FilePath $tools.node -ArgumentList @(('"'+$tools.azurite+'"'),'--silent','--skipApiVersionCheck','--disableTelemetry','--blobHost','127.0.0.1','--queueHost','127.0.0.1','--tableHost','127.0.0.1','--location',('"'+$data+'"')) -Environment (Get-LocalChildEnvironment) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'azurite.log') -RedirectStandardError (Join-Path $logs 'azurite-error.log')
    $runtimeCreated = $true
    $receipt.processes += New-ProcessRecord $azurite
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $azurite.Refresh(); if ($azurite.HasExited) { throw 'Azurite exited before readiness. Its diagnostics were retained.' }
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in @(10000,10001,10002))
        if (@($listeners | Where-Object { $_.OwningProcess -ne $azurite.Id -or $_.LocalAddress -ne '127.0.0.1' }).Count) { throw 'Azurite listeners are not exclusively owned loopback endpoints.' }
        if ($listeners.Count -eq 3) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Azurite startup timed out.' }
        Start-Sleep -Milliseconds 500
    } while ($true)
    # This command creates only the known Azurite containers and queues; it never uploads sample work or invokes a cloud credential.
    $seed = Start-Process -FilePath $tools.dotnet -ArgumentList @(('"'+(Join-Path $prepared.Operator 'TransferTool.dll')+'"'),'local-seed') -Environment (Get-LocalChildEnvironment -Telemetry) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'seed.log') -RedirectStandardError (Join-Path $logs 'seed-error.log')
    try {
        $seedDeadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
        while (!$seed.WaitForExit(1000)) { if ([DateTimeOffset]::UtcNow -gt $seedDeadline) { throw 'Local storage initialization timed out.' } }
        if ($seed.ExitCode -ne 0) { throw 'Local storage initialization failed. Diagnostics were retained.' }
    } finally {
        try { if (!$seed.HasExited) { $seed.Kill($true); $null=$seed.WaitForExit(10000) } } catch { Write-Warning 'The local initialization process could not be fully cleaned up.' }
        $seed.Dispose()
    }
    $environment = Get-LocalChildEnvironment -Telemetry
    $environment.AZURE_FUNCTIONS_ENVIRONMENT='Development'
    $environment.ASPNETCORE_URLS='http://127.0.0.1:7071'
    $hostProcess = Start-Process -FilePath $tools.func -ArgumentList @('start','--address','127.0.0.1','--port','7071','--no-build') -WorkingDirectory $app -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'functions.log') -RedirectStandardError (Join-Path $logs 'functions-error.log')
    $receipt.processes += New-ProcessRecord $hostProcess
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    do {
        $hostProcess.Refresh(); if ($hostProcess.HasExited) { throw 'Functions host exited before readiness. Its diagnostics were retained.' }
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq 7071)
        if (@($listeners | Where-Object { $_.OwningProcess -ne $hostProcess.Id -or $_.LocalAddress -ne '127.0.0.1' }).Count) { throw 'The Functions listener is not an owned loopback endpoint.' }
        $healthy=$false
        try { $healthy=(Invoke-RestMethod 'http://127.0.0.1:7071/admin/host/status' -TimeoutSec 2 -MaximumRedirection 0).state -eq 'Running' } catch { }
        if ($healthy -and $listeners.Count -eq 1) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Functions host startup timed out. Its diagnostics were retained.' }
        Start-Sleep -Milliseconds 500
    } while ($true)
    $receipt.ready=$true
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath
    $status=Get-LocalStatus
    if (!$status.Healthy) { throw 'Local runtime failed the final ownership/readiness check.' }
    Write-Host 'Local Functions and Azurite are ready. Emulator data, previous builds and logs were preserved. No sample transfer was submitted.'
    $status
} catch {
    $startupFailure=$_
    if ($runtimeCreated) {
        try {
            $registered=$null; $readable=$true
            try { $registered=Get-LocalState } catch { $readable=$false }
            if ($registered -and $registered.runId -cne $receipt.runId) { throw 'Local run registration changed; rollback refused.' }
            # Newly created roots remain identifiable even when persisting run.json was the failing step.
            # Use the exact in-memory PID/start/path inventory; preserve unreadable registration for review.
            Stop-LocalOwnedRun $receipt
            if ($readable -and $registered) { Remove-Item -LiteralPath $statePath -ErrorAction Stop }
            elseif (Test-Path -LiteralPath $statePath) { Write-Warning 'An incomplete local run receipt was retained for inspection after stopping this invocation''s owned processes.' }
            $runtimeStopped=$true
        } catch { Write-Warning 'Local runtime rollback could not be fully verified. Its ownership receipt, data and diagnostics were retained.' }
    } else { $runtimeStopped=$true }
    if (!$SkipTelemetry -and $runtimeStopped -and $collectorNew) {
        try {
            # Another producer can reuse the collector, or its owned generation can change, during startup.
            # Retain it unless both producer absence and this invocation's exact generation remain verified.
            $portal = & "$PSScriptRoot/Get-Portal.ps1"
            $collector = & "$PSScriptRoot/Get-Observability.ps1"
            if ($portal.State -ceq 'Stopped' -and $collector.OwnershipVerified -eq $true -and
                $collector.State -in @('Running','Unhealthy') -and ![string]::IsNullOrWhiteSpace($collectorIdentity) -and
                $collector.Identity -ceq $collectorIdentity) {
                & "$PSScriptRoot/Stop-Observability.ps1" -Confirm:$false
            } else { Write-Warning 'Telemetry was retained because another producer or a different collector generation may be active.' }
        }
        catch { Write-Warning 'The new telemetry process was retained because cleanup could not be verified.' }
    }
    throw $startupFailure
} finally {
    try {
        foreach ($key in $exporterKeys) {
            if ($null -eq $savedExporter[$key]) { if (Test-Path -LiteralPath "Env:$key") { Remove-Item -LiteralPath "Env:$key" } }
            else { [Environment]::SetEnvironmentVariable($key,$savedExporter[$key],'Process') }
        }
    } finally { if ($lock) { $lock.Dispose() } }
}
