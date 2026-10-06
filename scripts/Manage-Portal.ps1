#requires -Version 7.4
<#
.SYNOPSIS
Inspect or run the governed portal stop/build/start lifecycle.
.EXAMPLE
./scripts/Manage-Portal.ps1 -Action Restart
.EXAMPLE
./scripts/Manage-Portal.ps1 -Action Status
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Status','Start','Stop','Restart')][string]$Action='Status',
    [switch]$InstallMissing,
    [switch]$NoBrowser
)
$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'Portal lifecycle supports Windows only.'}
if($InstallMissing -and $Action -notin @('Start','Restart')){throw '-InstallMissing requires Start or Restart.'}
$root=Split-Path $PSScriptRoot -Parent
$receipt=Join-Path $root '.local/portal/process.json'
$assembly=Join-Path $root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll'
$port=5087
$config=Join-Path $root 'src/SelfService.Portal/portal.local.json'
if(Test-Path -LiteralPath $config){$settings=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json;if($settings.Portal.Port){$port=[int]$settings.Portal.Port}}
if($env:STUDIO_Portal__Port){$port=[int]$env:STUDIO_Portal__Port}
if($port -lt 1024 -or $port -gt 65535){throw 'Invalid portal port.'}
$url="http://localhost:$port"

function Get-PortalStatus {
    $result=[ordered]@{State='Unknown';Url=$url;ProcessId=$null;OwnershipVerified=$false;Healthy=$false;Detail='';Receipt=$receipt}
    try {
        # Query all listeners: a free requested port is not a command failure.
        $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port)
        if(!(Test-Path -LiteralPath $receipt)){
            $result.State=if($listeners.Count){'UnownedListener'}else{'Stopped'}
            $result.Detail='No main portal ownership receipt. Review instances are managed separately.'
            return [pscustomobject]$result
        }
        $record=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
        $result.ProcessId=[int]$record.pid
        if($result.ProcessId -le 0){throw 'Invalid process receipt.'}
        $process=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if(!$process){
            $result.State=if($listeners.Count){'UnownedListener'}else{'StaleReceipt'}
            $result.Detail='Recorded process is absent. Governed Stop performs receipt/session cleanup.'
            return [pscustomobject]$result
        }
        $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.pid)" -ErrorAction Stop).CommandLine
        if([string]::IsNullOrWhiteSpace($process.Path) -or [string]::IsNullOrWhiteSpace($command)){
            throw 'Process executable/command line is unavailable in this security context. Run Status in the Windows account that started the portal. Ownership remains unverified.'
        }
        if([IO.Path]::GetFullPath($record.assembly) -ne $assembly -or $process.Path -ne $record.executable -or
            $process.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$record.startedUtc).UtcTicks -or
            !$command -or !$command.Contains('"'+$assembly+'"')){
            $result.State='OwnershipMismatch';$result.Detail='Receipt does not match the live process. No process may be adopted.'
            return [pscustomobject]$result
        }
        $result.OwnershipVerified=$true
        if(@($listeners | Where-Object OwningProcess -ne $process.Id).Count){
            $result.State='UnownedListener';$result.Detail='Configured port belongs to a different process.'
            return [pscustomobject]$result
        }
        $result.State='Unhealthy'
        $result.Detail='Owned process is running, but the configured listener/bootstrap is not ready.'
        if($listeners.Count){
            # Match Start-Portal: workloads is a keyed dictionary, not an array.
            $expected=(Get-Content (Join-Path $root 'config/workloads.json') -Raw|ConvertFrom-Json -AsHashtable).workloads.Count
            $health=Invoke-RestMethod "$url/api/bootstrap" -TimeoutSec 3 -MaximumRedirection 0
            if($health.products.Count -eq $expected){$result.State='Ready';$result.Healthy=$true;$result.Detail='Owned listener and expected workload catalog verified; provider sign-ins are not checked.'}
            else{$result.Detail="Bootstrap workload count mismatch: expected $expected, received $($health.products.Count)."}
        }
    } catch {
        # Failure to inspect ownership/listeners is not evidence that a port is free.
        if($result.State -ne 'Unhealthy'){$result.State='Unknown'}
        $result.Detail="Status could not be fully verified: $($_.Exception.Message)"
    }
    [pscustomobject]$result
}

$before=Get-PortalStatus
if($Action -eq 'Status'){$before;return}
if($before.State -in @('Unknown','OwnershipMismatch','UnownedListener')){throw "Lifecycle blocked: $($before.State). $($before.Detail)"}
if($Action -eq 'Start' -and $before.State -ne 'Stopped'){throw 'Start requires a stopped portal with no receipt. Use Restart for an owned process or stale receipt.'}
foreach($name in @('Stop-Portal.ps1','Setup-Portal.ps1','Start-Portal.ps1')){
    if(!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))){throw "Missing lifecycle script: $name"}
}
if(!$PSCmdlet.ShouldProcess($url,"$Action main portal (Start/Restart build Release; Stop/Restart clear portal sessions)")){return}
if($Action -in @('Stop','Restart')){
    Write-Host 'Stopping the registered main portal. Azure/ADO and portal Codex sessions will need reconnection; GitHub CLI login can usually be reused.'
    & (Join-Path $PSScriptRoot 'Stop-Portal.ps1')
    $stopped=Get-PortalStatus
    if($stopped.State -ne 'Stopped'){throw "Stop did not leave a free, unregistered main portal: $($stopped.State). No build/start attempted."}
}
if($Action -in @('Start','Restart')){
    & (Join-Path $PSScriptRoot 'Setup-Portal.ps1') -InstallMissing:$InstallMissing
    & (Join-Path $PSScriptRoot 'Start-Portal.ps1') -NoBrowser:$NoBrowser
    $after=Get-PortalStatus
    if($after.State -ne 'Ready'){throw "Portal startup verification failed: $($after.State). $($after.Detail) Inspect .local/portal logs; use Stop for governed cleanup."}
    $after
}else{$stopped}
