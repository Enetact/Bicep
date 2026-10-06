#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/portal-lifecycle/'+[guid]::NewGuid().ToString('N'))
if(Get-Variable PortalLifecycleFixture -Scope Global -ErrorAction SilentlyContinue){throw 'Test fixture variable already exists.'}
$savedPort=$env:STUDIO_Portal__Port
$global:PortalLifecycleFixture=@{Root=$fixture;Running=$false;Foreign=$false;Denied=$false;Mismatch=$false;Health=$true;ProductCount=2;FailBuild=$false;Calls=[Collections.Generic.List[string]]::new();Start=[datetime]::UtcNow}
function Assert-True($condition,$message){if(!$condition){throw $message}}
function Assert-Rejected([scriptblock]$action){$rejected=$false;try{& $action|Out-Null}catch{$rejected=$true};Assert-True $rejected 'Expected lifecycle rejection.'}
function Get-NetTCPConnection {
    [CmdletBinding()]param($State)
    $h=$global:PortalLifecycleFixture
    if($h.Denied){throw 'Synthetic listener access denied'}
    if($h.Foreign){[pscustomobject]@{LocalPort=5087;OwningProcess=8765}}
    elseif($h.Running){[pscustomobject]@{LocalPort=5087;OwningProcess=4321}}
}
function Get-Process {
    [CmdletBinding()]param($Id)
    $h=$global:PortalLifecycleFixture
    if($h.Running){[pscustomobject]@{Id=4321;Path='C:\fixture\dotnet.exe';StartTime=if($h.Mismatch){$h.Start.AddMinutes(1)}else{$h.Start}}}
}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    if($global:PortalLifecycleFixture.HiddenMetadata){return [pscustomobject]@{CommandLine=$null}}
    [pscustomobject]@{CommandLine='dotnet "'+(Join-Path $global:PortalLifecycleFixture.Root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll')+'"'}
}
function Invoke-RestMethod {
    [CmdletBinding()]param($Uri,$TimeoutSec,$MaximumRedirection)
    if(!$global:PortalLifecycleFixture.Health){throw 'Synthetic bootstrap failure'}
    [pscustomobject]@{products=@(1..$global:PortalLifecycleFixture.ProductCount | ForEach-Object {"fixture-$_"})}
}
try {
    $env:STUDIO_Portal__Port='5087'
    foreach($dir in @('scripts','config','.local/portal')){New-Item -ItemType Directory -Path (Join-Path $fixture $dir) -Force|Out-Null}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Manage-Portal.ps1') -Destination (Join-Path $fixture 'scripts/Manage-Portal.ps1')
    # Production uses a keyed object, not an array. PSCustomObject.Count is 1.
    '{"schemaVersion":1,"workloads":{"fixture-a":{},"fixture-b":{}}}'|Set-Content (Join-Path $fixture 'config/workloads.json')
    @'
param([switch]$InstallMissing)
$h=$global:PortalLifecycleFixture
$h.Calls.Add('Build')
if($h.FailBuild){throw 'Synthetic build failure'}
'@|Set-Content (Join-Path $fixture 'scripts/Setup-Portal.ps1')
    @'
param([switch]$NoBrowser)
$h=$global:PortalLifecycleFixture
$h.Calls.Add('Start');$h.Running=$true
@{pid=4321;executable='C:\fixture\dotnet.exe';assembly=(Join-Path $h.Root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll');startedUtc=$h.Start.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $h.Root '.local/portal/process.json')
'@|Set-Content (Join-Path $fixture 'scripts/Start-Portal.ps1')
    @'
$h=$global:PortalLifecycleFixture
$h.Calls.Add('Stop')
if($h.StopNoop){return}
$h.Running=$false
$path=Join-Path $h.Root '.local/portal/process.json'
if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
'@|Set-Content (Join-Path $fixture 'scripts/Stop-Portal.ps1')
    $entry=Join-Path $fixture 'scripts/Manage-Portal.ps1'
    $h=$global:PortalLifecycleFixture
    Assert-True ((& $entry).State -eq 'Stopped') 'Default must inspect only.'
    & $entry -Action Restart -WhatIf
    Assert-True ($h.Calls.Count -eq 0) 'WhatIf invoked a lifecycle operation.'
    $result=& $entry -Action Restart -NoBrowser
    Assert-True ($result.State -eq 'Ready' -and ($h.Calls -join ',') -eq 'Stop,Build,Start') 'Restart order/readiness failed.'
    $h.Calls.Clear()
    $h.ProductCount=1
    $mismatched=& $entry -Action Status
    Assert-True ($mismatched.State -eq 'Unhealthy' -and $mismatched.Detail -match 'expected 2, received 1' -and $h.Calls.Count -eq 0) 'A genuinely incomplete catalog must remain unhealthy with counts.'
    $h.ProductCount=2
    $h.HiddenMetadata=$true
    Assert-True ((& $entry -Action Status).State -eq 'Unknown') 'Unreadable metadata was mislabeled as a proven ownership mismatch.'
    Assert-Rejected {& $entry -Action Restart}
    Assert-True ($h.Calls.Count -eq 0) 'Unreadable metadata permitted mutation.'
    $h.HiddenMetadata=$false
    Assert-Rejected {& $entry -Action Start}
    Assert-True ($h.Calls.Count -eq 0) 'Start touched a running portal.'
    $h.Mismatch=$true
    Assert-Rejected {& $entry -Action Restart}
    Assert-True ($h.Calls.Count -eq 0) 'PID reuse was not rejected before mutation.'
    $h.Mismatch=$false;$h.Denied=$true
    Assert-True ((& $entry -Action Status).State -eq 'Unknown') 'Denied inspection reported a stopped service.'
    Assert-Rejected {& $entry -Action Restart}
    Assert-True ($h.Calls.Count -eq 0) 'Denied inspection permitted mutation.'
    $h.Denied=$false;$h.Foreign=$true
    Assert-Rejected {& $entry -Action Restart}
    Assert-True ($h.Calls.Count -eq 0) 'Foreign listener permitted mutation.'
    $h.Foreign=$false;$h.Health=$false
    Assert-True ((& $entry -Action Status).State -eq 'Unhealthy') 'Bootstrap failure reported ready.'
    $h.Health=$true;$h.FailBuild=$true
    Assert-Rejected {& $entry -Action Restart}
    Assert-True (($h.Calls -join ',') -eq 'Stop,Build' -and !$h.Running) 'Failed rebuild started a portal.'
    $h.Calls.Clear();$h.FailBuild=$false
    $result=& $entry -Action Start -NoBrowser
    Assert-True ($result.Healthy -and ($h.Calls -join ',') -eq 'Build,Start') 'Cold start failed.'
    $h.Calls.Clear()
    $result=& $entry -Action Stop
    Assert-True ($result.State -eq 'Stopped' -and ($h.Calls -join ',') -eq 'Stop') 'Stop unexpectedly built or started.'
    & (Join-Path $fixture 'scripts/Start-Portal.ps1');$h.Running=$false;$h.Calls.Clear()
    Assert-True ((& $entry).State -eq 'StaleReceipt') 'Dead recorded process was not distinguished.'
    $result=& $entry -Action Restart -NoBrowser
    Assert-True ($result.Healthy -and ($h.Calls -join ',') -eq 'Stop,Build,Start') 'Stale receipt restart failed.'
    $h.Calls.Clear();$h.StopNoop=$true
    Assert-Rejected {& $entry -Action Restart}
    Assert-True (($h.Calls -join ',') -eq 'Stop') 'Unsuccessful stop permitted a rebuild.'
    $h.StopNoop=$false;$h.Calls.Clear();$h.Health=$false
    Assert-Rejected {& $entry -Action Restart -NoBrowser}
    Assert-True (($h.Calls -join ',') -eq 'Stop,Build,Start') 'Unhealthy started app was not rejected after startup.'
    [ordered]@{passed=16;scope='Synthetic lifecycle orchestration with production-shaped workload dictionary; no real services stopped/built/started';date=[datetimeoffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $fixture 'receipt.json')
    Write-Host "PASS: 16 portal lifecycle scenarios. Receipt: $fixture/receipt.json"
} finally {
    $env:STUDIO_Portal__Port=$savedPort
    Remove-Variable PortalLifecycleFixture -Scope Global
}
