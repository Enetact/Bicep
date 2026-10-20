#requires -Version 7.4
[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repository 'scripts/local-common.ps1')
$base=Join-Path $repository ('.local/local-lifecycle-tests/'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $base
$passed=0
$saved=@{};foreach($key in @('PATH','DOTNET_ROOT','OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS','TRACEPARENT','TRACESTATE','BAGGAGE','AZURE_CLIENT_SECRET','DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT')){$saved[$key]=[Environment]::GetEnvironmentVariable($key)}
function Assert-Test([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:passed++}
function Reject-Test([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Assert-Test $rejected 'Expected unsafe local input to be rejected.'}
function Get-ProjectRoot{$global:LocalRuntimeFixture.Root}
function New-Fixture {
    $root=Join-Path $base ([guid]::NewGuid().ToString('N'))
    foreach($folder in @('scripts','.local/data','.local/logs','src/BlobTransfer')){$null=New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force}
    foreach($name in @('common.ps1','local-common.ps1','Run-Local.ps1','Stop-Local.ps1','Get-Local.ps1')){Copy-Item -LiteralPath (Join-Path $repository "scripts/$name") -Destination (Join-Path $root "scripts/$name")}
    Copy-Item -LiteralPath (Join-Path $repository 'src/BlobTransfer/local.settings.azurite.example.json') -Destination (Join-Path $root 'src/BlobTransfer')
    'preserve data'|Set-Content (Join-Path $root '.local/data/sentinel');'preserve log'|Set-Content (Join-Path $root '.local/logs/sentinel')
    $tools=@{dotnet='C:\fixture\dotnet.exe';node='C:\fixture\node.exe';func='C:\fixture\func.exe';azurite=(Join-Path $root '.tools/azurite.js');powershell='C:\fixture\pwsh.exe'}
    $tools|ConvertTo-Json|Set-Content (Join-Path $root '.local/tools.json')
    @'
param([switch]$CheckOnly,[switch]$WriteReceipt)
if(!$CheckOnly -or !$WriteReceipt){throw 'Prepared runtime must validate without installing.'}
$global:LocalRuntimeFixture.Calls.Add('CheckTools')
'@|Set-Content (Join-Path $root 'scripts/Setup-Local.ps1')
    @'
param([switch]$NoBrowser,[switch]$InstallMissing)
$h=$global:LocalRuntimeFixture
$h.Calls.Add('StartTelemetry')
$existed=Test-Path -LiteralPath (Join-Path $h.Root '.local/observability/process.json')
$null=New-Item -ItemType Directory -Path (Join-Path $h.Root '.local/observability') -Force
'{"pid":7777}'|Set-Content (Join-Path $h.Root '.local/observability/process.json')
[pscustomobject]@{StartedByInvocation=(!$existed);Identity=$h.CollectorIdentity}
'@|Set-Content (Join-Path $root 'scripts/Start-Observability.ps1')
    @'
$h=$global:LocalRuntimeFixture
$identity=if($h.ReplaceCollector){'8888:222'}else{$h.CollectorIdentity}
[pscustomobject]@{State='Running';OwnershipVerified=(!$h.CollectorUnverified);Identity=$identity}
'@|Set-Content (Join-Path $root 'scripts/Get-Observability.ps1')
    @'
$h=$global:LocalRuntimeFixture
if($h.PortalStatusDenied){throw 'Synthetic portal inspection denied.'}
[pscustomobject]@{State=$h.PortalState}
'@|Set-Content (Join-Path $root 'scripts/Get-Portal.ps1')
    @'
$global:LocalRuntimeFixture.Calls.Add('UseTelemetry')
$env:OTEL_EXPORTER_OTLP_ENDPOINT='http://127.0.0.1:4318'
$env:OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf'
$env:OTEL_EXPORTER_OTLP_HEADERS='x-otlp-api-key=synthetic-key'
'@|Set-Content (Join-Path $root 'scripts/Use-Observability.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param()
$h=$global:LocalRuntimeFixture
$h.Calls.Add('StopTelemetry')
if($h.FailCollectorStop){throw 'Synthetic collector cleanup failure.'}
Remove-Item -LiteralPath (Join-Path $h.Root '.local/observability/process.json')
'@|Set-Content (Join-Path $root 'scripts/Stop-Observability.ps1')
    $global:LocalRuntimeFixture=@{Root=$root;Tools=$tools;Processes=@{};Calls=[Collections.Generic.List[string]]::new();Environments=@{};Denied=$false;MetadataDenied=$false;Foreign=$false;Health=$true;FailFunctions=$false;FailCollectorStop=$false;FailReceiptWrite=$false;FailEnvironmentRestore=$false;PortalState='Stopped';PortalStatusDenied=$false;CollectorIdentity='7777:111';ReplaceCollector=$false;CollectorUnverified=$false;SeedExit=0;StopNoop=$false;Started=[datetime]::UtcNow}
    $script:localRoot=Join-Path $root '.local'
    $bundle=Join-Path $localRoot 'builds/prepared'
    foreach($folder in @('blob-transfer','transfer-tool')){$null=New-Item -ItemType Directory -Path (Join-Path $bundle $folder) -Force}
    'synthetic DLL'|Set-Content (Join-Path $bundle 'blob-transfer/BlobTransfer.dll');'synthetic CLI'|Set-Content (Join-Path $bundle 'transfer-tool/TransferTool.dll')
    '{"version":"2.0"}'|Set-Content (Join-Path $bundle 'blob-transfer/host.json')
    $metadata=@(@{name='DispatchUploadedBlob';bindings=@(@{type='blobTrigger'})},@{name='CopyUploadedBlob';bindings=@(@{type='queueTrigger'})},@{name='ReconcileTransfers';bindings=@(@{type='timerTrigger'})},@{name='MonitorTransferPoison';bindings=@(@{type='timerTrigger'})},@{name='AuditTransferLedger';bindings=@(@{type='timerTrigger'})})
    $metadata|ConvertTo-Json -Depth 5|Set-Content (Join-Path $bundle 'blob-transfer/functions.metadata')
    Write-LocalBuildReceipt $bundle
    $global:LocalRuntimeFixture.Build=$bundle
    return $global:LocalRuntimeFixture
}
function New-TestProcess([int]$Number,[string]$Path,[string]$Command,[int]$Parent=0){
    $h=$global:LocalRuntimeFixture
    $p=[pscustomobject]@{Id=$Number;Path=$Path;StartTime=$h.Started.AddMilliseconds($Number);CommandLine=$Command;Parent=$Parent;ExitCode=0}
    $p|Add-Member ScriptMethod Refresh {}
    $p|Add-Member ScriptProperty HasExited {!$global:LocalRuntimeFixture.Processes.ContainsKey($this.Id)}
    $p|Add-Member ScriptMethod WaitForExit {param($Budget) if($this.Id -eq 5003){$global:LocalRuntimeFixture.Processes.Remove($this.Id);return $true};return !$global:LocalRuntimeFixture.Processes.ContainsKey($this.Id)}
    $p|Add-Member ScriptMethod Kill {param($Tree)
        if(!$Tree){throw 'Tree stop required.'}
        $fixtureVariable=Get-Variable LocalRuntimeFixture -Scope Global -ErrorAction SilentlyContinue
        $h=if($fixtureVariable){$fixtureVariable.Value}else{$null}
        if($h.StopNoop){return}
        $h.Calls.Add('Stop'+$this.Id)
        foreach($child in @($h.Processes.Values|Where-Object Parent -eq $this.Id)){$h.Processes.Remove($child.Id)}
        $h.Processes.Remove($this.Id)
    }
    $p|Add-Member ScriptMethod Dispose {if($this.Id -eq 5003){$global:LocalRuntimeFixture.Processes.Remove($this.Id)}}
    $h.Processes[$Number]=$p;return $p
}
function Get-Process{[CmdletBinding()]param($Id) $global:LocalRuntimeFixture.Processes[[int]$Id]}
function Get-NetTCPConnection{
    [CmdletBinding()]param($State)
    $h=$global:LocalRuntimeFixture
    if($h.Denied){throw 'Synthetic listener denied.'}
    if($h.Foreign){[pscustomobject]@{LocalPort=7071;LocalAddress='127.0.0.1';OwningProcess=9999};return}
    if($h.Processes.ContainsKey(5001)){foreach($port in @(10000,10001,10002)){[pscustomobject]@{LocalPort=$port;LocalAddress='127.0.0.1';OwningProcess=5001}}}
    if($h.Processes.ContainsKey(5002)){[pscustomobject]@{LocalPort=7071;LocalAddress='127.0.0.1';OwningProcess=5002}}
}
function Get-CimInstance{
    [CmdletBinding()]param($ClassName,$Filter)
    $h=$global:LocalRuntimeFixture
    if($h.MetadataDenied){throw 'Synthetic process denied.'}
    $rows=if($Filter -match '^ParentProcessId=(\d+)$'){@($h.Processes.Values|Where-Object Parent -eq ([int]$Matches[1]))}else{@($h.Processes[[int]($Filter -replace '^ProcessId=','')])}
    foreach($p in $rows){if($p){[pscustomobject]@{ProcessId=$p.Id;ExecutablePath=$p.Path;ParentProcessId=$p.Parent;CreationDate=$p.StartTime;CommandLine=$p.CommandLine}}}
}
function Invoke-RestMethod{[CmdletBinding()]param($Uri,$TimeoutSec,$MaximumRedirection) if(!$global:LocalRuntimeFixture.Health){throw 'Synthetic health failed.'};[pscustomobject]@{state='Running'}}
function Set-Content {
    [CmdletBinding()]param([Parameter(Position=0)][string]$Path,[string]$LiteralPath,[Parameter(ValueFromPipeline=$true)]$Value,[string]$Encoding)
    process {
        $target=if($LiteralPath){$LiteralPath}else{$Path}
        $fixtureVariable=Get-Variable LocalRuntimeFixture -Scope Global -ErrorAction SilentlyContinue
        $h=if($fixtureVariable){$fixtureVariable.Value}else{$null}
        if ($h -and $h.FailReceiptWrite -and $target -eq (Join-Path $h.Root '.local/run.json')) {
            Microsoft.PowerShell.Management\Set-Content -LiteralPath $target -Value '{incomplete'
            throw 'SYNTHETIC_RECEIPT_WRITE'
        }
        Microsoft.PowerShell.Management\Set-Content @PSBoundParameters
    }
}
function Remove-Item {
    [CmdletBinding(SupportsShouldProcess)]param([Parameter(Position=0)][string[]]$Path,[string[]]$LiteralPath,[switch]$Force,[switch]$Recurse)
    $fixtureVariable=Get-Variable LocalRuntimeFixture -Scope Global -ErrorAction SilentlyContinue
    $h=if($fixtureVariable){$fixtureVariable.Value}else{$null}
    if($h -and $h.FailEnvironmentRestore -and $LiteralPath -contains 'Env:OTEL_EXPORTER_OTLP_ENDPOINT') { throw 'SYNTHETIC_ENVIRONMENT_RESTORE' }
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Get-TestEnvironment {
    $state=@{}
    foreach($name in @('PATH','DOTNET_ROOT','OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS','DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT')) {
        $state[$name]=@{Exists=(Test-Path -LiteralPath "Env:$name");Value=[Environment]::GetEnvironmentVariable($name)}
    }
    return $state
}
function Assert-TestEnvironment($Before) {
    $after=Get-TestEnvironment
    foreach($name in $Before.Keys) { Assert-Test ($after[$name].Exists -eq $Before[$name].Exists -and $after[$name].Value -ceq $Before[$name].Value) ('Caller environment presence/value changed: '+$name) }
}
function Start-Process{
    [CmdletBinding()]param($FilePath,$ArgumentList,$Environment,$WindowStyle,[switch]$PassThru,$RedirectStandardOutput,$RedirectStandardError,$WorkingDirectory)
    $h=$global:LocalRuntimeFixture
    if($WindowStyle -ne 'Hidden'){throw 'Expected hidden helpers.'}
    if($FilePath -eq $h.Tools.node){$number=5001;$label='StartAzurite'}
    elseif($FilePath -eq $h.Tools.func){if($h.FailFunctions){throw 'SYNTHETIC_FUNCTIONS_START'};$number=5002;$label='StartFunctions'}
    else{$number=5003;$label='SeedLocal'}
    $h.Calls.Add($label);$h.Environments[$label]=$Environment
    $p=New-TestProcess $number $FilePath ($FilePath+' '+($ArgumentList -join ' '))
    if($number -eq 5003){$p.ExitCode=$h.SeedExit}
    foreach($path in @($RedirectStandardOutput,$RedirectStandardError)){if($path){''|Set-Content -LiteralPath $path}}
    return $p
}
function Write-TestRun($h){
    $app=Join-Path $h.Root '.local/runtime/existing/app';$null=New-Item -ItemType Directory -Path $app -Force
    $az=New-TestProcess 5001 $h.Tools.node ($h.Tools.node+' "'+$h.Tools.azurite+'" --blobHost 127.0.0.1 --queueHost 127.0.0.1 --tableHost 127.0.0.1')
    $hostProcess=New-TestProcess 5002 $h.Tools.func 'func start --address 127.0.0.1 --port 7071 --no-build'
    $state=@{schema='platform.local-run/v2';runId='fixture';root=$h.Root;processes=@((New-ProcessRecord $az),(New-ProcessRecord $hostProcess));app=$app;logs=(Join-Path $h.Root '.local/logs');ready=$true}
    $state|ConvertTo-Json -Depth 5|Set-Content (Join-Path $h.Root '.local/run.json');return $state
}
try{
    $h=New-Fixture
    Assert-Test ((Get-LocalPreparedBuild $h.Build).App -eq (Join-Path $h.Build 'blob-transfer')) 'Valid prepared closure rejected.'
    Reject-Test {Write-LocalBuildReceipt $h.Build}
    'changed'|Set-Content (Join-Path $h.Build 'transfer-tool/TransferTool.dll');Reject-Test {Get-LocalPreparedBuild $h.Build}
    $h=New-Fixture;'extra'|Set-Content (Join-Path $h.Build 'blob-transfer/extra');Reject-Test {Get-LocalPreparedBuild $h.Build}
    $h=New-Fixture;$path=Join-Path $h.Build 'local-runtime-build.json';$r=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable;$r.files[0].path='blob-transfer/../outside';$r|ConvertTo-Json -Depth 5|Set-Content $path;Reject-Test {Get-LocalPreparedBuild $h.Build}
    $h=New-Fixture;$path=Join-Path $h.Build 'local-runtime-build.json';$r=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable;$r.files+=,$r.files[0];$r|ConvertTo-Json -Depth 5|Set-Content $path;Reject-Test {Get-LocalPreparedBuild $h.Build}
    $h=New-Fixture
    $env:AZURE_CLIENT_SECRET='synthetic-secret';$env:OTEL_EXPORTER_OTLP_HEADERS='x-otlp-api-key=synthetic-key';$env:TRACEPARENT='00-11111111111111111111111111111111-2222222222222222-01';$env:TRACESTATE='private=value';$env:BAGGAGE='private=value'
    $emulator=Get-LocalChildEnvironment
    Assert-Test ($null -eq $emulator.AZURE_CLIENT_SECRET -and $null -eq $emulator.OTEL_EXPORTER_OTLP_HEADERS -and $null -eq $emulator.BAGGAGE) 'Emulator inherited credentials.'
    $worker=Get-LocalChildEnvironment -Telemetry
    Assert-Test ($worker.OTEL_EXPORTER_OTLP_HEADERS -eq $env:OTEL_EXPORTER_OTLP_HEADERS -and $worker.TRACEPARENT -eq $env:TRACEPARENT -and $null -eq $worker.TRACESTATE -and $null -eq $worker.AZURE_CLIENT_SECRET) 'Worker credential routing failed.'
    Assert-Test ((Get-LocalStatus).State -eq 'Stopped' -and $null -eq (Get-LocalStatus).Identity) 'Unregistered free profile was not stopped.'
    $h.Foreign=$true;Assert-Test ((Get-LocalStatus).State -eq 'UnownedListener') 'Foreign listener adopted.';Reject-Test {Assert-LocalPortsFree}
    $h.Foreign=$false;$h.Denied=$true;Assert-Test ((Get-LocalStatus).State -eq 'Unknown') 'Denied query reported free.';Reject-Test {Assert-LocalPortsFree}
    $h=New-Fixture;$s=Write-TestRun $h
    Assert-Test ((Get-LocalStatus).Healthy -and (Get-LocalStatus).Identity -match '^5001:\d+,5002:\d+$') 'Owned healthy profile or identity rejected.'
    $h.Health=$false;Assert-Test ((Get-LocalStatus).State -eq 'Unhealthy' -and (Get-LocalStatus).OwnershipVerified) 'Unavailable health incorrectly discarded verified ownership.';$h.Health=$true
    $purePath=$env:PATH;$null=Get-LocalStatus;$null=Get-LocalTools;Assert-Test ($env:PATH -eq $purePath) 'Read-only runtime/tooling inspection changed the caller environment.'
    $h.Processes.Clear();Assert-Test ((Get-LocalStatus).State -eq 'StaleReceipt') 'Stale receipt not distinguished.'
    $h=New-Fixture;$s=Write-TestRun $h;$h.Processes[5002].StartTime=$h.Processes[5002].StartTime.AddMinutes(1);Reject-Test {Stop-LocalOwnedRun $s};Assert-Test ($h.Calls.Count -eq 0) 'PID reuse allowed stop.'
    $h=New-Fixture;$s=Write-TestRun $h;$null=New-TestProcess 5004 'C:\fixture\foreign.exe' 'foreign' 5002;Reject-Test {Stop-LocalOwnedRun $s};Assert-Test ($h.Calls.Count -eq 0) 'Unknown child allowed partial stop.'
    $h=New-Fixture;$s=Write-TestRun $h;$h.MetadataDenied=$true;Reject-Test {Stop-LocalOwnedRun $s};Assert-Test ($h.Calls.Count -eq 0) 'Denied metadata allowed stop.'
    $h=New-Fixture;$s=Write-TestRun $h
    & (Join-Path $h.Root 'scripts/Stop-Local.ps1') -WhatIf
    Assert-Test ($h.Calls.Count -eq 0 -and (Test-Path (Join-Path $h.Root '.local/run.json'))) 'Stop WhatIf mutated.'
    & (Join-Path $h.Root 'scripts/Stop-Local.ps1') -Confirm:$false
    Assert-Test (($h.Calls -join ',') -eq 'Stop5002,Stop5001' -and !(Test-Path (Join-Path $h.Root '.local/run.json'))) 'Stop order or receipt cleanup failed.'
    Assert-Test ((Test-Path (Join-Path $h.Root '.local/data/sentinel')) -and (Test-Path (Join-Path $h.Root '.local/logs/sentinel'))) 'Persistent data/logs lost.'
    $h=New-Fixture;& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -WhatIf
    Assert-Test ($h.Calls.Count -eq 0 -and !(Test-Path (Join-Path $h.Root '.local/run.json'))) 'Start WhatIf mutated.'
    $h=New-Fixture;$beforePath=$env:PATH;$beforeHeader=$env:OTEL_EXPORTER_OTLP_HEADERS
    $status=& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false
    Assert-Test ($status.Healthy -and ($h.Calls -join ',') -eq 'CheckTools,StartTelemetry,UseTelemetry,StartAzurite,SeedLocal,StartFunctions') 'Dependency order or readiness failed.'
    Assert-Test ($env:PATH -eq $beforePath -and $env:OTEL_EXPORTER_OTLP_HEADERS -eq $beforeHeader) 'Caller environment changed.'
    Assert-Test ($null -eq $h.Environments.StartAzurite.OTEL_EXPORTER_OTLP_HEADERS -and $null -ne $h.Environments.StartFunctions.OTEL_EXPORTER_OTLP_HEADERS) 'Collector key reached emulator or did not reach worker.'
    Assert-Test (!(Test-Path (Join-Path $h.Build 'blob-transfer/local.settings.json')) -and $null -ne (Get-LocalPreparedBuild $h.Build)) 'Prepared bundle changed at startup.'
    $h=New-Fixture;$h.FailFunctions=$true;$h.FailCollectorStop=$true;$message='';$failureEnvironment=Get-TestEnvironment
    try{& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false|Out-Null}catch{$message=$_.Exception.Message}
    Assert-Test ($message -eq 'SYNTHETIC_FUNCTIONS_START' -and ($h.Calls -join ',') -eq 'CheckTools,StartTelemetry,UseTelemetry,StartAzurite,SeedLocal,Stop5001,StopTelemetry') 'Rollback masked startup failure or changed stop order.'
    Assert-Test (!(Test-Path (Join-Path $h.Root '.local/run.json')) -and (Test-Path (Join-Path $h.Root '.local/data/sentinel'))) 'Rollback lost data or retained false readiness.'
    Assert-TestEnvironment $failureEnvironment
    $h=New-Fixture;$h.FailReceiptWrite=$true;$message='';$failureEnvironment=Get-TestEnvironment
    try{& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false|Out-Null}catch{$message=$_.Exception.Message}
    Assert-Test ($message -eq 'SYNTHETIC_RECEIPT_WRITE' -and $h.Processes.Count -eq 0 -and ($h.Calls -join ',') -eq 'CheckTools,StartTelemetry,UseTelemetry,StartAzurite,Stop5001,StopTelemetry') 'Receipt-write failure left an owned producer running or masked its original error.'
    Assert-Test ((Test-Path (Join-Path $h.Root '.local/run.json')) -and (Test-Path (Join-Path $h.Root '.local/data/sentinel'))) 'Incomplete receipt or persistent data was removed after failed registration.'
    Assert-TestEnvironment $failureEnvironment
    foreach($scenario in @('PortalReady','PortalUnknown','PortalDenied','CollectorReplacement','CollectorUnverified','CollectorIdentityUnavailable')) {
        $h=New-Fixture;$h.FailFunctions=$true;$message=''
        switch($scenario) {
            PortalReady {$h.PortalState='Ready'}
            PortalUnknown {$h.PortalState='Unknown'}
            PortalDenied {$h.PortalStatusDenied=$true}
            CollectorReplacement {$h.ReplaceCollector=$true}
            CollectorUnverified {$h.CollectorUnverified=$true}
            CollectorIdentityUnavailable {$h.CollectorIdentity=$null}
        }
        try{& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false|Out-Null}catch{$message=$_.Exception.Message}
        Assert-Test ($message -eq 'SYNTHETIC_FUNCTIONS_START' -and $h.Processes.Count -eq 0 -and !$h.Calls.Contains('StopTelemetry') -and (Test-Path (Join-Path $h.Root '.local/observability/process.json'))) ('Shared or unverified collector was stopped: '+$scenario)
    }
    $h=New-Fixture;$h.FailFunctions=$true;$h.FailEnvironmentRestore=$true;$message=''
    if(Test-Path -LiteralPath 'Env:OTEL_EXPORTER_OTLP_ENDPOINT'){Microsoft.PowerShell.Management\Remove-Item -LiteralPath 'Env:OTEL_EXPORTER_OTLP_ENDPOINT'}
    $failureEnvironment=Get-TestEnvironment
    try{& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false|Out-Null}catch{$message=$_.Exception.Message}
    $released=$false;$probe=$null
    try{$probe=[IO.File]::Open((Join-Path $h.Root '.local/local-lifecycle.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$released=$true}finally{if($probe){$probe.Dispose()}}
    Assert-Test ($message -eq 'SYNTHETIC_ENVIRONMENT_RESTORE' -and $released) 'Environment restoration failure retained the lifecycle lock.'
    $h.FailEnvironmentRestore=$false
    foreach($name in $failureEnvironment.Keys) {
        if($failureEnvironment[$name].Exists){[Environment]::SetEnvironmentVariable($name,$failureEnvironment[$name].Value,'Process')}
        elseif(Test-Path -LiteralPath "Env:$name"){Microsoft.PowerShell.Management\Remove-Item -LiteralPath "Env:$name"}
    }
    Assert-TestEnvironment $failureEnvironment
    $h=New-Fixture;$h.FailFunctions=$true;$null=New-Item -ItemType Directory -Path (Join-Path $h.Root '.local/observability');'{"pid":7777}'|Set-Content (Join-Path $h.Root '.local/observability/process.json')
    try{& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -Confirm:$false|Out-Null}catch{}
    Assert-Test (!$h.Calls.Contains('StopTelemetry')) 'Rollback stopped an existing collector.'
    $h=New-Fixture;$null=& (Join-Path $h.Root 'scripts/Run-Local.ps1') -PreparedBuild $h.Build -SkipTelemetry -Confirm:$false
    Assert-Test (!$h.Calls.Contains('StartTelemetry') -and !$h.Calls.Contains('UseTelemetry')) 'Suite collector started twice.'
    [ordered]@{passed=$passed;scope='Synthetic lifecycle/build closure only; no real process or data mutation';date=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $base 'results.json')
    Write-Host "PASS: $passed local lifecycle scenarios. Evidence: $base/results.json"
}finally{
    foreach($key in $saved.Keys){
        if($null -eq $saved[$key]){if(Test-Path -LiteralPath "Env:$key"){Remove-Item -LiteralPath "Env:$key"}}
        else{[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}
    }
    Remove-Variable LocalRuntimeFixture -Scope Global -ErrorAction SilentlyContinue
}

