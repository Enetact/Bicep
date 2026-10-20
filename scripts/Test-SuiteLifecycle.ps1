#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$base=Join-Path $root ('artifacts/suite-lifecycle-tests/'+[guid]::NewGuid().ToString('N'))
if(Get-Variable SuiteLifecycleFixture -Scope Global -ErrorAction SilentlyContinue){throw 'Suite fixture variable already exists.'}
$savedPort=$env:STUDIO_Portal__Port
$savedNative=Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$environmentNames=@('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS','PATH','DOTNET_ROOT',
    'DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE')
$savedEnvironment=@{}
foreach($name in $environmentNames){$savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$passed=0
$cases=[Collections.Generic.List[string]]::new()

function Assert-SuiteTest([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject-SuiteTest([scriptblock]$Action,[string]$Expected=''){
    $failure=$null
    try{& $Action|Out-Null}catch{$failure=$_}
    Assert-SuiteTest ($null -ne $failure) 'Expected suite lifecycle rejection.'
    if($Expected){Assert-SuiteTest ($failure.Exception.Message -match [regex]::Escape($Expected)) "Wrong rejection; expected $Expected."}
}
function Complete-SuiteTest([string]$Name){$script:passed++;$script:cases.Add($Name)}
function Get-SuiteTestCalls {(@($global:SuiteLifecycleFixture.Calls)|Where-Object {$_ -ne 'StateDirectory'}) -join ','}
function Set-SuiteTestRunning {
    $h=$global:SuiteLifecycleFixture
    $h.PortalState='Ready';$h.PortalPid=4321;$h.TelemetryState='Running';$h.TelemetryPid=4322
    $h.PortalIdentity='4321:100001';$h.TelemetryIdentity='4322:100002'
}
function Set-SuiteTestLocal([bool]$Running){
    $h=$global:SuiteLifecycleFixture
    $h.LocalState=if($Running){'Ready'}else{'Stopped'}
    $h.LocalIdentity=if($Running){'synthetic-local-run'}else{$null}
    $path=Join-Path $h.Root '.local/run.json'
    if($Running){[IO.File]::WriteAllText($path,'{"synthetic":true}')}
    elseif(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
}
function New-SuiteTestCheckpoint {
    $path=Join-Path $global:SuiteLifecycleFixture.Root '.local/portal/credential-cache/5087/restart.bin'
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force|Out-Null
    [IO.File]::WriteAllText($path,'synthetic encrypted checkpoint marker; no credentials')
}
function Invoke-SuiteTest([hashtable]$Parameters=@{}){
    $before=@{}
    foreach($name in $script:environmentNames){$before[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
    try{& $global:SuiteLifecycleFixture.Entry @Parameters}
    finally{
        foreach($name in $script:environmentNames){
            Assert-SuiteTest ([Environment]::GetEnvironmentVariable($name,'Process') -ceq $before[$name]) "Caller environment was not restored: $name."
        }
    }
}
function Start-Process {
    [CmdletBinding()]param([string]$FilePath)
    $h=$global:SuiteLifecycleFixture
    Assert-SuiteTest ($FilePath -eq (Join-Path $h.Root 'Dashboard.url')) 'Unexpected synthetic browser destination.'
    $h.Calls.Add('Browser')
}
function New-SuiteTestFixture {
    $folder=Join-Path $base ([guid]::NewGuid().ToString('N'))
    $global:SuiteLifecycleFixture=@{Root=$folder;Calls=[Collections.Generic.List[string]]::new();PortalState='Stopped';PortalPid=$null;PortalIdentity=$null;
        PortalTelemetry=$true;TelemetryState='Stopped';TelemetryPid=$null;TelemetryIdentity=$null;TelemetryHealthy=$true;LocalState='Stopped';LocalIdentity=$null}
    $h=$global:SuiteLifecycleFixture
    foreach($flag in @('FailStatusAfterStart','StartAttempted','FailBuild','ReplacePortalDuringBuild','ReplaceCollectorDuringBuild','ReplaceLocalDuringBuild',
        'ReplacePortalIdentityDuringBuild','ReplaceCollectorIdentityDuringBuild','FailCollectorStart','ConcurrentCollectorBeforeStart','ReplaceCollectorDuringSetup','ReplaceCollectorIdentityDuringSetup','FailTelemetryImport','FailCollectorStop','FailSetup','FailPortalBeforeStart','UnknownPortalAfterStart','UnhealthyPortalAfterStart',
        'FailPortalAfterStart','FailPortalStop','PortalStopNoop','FailResume','FailLocalPrerequisite','FailLocalStart','FailLocalStop','LocalStopNoop')){$h[$flag]=$false}
    foreach($directory in @('scripts','.local/portal/credential-cache/5087','.local/suite','config')){New-Item -ItemType Directory -Path (Join-Path $folder $directory) -Force|Out-Null}
    foreach($name in @('Manage-Suite.ps1','Suite.Common.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $folder ('scripts/'+$name))}
    # Keep production status, profile filtering and environment restoration. Only the ACL writer is replaced.
    @'

function New-SuitePrivateDirectory([string]$Path){
    $full=Assert-SuitePath $Path
    $global:SuiteLifecycleFixture.Calls.Add('StateDirectory')
    New-Item -ItemType Directory -Path $full -Force|Out-Null
    $full
}
'@|Add-Content -LiteralPath (Join-Path $folder 'scripts/Suite.Common.ps1')
    @'
$h=$global:SuiteLifecycleFixture
if($h.FailStatusAfterStart -and $h.StartAttempted){throw 'Synthetic status inspection failed.'}
[pscustomobject]@{State=$h.PortalState;ProcessId=$h.PortalPid;Identity=$h.PortalIdentity;TelemetryConfigured=[bool]$h.PortalTelemetry;Healthy=($h.PortalState -eq 'Ready')}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Get-Portal.ps1')
    @'
$h=$global:SuiteLifecycleFixture
[pscustomobject]@{State=$h.TelemetryState;ProcessId=$h.TelemetryPid;Identity=$h.TelemetryIdentity;Healthy=[bool]($h.TelemetryState -eq 'Running' -and $h.TelemetryHealthy);shortcutPath=(Join-Path $h.Root 'Dashboard.url')}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Get-Observability.ps1')
    @'
$h=$global:SuiteLifecycleFixture
[pscustomobject]@{State=$h.LocalState;Identity=$h.LocalIdentity}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Get-Local.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param()
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('Build')
if($h.FailBuild){throw 'Synthetic suite build failure.'}
if($h.ReplacePortalDuringBuild){$h.PortalPid=8765;$h.PortalIdentity='8765:200001'}
if($h.ReplaceCollectorDuringBuild){$h.TelemetryPid=8766;$h.TelemetryIdentity='8766:200002'}
if($h.ReplacePortalIdentityDuringBuild){$h.PortalIdentity='4321:200001'}
if($h.ReplaceCollectorIdentityDuringBuild){$h.TelemetryIdentity='4322:200002'}
if($h.ReplaceLocalDuringBuild){$h.LocalIdentity='replacement-local-run'}
$output=Join-Path $h.Root 'prepared-build';New-Item -ItemType Directory -Path $output -Force|Out-Null
[pscustomobject]@{State='Built';BuildRoot=$output;ProjectCount=6}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Build-Suite.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param([switch]$NoBrowser,[switch]$InstallMissing)
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('StartTelemetry')
if($h.FailCollectorStart){throw 'Synthetic collector start failure.'}
if($h.ConcurrentCollectorBeforeStart){$h.TelemetryState='Running';$h.TelemetryPid=7654;$h.TelemetryIdentity='7654:300001'}
$started=$h.TelemetryState -ne 'Running'
if($started){$h.TelemetryPid=6543;$h.TelemetryIdentity='6543:300002'}
$h.TelemetryState='Running'
$result=& (Join-Path $PSScriptRoot 'Get-Observability.ps1')
$result|Add-Member -NotePropertyName StartedByInvocation -NotePropertyValue $started
$result
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Start-Observability.ps1')
    @'
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('UseTelemetry')
$env:OTEL_EXPORTER_OTLP_ENDPOINT='http://127.0.0.1:4318'
$env:OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf'
$env:OTEL_EXPORTER_OTLP_HEADERS='x-otlp-api-key=synthetic-key-not-a-credential'
if($h.FailTelemetryImport){throw 'Synthetic telemetry import failure.'}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Use-Observability.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param()
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('StopTelemetry')
if($h.FailCollectorStop){throw 'Synthetic collector stop failure.'}
if($h.PortalState -notin @('Stopped','StaleReceipt') -or (Test-Path -LiteralPath (Join-Path $h.Root '.local/run.json'))){throw 'Collector stopped before an owned producer.'}
$h.TelemetryState='Stopped';$h.TelemetryPid=$null;$h.TelemetryIdentity=$null
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Stop-Observability.ps1')
    @'
[CmdletBinding()]param([switch]$PrerequisitesOnly,[switch]$InstallMissing)
$h=$global:SuiteLifecycleFixture
$h.Calls.Add($(if($PrerequisitesOnly){'InstallPortal'}else{'SetupPortal'}))
$env:PATH='synthetic-path';$env:DOTNET_ROOT='synthetic-sdk-root'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='synthetic-sdk-optout';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='synthetic-first-time'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='synthetic-certificate-setting'
if($h.ReplaceCollectorDuringSetup){$h.TelemetryPid=7655;$h.TelemetryIdentity='7655:400001'}
if($h.ReplaceCollectorIdentityDuringSetup){$h.TelemetryIdentity='6543:400002'}
if($h.FailSetup){throw 'Synthetic portal setup failure.'}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Setup-Portal.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param([switch]$SkipTelemetry,[switch]$NoBrowser)
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('StartPortal');$h.StartAttempted=$true
if(!$SkipTelemetry -or $env:OTEL_EXPORTER_OTLP_HEADERS -ne 'x-otlp-api-key=synthetic-key-not-a-credential') {throw 'Portal did not receive ready telemetry.'}
if($h.FailPortalBeforeStart){throw 'Synthetic portal start failure.'}
$h.PortalPid=6544;$h.PortalIdentity='6544:300003'
$h.PortalState=if($h.UnknownPortalAfterStart){'Unknown'}elseif($h.UnhealthyPortalAfterStart){'Unhealthy'}else{'Ready'}
if($h.FailPortalAfterStart){throw 'Synthetic portal started then failed readiness.'}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Start-Portal.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param([switch]$DiscardConnections)
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('DiscardPortal')
if(!$DiscardConnections){throw 'A preserving stop was routed through destructive cleanup.'}
if($h.FailPortalStop){throw 'Synthetic portal stop failure.'}
$h.PortalState='Stopped';$h.PortalPid=$null;$h.PortalIdentity=$null
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Stop-Portal.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param([switch]$CheckpointOnly,[switch]$ResumePending,[switch]$NoBrowser)
$h=$global:SuiteLifecycleFixture
$checkpoint=Join-Path $h.Root '.local/portal/credential-cache/5087/restart.bin'
if($CheckpointOnly){
    $h.Calls.Add('CheckpointPortal')
    if($h.FailPortalStop){throw 'Synthetic portal stop failure.'}
    if($h.PortalStopNoop){return}
    $h.PortalState='Stopped';$h.PortalPid=$null;$h.PortalIdentity=$null
    [IO.File]::WriteAllText($checkpoint,'synthetic encrypted checkpoint marker; no credentials')
    return
}
if(!$ResumePending -or !(Test-Path -LiteralPath $checkpoint)){throw 'Resume requires the existing checkpoint.'}
$h.Calls.Add('ResumePortal')
if($env:OTEL_EXPORTER_OTLP_HEADERS -ne 'x-otlp-api-key=synthetic-key-not-a-credential'){throw 'Resume did not receive telemetry.'}
if($h.FailResume){throw 'Synthetic resume failure.'}
$h.PortalState='Ready';$h.PortalPid=6544;$h.PortalIdentity='6544:300003'
Remove-Item -LiteralPath $checkpoint
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Restart-PortalFresh.ps1')
    @'
[CmdletBinding()]param([switch]$CheckOnly,[switch]$WriteReceipt)
$h=$global:SuiteLifecycleFixture;$h.Calls.Add($(if($CheckOnly){'CheckLocal'}else{'InstallLocal'}))
$env:PATH='synthetic-local-path';$env:DOTNET_ROOT='synthetic-local-sdk-root'
$env:FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT='synthetic-functions-optout'
if($h.FailLocalPrerequisite){throw 'Synthetic local prerequisites failure.'}
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Setup-Local.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param([string]$PreparedBuild,[switch]$SkipTelemetry)
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('StartLocal')
if(!$SkipTelemetry -or $PreparedBuild -ne (Join-Path $h.Root 'prepared-build')){throw 'Full runtime did not receive the prepared suite build.'}
if($env:OTEL_EXPORTER_OTLP_HEADERS -ne 'x-otlp-api-key=synthetic-key-not-a-credential'){throw 'Local runtime did not receive telemetry.'}
if($h.FailLocalStart){throw 'Synthetic local runtime start failure.'}
$h.LocalState='Ready';$h.LocalIdentity='synthetic-local-run'
[IO.File]::WriteAllText((Join-Path $h.Root '.local/run.json'),'{}')
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Run-Local.ps1')
    @'
[CmdletBinding(SupportsShouldProcess)]param()
$h=$global:SuiteLifecycleFixture;$h.Calls.Add('StopLocal')
if($h.FailLocalStop){throw 'Synthetic local runtime stop failure.'}
if($h.LocalStopNoop){return}
$h.LocalState='Stopped';$h.LocalIdentity=$null
Remove-Item -LiteralPath (Join-Path $h.Root '.local/run.json') -ErrorAction SilentlyContinue
'@|Set-Content -LiteralPath (Join-Path $folder 'scripts/Stop-Local.ps1')
    $h.Entry=Join-Path $folder 'scripts/Manage-Suite.ps1'
    $h
}

try {
    $env:STUDIO_Portal__Port='5087'
    foreach($name in $environmentNames|Where-Object {$_ -like 'OTEL_*'}){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
    $h=New-SuiteTestFixture
    $result=Invoke-SuiteTest
    Assert-SuiteTest ($result.State -eq 'Stopped' -and $h.Calls.Count -eq 0 -and $result.Components.Count -eq 6) 'Default status mutated state or omitted maintained executables.'
    Complete-SuiteTest 'Default read-only status and six executable entries'

    foreach($action in @('Build','Start','Stop','Restart')){
        $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true
        $before=@(Get-ChildItem -LiteralPath $h.Root -Recurse -File|ForEach-Object {($_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash)}) -join "`n"
        Invoke-SuiteTest @{Action=$action;WhatIf=$true;Profile='Full';NoBrowser=$true}|Out-Null
        $after=@(Get-ChildItem -LiteralPath $h.Root -Recurse -File|ForEach-Object {($_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash)}) -join "`n"
        Assert-SuiteTest ($h.Calls.Count -eq 0 -and $before -ceq $after) 'WhatIf installed, built, changed keys/checkpoints or controlled a process.'
        Complete-SuiteTest "WhatIf $action is non-mutating"
    }

    $h=New-SuiteTestFixture
    $result=Invoke-SuiteTest @{Action='Start';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and (Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,SetupPortal,StartPortal') 'Studio startup failed collector-first order.'
    Complete-SuiteTest 'Studio collector-first startup'

    $h=New-SuiteTestFixture
    $result=Invoke-SuiteTest @{Action='Start';Profile='Full';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and (Get-SuiteTestCalls) -eq 'CheckLocal,Build,StartTelemetry,UseTelemetry,StartLocal,SetupPortal,StartPortal') 'Full startup missed preflight/prepared build or collector-first order.'
    Complete-SuiteTest 'Full prepared build and collector-first runtime startup'

    $h=New-SuiteTestFixture
    Invoke-SuiteTest @{Action='Start';Profile='Full';InstallMissing=$true;NoBrowser=$true}|Out-Null
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'InstallPortal,InstallLocal,Build,StartTelemetry,UseTelemetry,StartLocal,SetupPortal,StartPortal') 'InstallMissing did not remain an explicit prerequisite phase.'
    Complete-SuiteTest 'Explicit prerequisite installation before build'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning
    $result=Invoke-SuiteTest @{Action='Start';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and (Get-SuiteTestCalls) -eq '') 'Idempotent Start rebuilt or replaced an already healthy suite.'
    Complete-SuiteTest 'Healthy Start is idempotent'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true
    Invoke-SuiteTest @{Action='Stop'}|Out-Null
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'CheckpointPortal,StopLocal,StopTelemetry' -and (Test-Path -LiteralPath (Join-Path $h.Root '.local/portal/credential-cache/5087/restart.bin'))) 'Default stop failed preservation or producer-before-collector order.'
    Complete-SuiteTest 'Preserving Stop shuts registered producers before sink'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning
    Invoke-SuiteTest @{Action='Stop';DiscardConnections=$true}|Out-Null
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'DiscardPortal,StopTelemetry' -and !(Test-Path -LiteralPath (Join-Path $h.Root '.local/portal/credential-cache/5087/restart.bin'))) 'Explicit DiscardConnections did not select cleanup.'
    Complete-SuiteTest 'DiscardConnections is explicit'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true
    $result=Invoke-SuiteTest @{Action='Restart';Profile='Full';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and (Get-SuiteTestCalls) -eq 'CheckLocal,Build,CheckpointPortal,StopLocal,StopTelemetry,StartTelemetry,UseTelemetry,StartLocal,ResumePortal') 'Full restart did not build before stop or restore before readiness.'
    Complete-SuiteTest 'Full Restart preflight preservation dependency ordering'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true
    Invoke-SuiteTest @{Action='Restart';NoBrowser=$true}|Out-Null
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,CheckpointPortal,StopLocal,StopTelemetry,StartTelemetry,UseTelemetry,ResumePortal' -and $h.LocalState -eq 'Stopped') 'Studio restart unexpectedly started optional Functions.'
    Complete-SuiteTest 'Studio Restart leaves optional local runtime stopped'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;$h.FailBuild=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';NoBrowser=$true}} 'Synthetic suite build failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build' -and $h.PortalState -eq 'Ready' -and $h.TelemetryState -eq 'Running') 'Build failure stopped a running app.'
    Complete-SuiteTest 'Build failure preserves running services'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';InstallMissing=$true;NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'InstallPortal' -and $h.PortalState -eq 'Ready' -and $h.TelemetryState -eq 'Running') 'Prerequisite failure built or stopped a service.'
    Complete-SuiteTest 'Portal prerequisite failure restores environment before any stop'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;$h.FailLocalPrerequisite=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';Profile='Full';NoBrowser=$true}} 'Synthetic local prerequisites failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'CheckLocal' -and $h.PortalState -eq 'Ready' -and $h.TelemetryState -eq 'Running') 'Local prerequisite failure built or stopped a service.'
    Complete-SuiteTest 'Local prerequisite failure restores environment before any stop'

    foreach($flag in @('ReplacePortalDuringBuild','ReplaceCollectorDuringBuild','ReplaceLocalDuringBuild','ReplacePortalIdentityDuringBuild','ReplaceCollectorIdentityDuringBuild')){
        $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true;$h[$flag]=$true
        Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';Profile='Full';NoBrowser=$true}} 'ownership changed during build'
        Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'CheckLocal,Build') 'Ownership drift permitted process mutation.'
        Complete-SuiteTest "Ownership drift blocked: $flag"
    }

    foreach($flag in @('FailPortalStop','FailLocalStop','PortalStopNoop','LocalStopNoop')){
        $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true;$h[$flag]=$true
        Reject-SuiteTest {Invoke-SuiteTest @{Action='Stop';Profile='Full'}}
        Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running') 'Producer stop failure removed the sink.'
        Complete-SuiteTest "Sink retained after producer stop failure: $flag"
    }

    $h=New-SuiteTestFixture;New-SuiteTestCheckpoint
    $result=Invoke-SuiteTest @{Action='Start';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and (Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,ResumePortal') 'Start did not automatically resume existing checkpoint.'
    Complete-SuiteTest 'Start detects pending checkpoint'

    $h=New-SuiteTestFixture
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';ResumePending=$true;NoBrowser=$true}} 'existing encrypted checkpoint'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq '') 'Invalid ResumePending mutated processes.'
    Complete-SuiteTest 'Explicit ResumePending requires stopped checkpoint'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;New-SuiteTestCheckpoint
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';ResumePending=$true;NoBrowser=$true}} 'existing encrypted checkpoint'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq '') 'ResumePending replaced a running session.'
    Complete-SuiteTest 'ResumePending cannot take over running session'

    $h=New-SuiteTestFixture;$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,SetupPortal,StopTelemetry' -and $h.TelemetryState -eq 'Stopped') 'Failed cold startup did not clean only its new collector.'
    Complete-SuiteTest 'Cold startup rollback cleans newly started sink'

    $h=New-SuiteTestFixture;$h.TelemetryState='Running';$h.TelemetryPid=4322;$h.TelemetryIdentity='4322:100002';$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running') 'Rollback stopped a collector that predated this invocation.'
    Complete-SuiteTest 'Rollback preserves preexisting collector'

    $h=New-SuiteTestFixture;$h.ConcurrentCollectorBeforeStart=$true;$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running' -and $h.TelemetryPid -eq 7654) 'Rollback stopped a concurrently reused collector.'
    Complete-SuiteTest 'Rollback preserves concurrently reused collector'

    $h=New-SuiteTestFixture;$h.ReplaceCollectorDuringSetup=$true;$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running' -and $h.TelemetryPid -eq 7655) 'Rollback stopped a replacement collector with a different identity.'
    Complete-SuiteTest 'Rollback verifies collector identity before cleanup'

    $h=New-SuiteTestFixture;$h.ReplaceCollectorIdentityDuringSetup=$true;$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal setup failure'
    Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running' -and $h.TelemetryPid -eq 6543 -and $h.TelemetryIdentity -eq '6543:400002') 'Rollback stopped a same-PID collector replacement.'
    Complete-SuiteTest 'Rollback verifies start identity when collector PID is reused'

    $h=New-SuiteTestFixture;$h.FailPortalBeforeStart=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic portal start failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,SetupPortal,StartPortal,StopTelemetry' -and $h.TelemetryState -eq 'Stopped') 'Failed portal start retained a collector with no producer.'
    Complete-SuiteTest 'Portal start failure cleans only newly started sink'

    $h=New-SuiteTestFixture;New-SuiteTestCheckpoint;$h.FailResume=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';ResumePending=$true;NoBrowser=$true}} 'Synthetic resume failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,ResumePortal,StopTelemetry' -and (Test-Path -LiteralPath (Join-Path $h.Root '.local/portal/credential-cache/5087/restart.bin'))) 'Resume failure discarded its checkpoint or retained a new unused sink.'
    Complete-SuiteTest 'Failed resume retains checkpoint for recovery'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;$h.FailCollectorStop=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';NoBrowser=$true}} 'Synthetic collector stop failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,CheckpointPortal,StopTelemetry' -and $h.PortalState -eq 'Stopped' -and $h.TelemetryState -eq 'Running') 'Collector stop failure restarted producers or discarded the preserved session.'
    Complete-SuiteTest 'Collector stop failure halts restart with preserved session'

    foreach($flag in @('FailPortalAfterStart','UnknownPortalAfterStart','FailStatusAfterStart')){
        $h=New-SuiteTestFixture;$h[$flag]=$true
        Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}}
        Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.TelemetryState -eq 'Running') 'Rollback removed telemetry while a producer might still be running.'
        Complete-SuiteTest "Rollback keeps sink for running or unknown producer: $flag"
    }

    $h=New-SuiteTestFixture;$h.FailTelemetryImport=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic telemetry import failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,StopTelemetry') 'Telemetry import failure started a producer or skipped safe rollback.'
    Complete-SuiteTest 'Import failure restores environment and rolls back new sink'

    $h=New-SuiteTestFixture;$h.FailCollectorStart=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Synthetic collector start failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry') 'Collector startup failure started a producer.'
    Complete-SuiteTest 'Collector must be ready before any producer'

    $h=New-SuiteTestFixture;$h.FailLocalStart=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';Profile='Full';NoBrowser=$true}} 'Synthetic local runtime start failure'
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'CheckLocal,Build,StartTelemetry,UseTelemetry,StartLocal,StopTelemetry') 'Local startup failure started portal or retained unneeded new collector.'
    Complete-SuiteTest 'Local startup failure stops before portal'

    $h=New-SuiteTestFixture;Set-SuiteTestRunning;Set-SuiteTestLocal $true;$h.PortalState='Unhealthy';$h.FailSetup=$true
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';Profile='Full';NoBrowser=$true}}
    Assert-SuiteTest (!$h.Calls.Contains('StopTelemetry') -and $h.LocalState -eq 'Ready') 'Rollback removed a preexisting local producer or its sink.'
    Complete-SuiteTest 'Failed start keeps existing local producer and sink'

    foreach($state in @('Unknown','OwnershipMismatch','UnownedListener')){
        $h=New-SuiteTestFixture;$h.PortalState=$state
        Reject-SuiteTest {Invoke-SuiteTest @{Action='Restart';NoBrowser=$true}} 'Suite lifecycle blocked'
        Assert-SuiteTest ($h.Calls.Count -eq 0) 'Unverified portal ownership permitted mutation.'
        Complete-SuiteTest "Portal ownership blocks mutations: $state"
    }

    $h=New-SuiteTestFixture;$h.LocalState='UnownedListener'
    $result=Invoke-SuiteTest @{Action='Start';NoBrowser=$true}
    Assert-SuiteTest ($result.Healthy -and !$h.Calls.Contains('StartLocal') -and !$h.Calls.Contains('StopLocal')) 'Studio adopted or blocked on another unregistered emulator.'
    Complete-SuiteTest 'Studio ignores foreign unselected local ports'

    $h=New-SuiteTestFixture;$h.LocalState='UnownedListener'
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';Profile='Full';NoBrowser=$true}} 'Suite lifecycle blocked'
    Assert-SuiteTest ($h.Calls.Count -eq 0) 'Full adopted foreign emulator ports.'
    Complete-SuiteTest 'Full rejects foreign local ports'

    $h=New-SuiteTestFixture;Set-SuiteTestLocal $true;$h.LocalState='Unknown'
    Reject-SuiteTest {Invoke-SuiteTest @{Action='Stop'}} 'Suite lifecycle blocked'
    Assert-SuiteTest ($h.Calls.Count -eq 0) 'Studio ignored its registered runtime ownership failure.'
    Complete-SuiteTest 'Registered local producer must be verified even for Studio'

    $h=New-SuiteTestFixture
    $held=[IO.File]::Open((Join-Path $h.Root '.local/suite/lifecycle.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Reject-SuiteTest {Invoke-SuiteTest @{Action='Start';NoBrowser=$true}} 'Another suite lifecycle operation'}finally{$held.Dispose()}
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq '') 'Lock contention allowed lifecycle actions.'
    $result=Invoke-SuiteTest @{Action='Start';NoBrowser=$true}
    Assert-SuiteTest $result.Healthy 'Failed operation leaked the lifecycle lock.'
    Complete-SuiteTest 'Exclusive lock contention and release'

    $h=New-SuiteTestFixture;$global:LASTEXITCODE=37
    $result=Invoke-SuiteTest @{Action='Build'}
    Assert-SuiteTest ($result.State -eq 'Built' -and (Get-SuiteTestCalls) -eq 'Build') 'Inherited native exit code was treated as PowerShell helper failure.'
    Complete-SuiteTest 'PowerShell helper does not inherit native failure'

    $h=New-SuiteTestFixture
    Invoke-SuiteTest @{Action='Start'}|Out-Null
    Assert-SuiteTest ((Get-SuiteTestCalls) -eq 'Build,StartTelemetry,UseTelemetry,SetupPortal,StartPortal,Browser') 'Browser opened before verified readiness or with unexpected destination.'
    Complete-SuiteTest 'Browser opens only after verified suite readiness'

    foreach($parameters in @(@{Action='Build';InstallMissing=$true},@{Action='Stop';ResumePending=$true},@{Action='Start';DiscardConnections=$true},@{Action='Restart';DiscardConnections=$true;ResumePending=$true})){
        $h=New-SuiteTestFixture
        Reject-SuiteTest {Invoke-SuiteTest $parameters}
        Assert-SuiteTest ($h.Calls.Count -eq 0) 'Invalid option combination invoked a lifecycle action.'
        Complete-SuiteTest ('Invalid combination rejected: '+(($parameters.Keys|Sort-Object)-join '+'))
    }

    [ordered]@{passed=$passed;cases=@($cases);scope='Synthetic copied production suite coordinator/status with injected leaf services; no real process, credentials, cloud, package restore or network controlled';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $base 'results.json') -Encoding utf8NoBOM
    Write-Host "PASS: $passed synthetic suite lifecycle scenarios. Evidence: $base/results.json"
}
finally {
    $env:STUDIO_Portal__Port=$savedPort
    foreach($name in $environmentNames){
        if($null -eq $savedEnvironment[$name]){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}
        else{[Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process')}
    }
    if($null -eq $savedNative){Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue}else{$global:LASTEXITCODE=$savedNative.Value}
    Remove-Variable SuiteLifecycleFixture -Scope Global -ErrorAction SilentlyContinue
}
