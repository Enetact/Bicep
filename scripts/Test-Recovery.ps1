[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
foreach ($port in @(10000,10001,10002)) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        throw "Port $port is already in use. This test refuses to use or stop an existing storage service."
    }
}
$tools = Join-Path $root 'artifacts/test-tools'
& npm.cmd install --prefix $tools azurite@3.37.0 --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Azurite installation failed.' }
$run = Join-Path $root ("artifacts/recovery-tests/" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$entry = Join-Path $tools 'node_modules/azurite/dist/src/azurite.js'
$process = Start-Process -FilePath (Get-Command node).Source -ArgumentList @(
    "`"$entry`"", '--silent', '--skipApiVersionCheck', '--disableTelemetry',
    '--blobHost','127.0.0.1','--queueHost','127.0.0.1','--tableHost','127.0.0.1',
    '--location',"`"$run`""
) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'azurite.log') -RedirectStandardError (Join-Path $run 'azurite-error.log')
$previous = $env:BLOBTRANSFER_AZURITE
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 240; $attempt++) {
        if ($process.HasExited) { throw 'Azurite exited before startup.' }
        $listeners = @(Get-NetTCPConnection -LocalPort 10000,10001,10002 -State Listen -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 3 -and @($listeners | Where-Object OwningProcess -ne $process.Id).Count -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Azurite did not start on the expected loopback ports.' }
    $env:BLOBTRANSFER_AZURITE = '1'
    & dotnet test (Join-Path $root 'tests/BlobTransfer.Tests/BlobTransfer.Tests.csproj') -c Release -m:1 -p:RestoreLockedMode=true --logger 'trx;LogFileName=recovery.trx' --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw 'Recovery tests failed.' }
} finally {
    $env:BLOBTRANSFER_AZURITE = $previous
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
Write-Host "Local recovery test evidence: $run"
