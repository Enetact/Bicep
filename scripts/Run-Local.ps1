[CmdletBinding()]
param()
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    & "$PSScriptRoot/Setup-Local.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Prerequisite bootstrap failed.' }
    $tools = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) '.local/tools.json') -Raw | ConvertFrom-Json
    & $tools.powershell -NoProfile -File $PSCommandPath
    exit $LASTEXITCODE
}
. "$PSScriptRoot/local-common.ps1"
if (!$IsWindows) { throw 'Local lifecycle currently supports Windows only.' }
$state = Get-LocalState
if ($state) {
    foreach ($record in $state.processes) {
        if (Get-OwnedProcess $record) { throw 'A local run is already active. Use Test-Local.ps1 or Stop-Local.ps1.' }
    }
}
Assert-LocalPortsFree
& "$PSScriptRoot/Setup-Local.ps1"
$tools = Get-LocalTools
$root = Get-ProjectRoot
$app = Assert-LocalPath (Join-Path $localRoot 'app')
$operator = Assert-LocalPath (Join-Path $localRoot 'operator')
$data = Assert-LocalPath (Join-Path $localRoot 'data')
$logs = Assert-LocalPath (Join-Path $localRoot ('logs/' + [guid]::NewGuid().ToString('N')))
foreach ($path in @($app,$operator,$data,$logs)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
& $tools.dotnet publish (Join-Path $root 'src/BlobTransfer/BlobTransfer.csproj') -c Release -m:1 -p:RestoreLockedMode=true --no-self-contained -o $app
if ($LASTEXITCODE -ne 0) { throw 'Local Function build failed.' }
Test-FunctionMetadata (Join-Path $app 'functions.metadata')
& $tools.dotnet publish (Join-Path $root 'src/TransferTool/TransferTool.csproj') -c Release -m:1 -p:RestoreLockedMode=true --no-self-contained -o $operator
if ($LASTEXITCODE -ne 0) { throw 'Local operator build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src/BlobTransfer/local.settings.azurite.example.json') -Destination (Join-Path $app 'local.settings.json') -Force
$receipt = @{ root=[IO.Path]::GetFullPath($root); startedUtc=[DateTimeOffset]::UtcNow.ToString('O'); processes=@(); logs=$logs; app=$app; ready=$false }
$statePath = Join-Path $localRoot 'run.json'
try {
    $azurite = Start-Process -FilePath $tools.node -ArgumentList @("`"$($tools.azurite)`"",'--silent','--skipApiVersionCheck','--disableTelemetry','--blobHost','127.0.0.1','--queueHost','127.0.0.1','--tableHost','127.0.0.1','--location',"`"$data`"") -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'azurite.log') -RedirectStandardError (Join-Path $logs 'azurite-error.log')
    $receipt.processes += New-ProcessRecord $azurite
    $receipt | ConvertTo-Json -Depth 5 | Set-Content $statePath
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        if ($azurite.HasExited) { throw "Azurite exited. See $logs" }
        $listeners = @(Get-NetTCPConnection -LocalPort 10000,10001,10002 -State Listen -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 3 -and @($listeners | Where-Object OwningProcess -ne $azurite.Id).Count -eq 0) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Azurite startup timed out.' }
        Start-Sleep -Milliseconds 500
    } while ($true)
    & $tools.dotnet (Join-Path $operator 'TransferTool.dll') local-seed
    if ($LASTEXITCODE -ne 0) { throw 'Local storage initialization failed.' }
    # Clear inherited cloud connection settings before starting the host.
    $environment = @{}
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) {
        if ($key -match '^(AzureWebJobsStorage|UploadStorage|TransferQueueStorage|Ledger__|Destination__|WEBSITE_|LocalDevelopment__)') { $environment[$key] = $null }
    }
    $environment.AZURE_FUNCTIONS_ENVIRONMENT = 'Development'
    $environment.FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT = '1'
    $environment.ASPNETCORE_URLS = 'http://127.0.0.1:7071'
    $hostProcess = Start-Process -FilePath $tools.func -ArgumentList @('start','--port','7071','--no-build') -WorkingDirectory $app -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'functions.log') -RedirectStandardError (Join-Path $logs 'functions-error.log')
    $receipt.processes += New-ProcessRecord $hostProcess
    $receipt | ConvertTo-Json -Depth 5 | Set-Content $statePath
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    do {
        if ($hostProcess.HasExited) { throw "Functions host exited. See $logs" }
        try {
            $status = Invoke-RestMethod 'http://127.0.0.1:7071/admin/host/status' -TimeoutSec 2
            if ($status.state -eq 'Running') { break }
        } catch { }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "Functions host startup timed out. See $logs" }
        Start-Sleep -Seconds 1
    } while ($true)
    $receipt.ready = $true
    $receipt | ConvertTo-Json -Depth 5 | Set-Content $statePath
    Write-Host "Local Functions host ready at http://localhost:7071. Logs: $logs"
    Write-Host 'Run ./scripts/Test-Local.ps1 to verify uploads; ./scripts/Stop-Local.ps1 stops this run and preserves data.'
} catch {
    & "$PSScriptRoot/Stop-Local.ps1"
    throw
}
