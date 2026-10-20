#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base=Join-Path $root ('artifacts/telemetry-lifecycle-tests/'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $base -Force
if(Get-Variable ObservabilityFixture -Scope Global -ErrorAction SilentlyContinue){throw 'A telemetry lifecycle fixture is already active.'}
$passed=0
$savedEnvironment=@{}
foreach($name in @('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS')){$savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
function Assert-TelemetryLifecycle([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject-TelemetryLifecycle([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Assert-TelemetryLifecycle $rejected 'Expected telemetry lifecycle rejection.'}
function New-TelemetryFixture {
    $directory=Join-Path $base ([guid]::NewGuid().ToString('N'))
    $state=Join-Path $directory '.local/observability'
    foreach($folder in @('scripts','config','.local/observability','.local/observability/aspire-home/versions/13.6.0')){$null=New-Item -ItemType Directory -Path (Join-Path $directory $folder) -Force}
    foreach($name in @('Observability.Common.ps1','Start-Observability.ps1','Stop-Observability.ps1','Get-Observability.ps1','Use-Observability.ps1')){Copy-Item -LiteralPath (Join-Path $root ('scripts/'+$name)) -Destination (Join-Path $directory 'scripts')}
    Copy-Item -LiteralPath (Join-Path $root 'config/observability.json') -Destination (Join-Path $directory 'config/observability.json')
    $architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $cli=Join-Path $directory ".tools/aspire-cli/node_modules/@microsoft/aspire-cli-win-$architecture/bin/aspire.exe"
    $null=New-Item -ItemType Directory -Path (Split-Path $cli -Parent) -Force
    [IO.File]::WriteAllText($cli,'synthetic executable; never started')
    $native=Join-Path $state 'aspire-home/versions/13.6.0/Aspire.Dashboard.exe'
    [IO.File]::WriteAllText($native,'synthetic executable; never started')
    $h=@{Root=$directory;State=$state;Cli=$cli;Native=$native;Processes=@{};Calls=[Collections.Generic.List[string]]::new();Denied=$false;BadAcl=$false;Foreign=$false;ForeignChild=$false;LaunchExit=$false;StopNoop=$false;CimDenied=$false;HttpFailure=$false;InstallExitCode=0;NextId=4001;PrivateConfig=(Join-Path $state 'dashboard.json');Receipt=(Join-Path $state 'process.json')}
    $global:ObservabilityFixture=$h
    [ordered]@{schemaVersion=1;cliVersion='13.6.0';cliPath=$cli;dashboardUrl='http://127.0.0.1:18888';otlpGrpcUrl='http://127.0.0.1:4317';otlpHttpUrl='http://127.0.0.1:4318';apiKey=('a'*64);otlpApiKey=('b'*64);browserToken=('c'*64)}|ConvertTo-Json|Set-Content -LiteralPath $h.PrivateConfig
    # The real scripts execute against synthetic filesystem/process/HTTP boundaries only.
    @'
function Get-ObservabilityPaths {
    $h=$global:ObservabilityFixture
    @{Root=$h.Root;State=$h.State;PrivateConfig=$h.PrivateConfig;Receipt=$h.Receipt;Settings=(Join-Path $h.Root 'config/observability.json');Lock=(Join-Path $h.State 'lifecycle.lock')}
}
function Protect-ObservabilityDirectory([string]$Path){$global:ObservabilityFixture.Calls.Add('Protect');$null=New-Item -ItemType Directory -Path $Path -Force}
function Assert-ObservabilityPrivateFile([string]$Path){if($global:ObservabilityFixture.BadAcl){throw 'Synthetic private ACL failure.'};$null=Assert-ObservabilityPath $Path;if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw 'Synthetic private file missing.'}}
function Get-NetTCPConnection {
    [CmdletBinding()]param($State)
    $h=$global:ObservabilityFixture
    if($h.Denied){throw 'Synthetic listener inspection denied.'}
    if($h.Foreign){[pscustomobject]@{LocalPort=18888;LocalAddress='127.0.0.1';OwningProcess=9999};return}
    if($h.Processes.ContainsKey(4002)){foreach($port in @(18888,4317,4318)){[pscustomobject]@{LocalPort=$port;LocalAddress='127.0.0.1';OwningProcess=4002}}}
}
function Get-Process {[CmdletBinding()]param($Id) $global:ObservabilityFixture.Processes[[int]$Id]}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    $h=$global:ObservabilityFixture
    if($h.CimDenied){throw 'Synthetic process inspection denied.'}
    $rows=@()
    foreach($process in $h.Processes.Values){
        $ticks=$process.StartTime.Ticks
        $rows+=[pscustomobject]@{ProcessId=$process.Id;ParentProcessId=$process.Parent;ExecutablePath=$process.Path;CommandLine=$process.Command;CreationDate=([datetime]::new($ticks-($ticks%10),[DateTimeKind]::Utc));Name=[IO.Path]::GetFileName($process.Path)}
    }
    if($Filter -match '^ProcessId=(\d+)$'){$rows|Where-Object ProcessId -eq ([int]$Matches[1]);return}
    if($Filter -match '^ParentProcessId=(\d+)$'){
        $parent=[int]$Matches[1];$rows|Where-Object ParentProcessId -eq $parent
        if($h.ForeignChild -and $parent -eq 4001){[pscustomobject]@{ProcessId=8999;ParentProcessId=4001;ExecutablePath='C:\foreign.exe';CreationDate=[datetime]::UtcNow;CommandLine='foreign';Name='foreign.exe'}}
        return
    }
    $rows
}
function New-SyntheticObservabilityProcess([int]$Number,[string]$Executable,[int]$Parent,[string]$Command){
    $h=$global:ObservabilityFixture
    $process=[pscustomobject]@{Id=$Number;Path=$Executable;StartTime=([datetime]::new(639281088000000001,[DateTimeKind]::Utc).AddSeconds($Number-4001));Parent=$Parent;Command=$Command}
    $process|Add-Member -MemberType ScriptProperty -Name HasExited -Value {-not $global:ObservabilityFixture.Processes.ContainsKey($this.Id)}
    $process|Add-Member -MemberType ScriptMethod -Name Refresh -Value {}
    $process|Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($Budget) -not $global:ObservabilityFixture.Processes.ContainsKey($this.Id)}
    $h.Processes[$Number]=$process
    $process
}
function Start-Process {
    [CmdletBinding()]param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru,$Environment,$RedirectStandardOutput,$RedirectStandardError)
    $h=$global:ObservabilityFixture
    if($FilePath -ne $h.Cli){$h.Calls.Add('Browser');return}
    $h.Calls.Add('Start')
    if($WindowStyle -ne 'Hidden' -or $ArgumentList -contains '--api-key' -or @($ArgumentList|Where-Object {$_ -match '^[a-f0-9]{64}$'}).Count){throw 'Synthetic secret/launch contract failed.'}
    $main=New-SyntheticObservabilityProcess 4001 $h.Cli 1234 ('aspire dashboard run --config-file-path "'+(Join-Path $h.State 'dashboard-settings.json')+'"')
    if(!$h.LaunchExit){$null=New-SyntheticObservabilityProcess 4002 $h.Native 4001 'Aspire.Dashboard'}else{$h.Processes.Remove(4001)}
    $main
}
function Stop-Process {
    [CmdletBinding()]param($InputObject,[switch]$Force)
    $h=$global:ObservabilityFixture;$h.Calls.Add('Stop:'+ $InputObject.Id)
    if(!$h.StopNoop){$h.Processes.Remove($InputObject.Id)}
}
function Invoke-WebRequest {[CmdletBinding()]param($Uri,[switch]$NoProxy,[switch]$SkipHttpErrorCheck,$TimeoutSec,$MaximumRedirection) if($global:ObservabilityFixture.HttpFailure){throw 'Synthetic frontend unresponsive.'};[pscustomobject]@{StatusCode=200}}
function npm {
    $h=$global:ObservabilityFixture;$h.Calls.Add('Install')
    Write-Output 'Synthetic native install standard output.'
    Write-Error 'Synthetic installer error stream.' -ErrorAction Continue
    if($h.InstallExitCode -eq 0){[IO.File]::WriteAllText($h.Cli,'synthetic executable; never started')}
    $global:LASTEXITCODE=$h.InstallExitCode
}
'@ | Add-Content -LiteralPath (Join-Path $directory 'scripts/Observability.Common.ps1')
    $h
}
function Read-TelemetryFixtureStatus($h){& (Join-Path $h.Root 'scripts/Get-Observability.ps1')}
function Start-TelemetryFixture($h){& (Join-Path $h.Root 'scripts/Start-Observability.ps1') -NoBrowser}
function Stop-TelemetryFixture($h){& (Join-Path $h.Root 'scripts/Stop-Observability.ps1')}
try {
    $h=New-TelemetryFixture
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'Stopped') 'Empty fixture was not stopped.';$passed++
    $h.Denied=$true
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'Unknown') 'Denied listener query was mistaken for a free port.'
    Reject-TelemetryLifecycle {Start-TelemetryFixture $h};Assert-TelemetryLifecycle ($h.Calls.Count -eq 0) 'Denied startup mutated state.';$passed++
    $h=New-TelemetryFixture;$h.CimDenied=$true
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'Unknown') 'Denied process metadata was mistaken for a stopped state.';$passed++
    $h=New-TelemetryFixture;$h.Foreign=$true
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'UnownedListener') 'Foreign listener was not detected.'
    Reject-TelemetryLifecycle {Start-TelemetryFixture $h};Assert-TelemetryLifecycle ($h.Calls.Count -eq 0) 'Foreign listener startup mutated state.';$passed++
    $h=New-TelemetryFixture;$keyHash=(Get-FileHash -LiteralPath $h.PrivateConfig).Hash
    & (Join-Path $h.Root 'scripts/Start-Observability.ps1') -NoBrowser -WhatIf|Out-Null
    Assert-TelemetryLifecycle ($h.Calls.Count -eq 0 -and !(Test-Path -LiteralPath $h.Receipt) -and (Get-FileHash -LiteralPath $h.PrivateConfig).Hash -eq $keyHash) 'Start WhatIf changed keys or state.';$passed++
    $h=New-TelemetryFixture;$keyHash=(Get-FileHash -LiteralPath $h.PrivateConfig).Hash
    $status=Start-TelemetryFixture $h
    Assert-TelemetryLifecycle ($status.State -eq 'Running' -and $status.Healthy -and $status.OwnershipVerified -and $status.StartedByInvocation) 'Startup did not verify ownership/readiness or record creation.'
    Assert-TelemetryLifecycle ((Get-FileHash -LiteralPath $h.PrivateConfig).Hash -eq $keyHash) 'Startup rotated private keys.';$passed++
    $identity=$status.Identity
    Assert-TelemetryLifecycle ($identity -eq "4001:$($h.Processes[4001].StartTime.Ticks)|4002:$($h.Processes[4002].StartTime.Ticks)") 'Status did not bind both collector process generations.';$passed++
    $receipt=Get-Content -LiteralPath $h.Receipt -Raw|ConvertFrom-Json
    $receipt.listenerStartedUtc=([datetimeoffset]$receipt.listenerStartedUtc).AddSeconds(1).ToString('O')
    $receipt|ConvertTo-Json|Set-Content -LiteralPath $h.Receipt
    $h.Processes[4002].StartTime=$h.Processes[4002].StartTime.AddSeconds(1)
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).Identity -ne $identity) 'A dashboard generation replacement retained the previous identity.';$passed++
    $receipt.listenerStartedUtc=([datetimeoffset]$receipt.listenerStartedUtc).AddSeconds(-1).ToString('O')
    $receipt|ConvertTo-Json|Set-Content -LiteralPath $h.Receipt
    $h.Processes[4002].StartTime=$h.Processes[4002].StartTime.AddSeconds(-1)
    $safe=$status|ConvertTo-Json -Compress
    Assert-TelemetryLifecycle ($safe -notmatch '[a-f0-9]{64}|login\?t=|apiKey|browserToken') 'Status exposed private authentication.';$passed++
    $count=$h.Calls.Count;$reused=Start-TelemetryFixture $h
    Assert-TelemetryLifecycle ($h.Calls.Count -eq $count -and !$reused.StartedByInvocation) 'Idempotent startup mutated a running collector or claimed another invocation creation.';$passed++
    $imported=@(& (Join-Path $h.Root 'scripts/Use-Observability.ps1'))
    Assert-TelemetryLifecycle ($imported.Count -eq 0 -and $env:OTEL_EXPORTER_OTLP_ENDPOINT -eq 'http://127.0.0.1:4318' -and $env:OTEL_EXPORTER_OTLP_HEADERS -eq ('x-otlp-api-key='+('b'*64))) 'Telemetry import returned private output or incorrect exporter configuration.';$passed++
    $h.HttpFailure=$true
    $unhealthy=Read-TelemetryFixtureStatus $h
    Assert-TelemetryLifecycle ($unhealthy.State -eq 'Unhealthy' -and $unhealthy.OwnershipVerified -and !$unhealthy.Healthy) 'Listening but unresponsive dashboard was healthy.'
    Reject-TelemetryLifecycle {& (Join-Path $h.Root 'scripts/Use-Observability.ps1')};$h.HttpFailure=$false;$passed++
    $count=$h.Calls.Count
    & (Join-Path $h.Root 'scripts/Stop-Observability.ps1') -WhatIf|Out-Null
    Assert-TelemetryLifecycle ($h.Calls.Count -eq $count -and $h.Processes.Count -eq 2) 'Stop WhatIf mutated live fixture state.';$passed++
    $h.BadAcl=$true
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'Unknown') 'Private ownership failure was not unknown.'
    Reject-TelemetryLifecycle {Stop-TelemetryFixture $h};Assert-TelemetryLifecycle ($h.Processes.Count -eq 2) 'Private ownership failure stopped a process.';$passed++
    $h.BadAcl=$false;$h.ForeignChild=$true
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'OwnershipMismatch') 'Foreign child was not rejected.'
    Reject-TelemetryLifecycle {Stop-TelemetryFixture $h};Assert-TelemetryLifecycle ($h.Processes.Count -eq 2) 'Foreign child inventory allowed a stop.';$passed++
    $h.ForeignChild=$false;$main=$h.Processes[4001];$main.StartTime=$main.StartTime.AddSeconds(1)
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'OwnershipMismatch') 'PID reuse was not rejected.'
    Reject-TelemetryLifecycle {Stop-TelemetryFixture $h};Assert-TelemetryLifecycle ($h.Processes.Count -eq 2) 'PID reuse allowed a stop.';$passed++
    $main.StartTime=$main.StartTime.AddSeconds(-1);$h.StopNoop=$true
    Reject-TelemetryLifecycle {Stop-TelemetryFixture $h};Assert-TelemetryLifecycle (Test-Path -LiteralPath $h.Receipt) 'Failed stop removed its receipt.';$passed++
    $h.StopNoop=$false;$status=Stop-TelemetryFixture $h
    Assert-TelemetryLifecycle ($status.State -eq 'Stopped' -and $h.Processes.Count -eq 0 -and !(Test-Path -LiteralPath $h.Receipt)) 'Stop did not leave a verified stopped state.'
    Assert-TelemetryLifecycle ($h.Calls.IndexOf('Stop:4002') -lt $h.Calls.IndexOf('Stop:4001')) 'Collector CLI stopped before its verified dashboard child.'
    Assert-TelemetryLifecycle ((Get-FileHash -LiteralPath $h.PrivateConfig).Hash -eq $keyHash) 'Stop destroyed private keys.';$passed++
    $status=Start-TelemetryFixture $h
    Assert-TelemetryLifecycle ($status.State -eq 'Running' -and (Get-FileHash -LiteralPath $h.PrivateConfig).Hash -eq $keyHash) 'Stop/start did not retain credentials.';$passed++
    $h.Processes.Clear()
    Assert-TelemetryLifecycle ((Read-TelemetryFixtureStatus $h).State -eq 'StaleReceipt') 'Absent processes were not classified stale.'
    $status=Start-TelemetryFixture $h
    Assert-TelemetryLifecycle ($status.State -eq 'Running' -and (Get-FileHash -LiteralPath $h.PrivateConfig).Hash -eq $keyHash) 'Safe stale startup failed or rotated keys.';$passed++
    $h=New-TelemetryFixture;$h.LaunchExit=$true
    Reject-TelemetryLifecycle {Start-TelemetryFixture $h}
    Assert-TelemetryLifecycle ((Test-Path -LiteralPath $h.Receipt) -and (Test-Path -LiteralPath $h.PrivateConfig)) 'Failed launch removed private recovery state.';$passed++
    $h=New-TelemetryFixture
    Remove-Item -LiteralPath $h.Cli
    Reject-TelemetryLifecycle {Start-TelemetryFixture $h}
    Assert-TelemetryLifecycle (!$h.Calls.Contains('Start')) 'Missing pinned CLI started a process.';$passed++
    $h=New-TelemetryFixture;Remove-Item -LiteralPath $h.Cli
    $installed=@(& (Join-Path $h.Root 'scripts/Start-Observability.ps1') -NoBrowser -InstallMissing)
    Assert-TelemetryLifecycle ($installed.Count -eq 1 -and $installed[0].State -eq 'Running' -and (Test-Path -LiteralPath (Join-Path $h.State 'install.log'))) 'Installer output contaminated the single startup status object.';$passed++
    $h=New-TelemetryFixture;Remove-Item -LiteralPath $h.Cli;$h.InstallExitCode=1
    Reject-TelemetryLifecycle {& (Join-Path $h.Root 'scripts/Start-Observability.ps1') -NoBrowser -InstallMissing}
    Assert-TelemetryLifecycle (!$h.Calls.Contains('Start') -and (Test-Path -LiteralPath $h.PrivateConfig)) 'Failed installer started telemetry or destroyed keys.';$passed++
    $h=New-TelemetryFixture;$h.Processes.Clear();$null=Start-TelemetryFixture $h;$h.Processes.Clear();$h.Foreign=$true
    Reject-TelemetryLifecycle {Start-TelemetryFixture $h};Reject-TelemetryLifecycle {Stop-TelemetryFixture $h}
    Assert-TelemetryLifecycle (Test-Path -LiteralPath $h.Receipt) 'Foreign listener allowed stale receipt cleanup.';$passed++
    [ordered]@{passed=$passed;scope='Synthetic processes/listeners/HTTP/ACL; no real service lifecycle or credentials';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $base 'results.json')
    Write-Host "PASS: $passed observability lifecycle scenarios. Evidence: $base/results.json"
} finally {
    foreach($name in $savedEnvironment.Keys){
        if($null -eq $savedEnvironment[$name]){if(Test-Path -LiteralPath "Env:\$name"){Remove-Item -LiteralPath "Env:\$name" -ErrorAction Stop}}
        else{[Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process')}
    }
    Remove-Variable ObservabilityFixture -Scope Global -ErrorAction SilentlyContinue
}
