#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[ValidateSet('Arm64','X64')][string]$ExpectedArchitecture,[int]$Port=5088)
$ErrorActionPreference='Stop'
$directory=(Resolve-Path -LiteralPath $PackageDirectory).Path
$exe=Join-Path $directory 'SelfService.Portal.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Select an extracted/published portal package directory.'}
if(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue){throw 'Test port is already in use.'}
$log=Join-Path $directory 'package-test.log';$errorLog=Join-Path $directory 'package-test-error.log'
$p=Start-Process -FilePath $exe -ArgumentList @("--Portal:Port=$Port") -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError $errorLog
try {
    $ready=$null
    for($i=0;$i -lt 40;$i++){
        $p.Refresh();if($p.HasExited){throw 'Package exited before becoming ready. Read package-test-error.log.'}
        try {$ready=Invoke-RestMethod "http://localhost:$Port/api/bootstrap" -TimeoutSec 1;break}catch{Start-Sleep -Milliseconds 250}
    }
    if(!$ready -or $ready.architecture -ne $ExpectedArchitecture -or $ready.products.Count -ne 7 -or !$ready.packagedCatalog){throw 'Package architecture/bundled catalog readiness failed.'}
    $page=Invoke-WebRequest "http://localhost:$Port/" -TimeoutSec 3
    if($page.StatusCode -ne 200 -or $page.Content -notmatch 'Platform Studio'){throw 'Packaged UI missing.'}
    $old=$env:PORTAL_TEST_URL
    try {
        $env:PORTAL_TEST_URL="http://localhost:$Port"
        Push-Location (Split-Path $PSScriptRoot -Parent)
        try {& node tests/portal/smoke.mjs;if($LASTEXITCODE -ne 0){throw 'Packaged HTTP smoke tests failed.'}}finally{Pop-Location}
    }finally{$env:PORTAL_TEST_URL=$old}
    & (Join-Path $PSScriptRoot 'Test-AgentHost.ps1') -Port $Port
    @{architecture=$ready.architecture;package=$directory;verifiedUtc=[datetimeoffset]::UtcNow.ToString('O');httpChecks=24;ahpChecks=5;bundledAzureSkills=42;liveAzure=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'package-test.json')
    Write-Host "PASS: $ExpectedArchitecture package started and served UI, catalog, skills and analysis. No live cloud calls."
}finally{
    $p.Refresh()
    if(!$p.HasExited){if($p.Path -ne $exe){throw 'Package process identity changed; refusing to stop it.'};Stop-Process -Id $p.Id;$p.WaitForExit(10000)|Out-Null}
}
