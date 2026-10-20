#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/portal-lifecycle/'+[guid]::NewGuid().ToString('N'))
if(Get-Variable PortalLifecycleFixture -Scope Global -ErrorAction SilentlyContinue){throw 'Test fixture variable already exists.'}
$savedPort=$env:STUDIO_Portal__Port
$global:PortalLifecycleFixture=@{Root=$fixture;Running=$false;Foreign=$false;Denied=$false;Mismatch=$false;Health=$true;
    ProductCount=2;HiddenMetadata=$false;Telemetry=$true;FailDelegation=$false;Port=5087;
    Calls=[Collections.Generic.List[object]]::new();Start=[datetime]::UtcNow}
$passed=0
function Assert-PortalTest([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject-PortalTest([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Assert-PortalTest $rejected 'Expected lifecycle rejection.'}
function Get-NetTCPConnection {
    [CmdletBinding()]param($State)
    $h=$global:PortalLifecycleFixture
    if($h.Denied){throw 'Synthetic listener access denied.'}
    if($h.Foreign){[pscustomobject]@{LocalPort=$h.Port;OwningProcess=8765}}
    elseif($h.Running){[pscustomobject]@{LocalPort=$h.Port;OwningProcess=4321}}
}
function Get-Process {
    [CmdletBinding()]param($Id)
    $h=$global:PortalLifecycleFixture
    if($h.Running -and $Id -eq 4321){[pscustomobject]@{Id=4321;Path='C:\fixture\dotnet.exe';StartTime=if($h.Mismatch){$h.Start.AddMinutes(1)}else{$h.Start}}}
}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    if($global:PortalLifecycleFixture.HiddenMetadata){return [pscustomobject]@{CommandLine=$null}}
    [pscustomobject]@{CommandLine='dotnet "'+(Join-Path $global:PortalLifecycleFixture.Root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll')+'"'}
}
function Invoke-RestMethod {
    [CmdletBinding()]param($Uri,$TimeoutSec,$MaximumRedirection)
    $h=$global:PortalLifecycleFixture
    if(!$h.Health){throw 'Synthetic bootstrap failure.'}
    Assert-PortalTest ($Uri -eq "http://localhost:$($h.Port)/api/bootstrap") 'Status ignored the configured port.'
    [pscustomobject]@{products=@(1..$h.ProductCount|ForEach-Object {"fixture-$_"});observability=[pscustomobject]@{dashboardUrl=if($h.Telemetry){'http://127.0.0.1:18888'}else{$null}}}
}
function Write-PortalTestRegistration {
    $h=$global:PortalLifecycleFixture
    @{pid=4321;executable='C:\fixture\dotnet.exe';assembly=(Join-Path $h.Root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll');startedUtc=$h.Start.ToString('O')}|
        ConvertTo-Json|Set-Content -LiteralPath (Join-Path $h.Root '.local/portal/process.json')
}
try {
    $env:STUDIO_Portal__Port='5087'
    foreach($dir in @('scripts','config','.local/portal')){New-Item -ItemType Directory -Path (Join-Path $fixture $dir) -Force|Out-Null}
    foreach($name in @('Manage-Portal.ps1','Get-Portal.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture ('scripts/'+$name))}
    # A keyed dictionary is the production shape; PSCustomObject.Count would incorrectly report one product.
    '{"schemaVersion":1,"workloads":{"fixture-a":{},"fixture-b":{}}}'|Set-Content -LiteralPath (Join-Path $fixture 'config/workloads.json')
    @'
[CmdletBinding(SupportsShouldProcess)]
param([string]$Action='Status',[string]$Profile='Studio',[switch]$InstallMissing,[switch]$NoBrowser,[switch]$DiscardConnections,[switch]$ResumePending)
$h=$global:PortalLifecycleFixture
if(!$PSCmdlet.ShouldProcess($h.Root,"Synthetic $Action $Profile")){return}
$request=[pscustomobject]@{Action=$Action;Profile=$Profile;InstallMissing=[bool]$InstallMissing;NoBrowser=[bool]$NoBrowser;DiscardConnections=[bool]$DiscardConnections;ResumePending=[bool]$ResumePending}
$h.Calls.Add($request)
if($h.FailDelegation){throw 'Synthetic suite delegation failed.'}
[pscustomobject]@{State='Delegated';Request=$request}
'@|Set-Content -LiteralPath (Join-Path $fixture 'scripts/Manage-Suite.ps1')
    $entry=Join-Path $fixture 'scripts/Manage-Portal.ps1'
    $get=Join-Path $fixture 'scripts/Get-Portal.ps1'
    $h=$global:PortalLifecycleFixture
    Assert-PortalTest ((& $entry).State -eq 'Stopped' -and $h.Calls.Count -eq 0) 'Default must inspect only.';$passed++
    $h.Foreign=$true
    Assert-PortalTest ((& $get).State -eq 'UnownedListener' -and $h.Calls.Count -eq 0) 'An unowned listener was reported stopped.';$passed++
    $h.Foreign=$false;$h.Running=$true;Write-PortalTestRegistration
    $ready=& $get
    Assert-PortalTest ($ready.State -eq 'Ready' -and $ready.Healthy -and $ready.TelemetryConfigured -and $ready.OwnershipVerified -and $h.Calls.Count -eq 0) 'Owned portal readiness/telemetry projection failed.';$passed++
    $h.ProductCount=1
    $status=& $entry -Action Status
    Assert-PortalTest ($status.State -eq 'Unhealthy' -and $status.Detail -match 'expected 2, received 1' -and $h.Calls.Count -eq 0) 'Incomplete keyed catalog was not explained.';$passed++
    $h.ProductCount=2;$h.HiddenMetadata=$true
    Assert-PortalTest ((& $entry -Action Status).State -eq 'Unknown') 'Unreadable process metadata was not Unknown.';$passed++
    $h.HiddenMetadata=$false;$h.Mismatch=$true
    Assert-PortalTest ((& $entry -Action Status).State -eq 'OwnershipMismatch') 'PID/start-time mismatch was not detected.';$passed++
    $h.Mismatch=$false;$h.Denied=$true
    Assert-PortalTest ((& $entry -Action Status).State -eq 'Unknown') 'Denied listener inspection was treated as stopped.';$passed++
    $h.Denied=$false;$h.Foreign=$true
    Assert-PortalTest ((& $entry -Action Status).State -eq 'UnownedListener') 'A foreign listener on the selected port was adopted.';$passed++
    $h.Foreign=$false;$h.Health=$false
    Assert-PortalTest ((& $entry -Action Status).State -eq 'Unhealthy') 'Bootstrap failure was reported Ready.';$passed++
    $h.Health=$true;$h.Telemetry=$false
    Assert-PortalTest ((& $get).TelemetryConfigured -eq $false) 'A portal without telemetry was reported attached.';$passed++
    $h.Telemetry=$true;$h.Running=$false
    Assert-PortalTest ((& $get).State -eq 'StaleReceipt') 'Dead recorded process was not distinguished.';$passed++
    $h.Running=$true;$h.Port=5091;$env:STUDIO_Portal__Port='5091'
    Assert-PortalTest ((& $get).Url -eq 'http://localhost:5091') 'Environment-selected port was ignored.';$passed++
    $env:STUDIO_Portal__Port='99999'
    Reject-PortalTest {& $entry -Action Status};$passed++
    $h.Port=5087;$env:STUDIO_Portal__Port='5087'

    foreach($action in @('Build','Start','Stop','Restart')){
        $h.Calls.Clear()
        $result=& $entry -Action $action -NoBrowser
        Assert-PortalTest ($result.State -eq 'Delegated' -and $h.Calls.Count -eq 1 -and $result.Request.Action -eq $action -and $result.Request.Profile -eq 'Studio' -and $result.Request.NoBrowser -and !$result.Request.DiscardConnections) 'Compatibility wrapper failed preserving Studio delegation.'
        $passed++
    }
    $h.Calls.Clear()
    $request=(& $entry -Action Restart -InstallMissing -NoBrowser -DiscardConnections).Request
    Assert-PortalTest ($request.InstallMissing -and $request.NoBrowser -and $request.DiscardConnections -and !$request.ResumePending) 'Restart flags were not delegated exactly.';$passed++
    $h.Calls.Clear()
    $request=(& $entry -Action Start -ResumePending).Request
    Assert-PortalTest ($request.ResumePending -and !$request.DiscardConnections) 'ResumePending was not preserved by compatibility command.';$passed++
    $h.Calls.Clear()
    & $entry -Action Restart -WhatIf
    Assert-PortalTest ($h.Calls.Count -eq 0) 'WhatIf allowed delegated mutation.';$passed++
    Reject-PortalTest {& $entry -Action Stop -InstallMissing}
    Assert-PortalTest ($h.Calls.Count -eq 0) 'Invalid InstallMissing was not rejected before delegation.';$passed++
    $h.FailDelegation=$true
    Reject-PortalTest {& $entry -Action Restart -NoBrowser}
    Assert-PortalTest ($h.Calls.Count -eq 1) 'Delegation failure was retried or hidden.';$passed++

    [ordered]@{passed=$passed;scope='Copied production portal status/compatibility wrapper with synthetic native metadata and suite delegate; no real services or credentials controlled';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|
        ConvertTo-Json|Set-Content -LiteralPath (Join-Path $fixture 'receipt.json') -Encoding utf8NoBOM
    Write-Host "PASS: $passed portal status/delegation scenarios. Evidence: $fixture/receipt.json"
}
finally {
    $env:STUDIO_Portal__Port=$savedPort
    Remove-Variable PortalLifecycleFixture -Scope Global
}
