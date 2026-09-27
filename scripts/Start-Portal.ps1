#requires -Version 7.4
[CmdletBinding()]
param([switch]$NoBrowser)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$project=Join-Path $root 'src/SelfService.Portal'
$dll=Join-Path $project 'bin/Release/net10.0/SelfService.Portal.dll'
if(!(Test-Path -LiteralPath $dll)){throw 'Run ./scripts/Setup-Portal.ps1 first.'}
$state=Join-Path $root '.local/portal';New-Item -ItemType Directory -Path $state -Force|Out-Null
$receipt=Join-Path $state 'process.json'
if(Test-Path -LiteralPath $receipt){throw 'A portal ownership receipt exists. Run Stop-Portal.ps1 before starting another instance.'}
$port=5087
$config=Join-Path $project 'portal.local.json'
if(Test-Path -LiteralPath $config){$settings=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json;if($settings.Portal.Port){$port=[int]$settings.Portal.Port}}
if($env:STUDIO_Portal__Port){$port=[int]$env:STUDIO_Portal__Port}
if($port -lt 1024 -or $port -gt 65535){throw 'Invalid portal port.'}
$url="http://localhost:$port"
# Never adopt an unrelated listener on the chosen port.
if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw "Port $port is already occupied."}
$exe=(Get-Command dotnet -ErrorAction Stop).Source
$p=Start-Process -FilePath $exe -ArgumentList @('"'+$dll+'"') -WorkingDirectory $project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $state 'stdout.log') -RedirectStandardError (Join-Path $state 'stderr.log')
@{pid=$p.Id;startedUtc=$p.StartTime.ToUniversalTime().ToString('O');executable=$exe;assembly=$dll;url=$url}|ConvertTo-Json|Set-Content -LiteralPath $receipt
for($i=0;$i -lt 40;$i++){
    $p.Refresh();if($p.HasExited){throw 'Portal exited. See .local/portal/stderr.log, then run Stop-Portal.ps1 to clear the stale receipt.'}
    try {$health=Invoke-RestMethod "$url/api/bootstrap" -TimeoutSec 1;if($health.products.Count -eq 2){if(!$NoBrowser){Start-Process $url};Write-Host "Portal ready: $url ($($health.architecture)). Stop with ./scripts/Stop-Portal.ps1.";return}}catch{}
    Start-Sleep -Milliseconds 250
}
throw 'Portal did not become ready. Inspect .local/portal logs and run Stop-Portal.ps1.'
