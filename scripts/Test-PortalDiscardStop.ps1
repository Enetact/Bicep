#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$base=Join-Path $root ('artifacts/portal-discard-stop/'+[guid]::NewGuid().ToString('N'))
if(Get-Variable PortalDiscardFixture -Scope Global -ErrorAction SilentlyContinue){throw 'A portal discard fixture already exists.'}
$passed=0
function Assert-DiscardTest([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject-DiscardTest([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Assert-DiscardTest $rejected 'Expected portal discard rejection.'}
function New-DiscardFixture {
    $directory=Join-Path $base ([guid]::NewGuid().ToString('N'))
    foreach($name in @('scripts','.local/portal/agents/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','.local/portal/github-cli/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','src/SelfService.Portal/bin/Release/net10.0')){$null=New-Item -ItemType Directory -Path (Join-Path $directory $name) -Force}
    foreach($name in @('Suite.Common.ps1','Stop-Portal.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $directory 'scripts')}
    $started=[datetime]::new(639281088000000001,[DateTimeKind]::Utc)
    $assembly=Join-Path $directory 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll'
    $receipt=Join-Path $directory '.local/portal/process.json'
    $agent=Join-Path $directory '.local/portal/agents/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $github=Join-Path $directory '.local/portal/github-cli/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $h=@{Root=$directory;Receipt=$receipt;Agent=$agent;Github=$github;Processes=@{};Calls=[Collections.Generic.List[string]]::new();StuckId=0;BadPath='';Hidden=$false;Foreign=$false;RegistrationSwap=$false;Start=$started}
    $global:PortalDiscardFixture=$h
    @{pid=4321;startedUtc=$started.ToString('O');executable='C:\fixture\dotnet.exe';assembly=$assembly}|ConvertTo-Json|Set-Content -LiteralPath $receipt
    @{pid=4322;parentPid=4321;startedUtc=$started.AddSeconds(1).ToString('O');executable='C:\fixture\codex.exe'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $agent 'process.json')
    @{pid=4323;parentPid=4321;parentStartedUtc=$started.ToString('O');startedUtc=$started.AddSeconds(2).ToString('O');executable='C:\fixture\gh.exe'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $github 'process.json')
    'synthetic authentication; never a real credential'|Set-Content -LiteralPath (Join-Path $agent 'auth.json')
    'retained agent diagnostics'|Set-Content -LiteralPath (Join-Path $agent 'session.log')
    foreach($spec in @(@{Id=4321;Path='C:\fixture\dotnet.exe';Parent=1;Command=('dotnet "'+$assembly+'"');Time=$started},@{Id=4322;Path='C:\fixture\codex.exe';Parent=4321;Command='codex app-server --listen stdio://';Time=$started.AddSeconds(1)},@{Id=4323;Path='C:\fixture\gh.exe';Parent=4321;Command='gh auth login --hostname github.com';Time=$started.AddSeconds(2)})){
        $process=[pscustomobject]@{Id=$spec.Id;Path=$spec.Path;StartTime=$spec.Time;Parent=$spec.Parent;Command=$spec.Command}
        $process|Add-Member ScriptMethod WaitForExit {param($Budget) -not $global:PortalDiscardFixture.Processes.ContainsKey($this.Id)}
        $h.Processes[$spec.Id]=$process
    }
    @'
function Get-Item {
    [CmdletBinding()]param([string]$LiteralPath,[switch]$Force)
    $entry=Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force
    if($LiteralPath -eq $global:PortalDiscardFixture.BadPath){[pscustomobject]@{Attributes=[IO.FileAttributes]::ReparsePoint;Length=$entry.Length};return}
    $entry
}
function Get-Process {[CmdletBinding()]param($Id) $global:PortalDiscardFixture.Processes[[int]$Id]}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    $h=$global:PortalDiscardFixture
    if($h.Hidden){throw 'Synthetic process metadata denied.'}
    $rows=@($h.Processes.Values|ForEach-Object {[pscustomobject]@{ProcessId=$_.Id;ParentProcessId=$_.Parent;ExecutablePath=$_.Path;Name=[IO.Path]::GetFileName($_.Path);CreationDate=$_.StartTime;CommandLine=$_.Command}})
    if($Filter -match '^ProcessId=(\d+)$'){$rows|Where-Object ProcessId -eq ([int]$Matches[1]);return}
    if($Filter -match '^ParentProcessId=(\d+)$'){
        $parent=[int]$Matches[1];$rows|Where-Object ParentProcessId -eq $parent
        if($h.Foreign){[pscustomobject]@{ProcessId=9999;ParentProcessId=4321;ExecutablePath='C:\foreign.exe';Name='foreign.exe';CreationDate=$h.Start;CommandLine='foreign'}}
        return
    }
    throw 'Unexpected synthetic process query.'
}
function Stop-Process {
    [CmdletBinding()]param($InputObject,[switch]$Force)
    $h=$global:PortalDiscardFixture;$h.Calls.Add('Stop:'+$InputObject.Id)
    if($InputObject.Id -ne $h.StuckId){$h.Processes.Remove($InputObject.Id)}
    if($InputObject.Id -eq 4321 -and $h.RegistrationSwap){$record=Get-Content -LiteralPath $h.Receipt -Raw|ConvertFrom-Json;$record.startedUtc=$h.Start.AddMinutes(1).ToString('O');$record|ConvertTo-Json|Set-Content -LiteralPath $h.Receipt}
}
'@|Add-Content -LiteralPath (Join-Path $directory 'scripts/Suite.Common.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]
param([switch]$CheckpointOnly,[switch]$NoBrowser)
$global:PortalDiscardFixture.Calls.Add('CheckpointOnly:'+ [bool]$CheckpointOnly)
'@|Set-Content -LiteralPath (Join-Path $directory 'scripts/Restart-PortalFresh.ps1')
    $h
}
function Invoke-DiscardFixture($h){& (Join-Path $h.Root 'scripts/Stop-Portal.ps1') -DiscardConnections}
function Assert-DiscardStateRetained($h){Assert-DiscardTest ((Test-Path -LiteralPath $h.Receipt) -and (Test-Path -LiteralPath (Join-Path $h.Agent 'process.json')) -and (Test-Path -LiteralPath (Join-Path $h.Agent 'auth.json')) -and (Test-Path -LiteralPath (Join-Path $h.Github 'process.json'))) 'Failure removed credential or receipt recovery state.'}
function Add-DiscardAdvancedHelpers($h){
    $h.Azure=Join-Path $h.Root '.local/portal/azure-cli/cccccccccccccccccccccccccccccccc'
    $h.Mcp=Join-Path $h.Root '.local/portal/mcp/dddddddddddddddddddddddddddddddd/ado'
    foreach($directory in @($h.Azure,$h.Mcp,(Join-Path $h.Azure 'nested'))){$null=New-Item -ItemType Directory -Path $directory -Force}
    @{parentPid=4321;parentStartedUtc=$h.Start.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $h.Azure 'owner.json')
    @{pid=4324;parentPid=4321;startedUtc=$h.Start.AddSeconds(3).ToString('O');executable='C:\fixture\az.exe'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $h.Azure 'process.json')
    @{schema='platform.mcp-process/v1';provider='ado';pid=4325;parentPid=4321;parentStartedUtc=$h.Start.ToString('O');startedUtc=$h.Start.AddSeconds(4).ToString('O');executable='C:\fixture\node.exe'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $h.Mcp 'process.json')
    'synthetic fixture cache'|Set-Content -LiteralPath (Join-Path $h.Azure 'nested/cache.json')
    'synthetic fixture response'|Set-Content -LiteralPath (Join-Path $h.Mcp 'api-key-fixture.rsp')
    foreach($spec in @(@{Id=4324;Path='C:\fixture\az.exe';Command='az login';Seconds=3},@{Id=4325;Path='C:\fixture\node.exe';Command='node mcp';Seconds=4})){
        $process=[pscustomobject]@{Id=$spec.Id;Path=$spec.Path;StartTime=$h.Start.AddSeconds($spec.Seconds);Parent=4321;Command=$spec.Command}
        $process|Add-Member ScriptMethod WaitForExit {param($Budget) -not $global:PortalDiscardFixture.Processes.ContainsKey($this.Id)}
        $process|Add-Member ScriptMethod Kill {param($Tree) $h=$global:PortalDiscardFixture;$h.Calls.Add('Kill:'+$this.Id);if($this.Id -ne $h.StuckId){$h.Processes.Remove($this.Id)}}
        $h.Processes[$spec.Id]=$process
    }
}
try {
    $h=New-DiscardFixture
    & (Join-Path $h.Root 'scripts/Stop-Portal.ps1') -DiscardConnections -WhatIf|Out-Null
    Assert-DiscardTest ($h.Calls.Count -eq 0 -and $h.Processes.Count -eq 3) 'Discard WhatIf stopped a process.';Assert-DiscardStateRetained $h;$passed++
    $h=New-DiscardFixture
    & (Join-Path $h.Root 'scripts/Stop-Portal.ps1')|Out-Null
    Assert-DiscardTest ($h.Calls.Count -eq 1 -and $h.Calls[0] -eq 'CheckpointOnly:True' -and $h.Processes.Count -eq 3) 'Default stop did not delegate protected checkpoint flow.';Assert-DiscardStateRetained $h;$passed++
    foreach($bad in @('receipt','agent','auth','child-receipt','github-receipt')){
        $h=New-DiscardFixture
        $h.BadPath=switch($bad){receipt {$h.Receipt} agent {$h.Agent} auth {Join-Path $h.Agent 'auth.json'} 'child-receipt' {Join-Path $h.Agent 'process.json'} 'github-receipt' {Join-Path $h.Github 'process.json'}}
        Reject-DiscardTest {Invoke-DiscardFixture $h}
        Assert-DiscardTest ($h.Calls.Count -eq 0 -and $h.Processes.Count -eq 3) 'A linked state path allowed a process stop.';Assert-DiscardStateRetained $h;$passed++
    }
    foreach($number in @(4321,4322,4323)){
        $h=New-DiscardFixture;$h.StuckId=$number
        Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
        Assert-DiscardTest ($h.Processes.ContainsKey($number)) 'Stuck process fixture unexpectedly exited.'
        if($number -eq 4321){Assert-DiscardTest ($h.Calls.Count -eq 1 -and $h.Processes.Count -eq 3) 'Parent timeout still stopped helpers.'}
        $passed++
    }
    $h=New-DiscardFixture;$h.Hidden=$true
    Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
    Assert-DiscardTest ($h.Calls.Count -eq 0) 'Metadata denial allowed a stop.';$passed++
    $h=New-DiscardFixture;$h.Foreign=$true
    Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
    Assert-DiscardTest ($h.Calls.Count -eq 0) 'An unregistered child allowed a stop.';$passed++
    $h=New-DiscardFixture;$h.Processes[4322].StartTime=$h.Start.AddMinutes(1)
    Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
    Assert-DiscardTest ($h.Calls.Count -eq 0) 'A child PID reuse allowed a stop.';$passed++
    $h=New-DiscardFixture;$h.RegistrationSwap=$true
    Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
    Assert-DiscardTest ($h.Calls.Count -eq 1) 'A changed parent registration allowed helper shutdown.';$passed++
    $h=New-DiscardFixture
    Invoke-DiscardFixture $h|Out-Null
    Assert-DiscardTest ($h.Calls.Count -eq 3 -and $h.Calls[0] -eq 'Stop:4321' -and $h.Processes.Count -eq 0) 'Discard did not quiesce the parent before its exact registered helpers.'
    Assert-DiscardTest (!(Test-Path -LiteralPath $h.Receipt) -and !(Test-Path -LiteralPath (Join-Path $h.Agent 'auth.json')) -and (Test-Path -LiteralPath (Join-Path $h.Agent 'session.log'))) 'Successful discard removed retained logs or left authentication/receipt state.';$passed++
    foreach($kind in @('azure-cache','mcp-response')){
        $h=New-DiscardFixture;Add-DiscardAdvancedHelpers $h
        $h.BadPath=if($kind -eq 'azure-cache'){Join-Path $h.Azure 'nested/cache.json'}else{Join-Path $h.Mcp 'api-key-fixture.rsp'}
        Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
        Assert-DiscardTest ($h.Calls.Count -eq 0 -and $h.Processes.Count -eq 5) 'A linked CLI cache or MCP response file allowed a stop.';$passed++
    }
    foreach($number in @(4324,4325)){
        $h=New-DiscardFixture;Add-DiscardAdvancedHelpers $h;$h.StuckId=$number
        Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
        Assert-DiscardTest ((Test-Path -LiteralPath (Join-Path $h.Azure 'nested/cache.json')) -and (Test-Path -LiteralPath (Join-Path $h.Mcp 'process.json')) -and $h.Processes.ContainsKey($number)) 'A stuck CLI/MCP helper allowed credential cleanup.';$passed++
    }
    $h=New-DiscardFixture;Add-DiscardAdvancedHelpers $h
    $path=Join-Path $h.Azure 'process.json';$record=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json;$record.parentPid=9999;$record|ConvertTo-Json|Set-Content -LiteralPath $path
    Reject-DiscardTest {Invoke-DiscardFixture $h};Assert-DiscardStateRetained $h
    Assert-DiscardTest ($h.Calls.Count -eq 0 -and (Test-Path -LiteralPath (Join-Path $h.Azure 'nested/cache.json'))) 'A foreign CLI writer was treated as a discardable owned cache.';$passed++
    $h=New-DiscardFixture;Add-DiscardAdvancedHelpers $h
    Invoke-DiscardFixture $h|Out-Null
    Assert-DiscardTest ($h.Calls.Count -eq 5 -and $h.Calls[0] -eq 'Stop:4321' -and $h.Calls.Contains('Kill:4325') -and $h.Processes.Count -eq 0 -and !(Test-Path -LiteralPath $h.Azure) -and !(Test-Path -LiteralPath (Join-Path $h.Mcp 'process.json'))) 'Exact CLI/MCP cleanup did not preserve its existing process authority.';$passed++
    [ordered]@{passed=$passed;scope='Copied production scripts with synthetic process metadata and reparse attributes; no real service, keys or cloud access';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $base 'results.json')
    Write-Host "PASS: $passed synthetic portal discard scenarios. Evidence: $base/results.json"
}finally{Remove-Variable PortalDiscardFixture -Scope Global -ErrorAction SilentlyContinue}
