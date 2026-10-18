#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$base=Join-Path $root ('artifacts/portal-fresh-restart-tests/'+[guid]::NewGuid().ToString('N'))
if(Get-Variable PortalFreshFixture -Scope Global -ErrorAction SilentlyContinue){throw 'Fresh restart fixture variable already exists.'}
$savedPort=$env:STUDIO_Portal__Port
$env:STUDIO_Portal__Port='5087'
$passed=0
function Assert-FreshTest([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject-FreshTest([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Assert-FreshTest $rejected 'Expected fresh-restart rejection.'}
function New-FreshTestProcess([int]$Number,[string]$Executable,[int]$Parent,[string]$Command){
    $h=$global:PortalFreshFixture
    $process=[pscustomobject]@{Id=$Number;Path=$Executable;StartTime=$h.Start.AddSeconds($Number-4321);Parent=$Parent;Command=$Command}
    $process|Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($Budget) -not $global:PortalFreshFixture.Processes.ContainsKey($this.Id)}
    $process|Add-Member -MemberType ScriptMethod -Name Kill -Value {param($Tree) if(!$Tree){throw 'MCP fixture expected descendant termination.'};$global:PortalFreshFixture.Calls.Add('StopMcpTree');$global:PortalFreshFixture.Processes.Remove($this.Id)}
    $h.Processes[$Number]=$process
    $process
}
function Get-NetTCPConnection {
    [CmdletBinding()]param($State)
    $h=$global:PortalFreshFixture
    $h.ListenerChecks++
    if($h.LateColdRegistration -and $h.ListenerChecks -eq 3){Add-FreshTestRegistration}
    if($h.Denied){throw 'Synthetic listener access denied.'}
    if($h.Foreign){[pscustomobject]@{LocalPort=5087;OwningProcess=9999}}
    elseif($h.Processes.ContainsKey($h.MainPid)){[pscustomobject]@{LocalPort=5087;OwningProcess=$h.MainPid}}
}
function Get-Process {[CmdletBinding()]param($Id) $global:PortalFreshFixture.Processes[[int]$Id]}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,$Filter)
    $h=$global:PortalFreshFixture
    if($h.MetadataDenied){throw 'Synthetic process inspection denied.'}
    if($Filter -match '^ParentProcessId=(\d+)$'){
        $parent=[int]$Matches[1]
        foreach($process in $h.Processes.Values|Where-Object Parent -eq $parent){[pscustomobject]@{ProcessId=$process.Id;ParentProcessId=$process.Parent;Name=[IO.Path]::GetFileName($process.Path);ExecutablePath=$process.Path;CreationDate=if($h.ConsoleMissingDate -and $process.Path -like '*conhost.exe'){$null}elseif($h.ConsolePredates -and $process.Path -like '*conhost.exe'){$h.Start.AddDays(-1)}else{$process.StartTime}}}
        return
    }
    $number=[int]($Filter -replace '^ProcessId=','')
    $process=$h.Processes[$number]
    [pscustomobject]@{CommandLine=if($h.HiddenMetadata){$null}else{$process.Command};ParentProcessId=if($h.WrongParent -and $number -ne $h.MainPid){9999}else{$process.Parent}}
}
function Get-Item {
    [CmdletBinding()]param([string]$LiteralPath,[switch]$Force)
    if($global:PortalFreshFixture.LinkedPath -and [IO.Path]::GetFullPath($LiteralPath) -eq $global:PortalFreshFixture.LinkedPath){return [pscustomobject]@{Attributes=[IO.FileAttributes]::ReparsePoint;Length=1}}
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}
function Stop-Process {
    [CmdletBinding()]param($InputObject,[switch]$Force)
    $h=$global:PortalFreshFixture
    $label=if($InputObject.Id -eq $h.MainPid){'StopMain'}else{'StopChild'}
    $h.Calls.Add($label)
    if(($label -eq 'StopMain' -and $h.StopNoop) -or ($label -eq 'StopChild' -and $h.ChildStopNoop)){return}
    if($label -eq 'StopMain' -and $h.LateKnownWriter){
        $directory=Join-Path $h.Root ('.local/portal/mcp/'+('4'*32)+'/ado');New-Item -ItemType Directory -Path $directory -Force|Out-Null
        $child=New-FreshTestProcess 4324 'C:\fixture\node.exe' $h.MainPid 'node synthetic-ado-mcp'
        @{schema='platform.mcp-process/v1';provider='ado';pid=$child.Id;parentPid=$h.MainPid;parentStartedUtc=$h.Start.ToUniversalTime().ToString('O');executable=$child.Path;startedUtc=$child.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $directory 'process.json')
    }
    if($label -eq 'StopMain' -and $h.LateUnknownWriter){[void](New-FreshTestProcess 4325 'C:\fixture\unregistered.exe' $h.MainPid 'unregistered child')}
    $h.Processes.Remove($InputObject.Id)
    if($label -eq 'StopChild' -and $InputObject.Id -eq 4323 -and $h.StaleAfterWriters){Add-FreshTestRegistration;$h.Processes.Remove(8765);$h.Processes.Remove(8766)}
}
function Invoke-RestMethod {
    [CmdletBinding()]param($Uri,$TimeoutSec,$MaximumRedirection)
    $h=$global:PortalFreshFixture
    [pscustomobject]@{products=if($h.BadHealth){@('synthetic-a')}else{@('synthetic-a','synthetic-b')}}
}
function Start-Process {
    [CmdletBinding()]param($FilePath,$WindowStyle)
    $h=$global:PortalFreshFixture
    Assert-FreshTest ($WindowStyle -eq 'Hidden' -and $FilePath -ceq ('http://localhost:5087/#resume='+$h.Proof)) 'Browser did not receive the private resume handoff.'
    $h.Calls.Add('Browser')
}
function git {$global:LASTEXITCODE=0;@('config/workloads.json','scripts/Restart-PortalFresh.ps1','src/SelfService.Portal/SelfService.Portal.csproj')}
function Add-FreshTestRegistration {
    $h=$global:PortalFreshFixture
    $main=New-FreshTestProcess 8765 'C:\fixture\dotnet.exe' 0 ('dotnet "'+$h.Assembly+'"');$h.MainPid=8765
    @{pid=$main.Id;executable=$main.Path;assembly=$h.Assembly;startedUtc=$main.StartTime.ToUniversalTime().ToString('O');url='http://localhost:5087'}|ConvertTo-Json|Set-Content $h.Receipt
    $directory=Join-Path $h.Root ('.local/portal/mcp/'+('5'*32)+'/ado');New-Item -ItemType Directory -Path $directory -Force|Out-Null
    $child=New-FreshTestProcess 8766 'C:\fixture\node.exe' $main.Id 'node synthetic-ado-mcp'
    @{schema='platform.mcp-process/v1';provider='ado';pid=$child.Id;parentPid=$main.Id;parentStartedUtc=$main.StartTime.ToUniversalTime().ToString('O');executable=$child.Path;startedUtc=$child.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $directory 'process.json')
}
function dotnet {
    $h=$global:PortalFreshFixture;$arguments=@($args);$global:LASTEXITCODE=0
    if($arguments[0] -eq 'build'){
        $index=[array]::IndexOf($arguments,'--artifacts-path')
        if($index -ge 0){
            $h.Calls.Add('Preflight')
            if($h.FailPreflight){$global:LASTEXITCODE=1;return}
            $dll=Join-Path $arguments[$index+1] 'bin/SelfService.Portal/release/SelfService.Portal.dll'
            New-Item -ItemType Directory -Path (Split-Path $dll -Parent) -Force|Out-Null
            [IO.File]::WriteAllText($dll,'synthetic preflight assembly')
            if($h.DriftPreflight){[IO.File]::AppendAllText((Join-Path $h.Root 'src/SelfService.Portal/SelfService.Portal.csproj'),'changed')}
            if($h.ReplaceMain){$record=Get-Content $h.Receipt -Raw|ConvertFrom-Json -AsHashtable;$record.pid=8765;$record|ConvertTo-Json|Set-Content $h.Receipt;[void](New-FreshTestProcess 8765 'C:\fixture\dotnet.exe' 0 ('dotnet "'+$h.Assembly+'"'));$h.MainPid=8765}
        }else{
            $h.Calls.Add('Rebuild')
            if($h.FailRebuild){$global:LASTEXITCODE=1;return}
            [IO.File]::WriteAllText($h.Assembly,'synthetic fresh Release assembly')
            if($h.DriftRebuild){[IO.File]::AppendAllText((Join-Path $h.Root 'src/SelfService.Portal/SelfService.Portal.csproj'),'changed')}
        }
        return 'Synthetic build output.'
    }
    if($arguments -contains '--checkpoint-credentials'){
        $h.Calls.Add('Checkpoint')
        $writers=@($h.Processes.Values|Where-Object {$_.Path -ne (Join-Path ([Environment]::SystemDirectory) 'conhost.exe')})
        Assert-FreshTest ($writers.Count -eq 0) 'Credential checkpoint ran before owned writers stopped.'
        if($h.FailCheckpoint){$global:LASTEXITCODE=1;return 'Withheld synthetic helper failure.'}
        New-Item -ItemType Directory -Path (Split-Path $h.Checkpoint -Parent) -Force|Out-Null
        if(!$h.MissingCheckpoint){[IO.File]::WriteAllText($h.Checkpoint,'synthetic encrypted checkpoint; not a real credential')}
        if($h.BadProof){return 'invalid-helper-output'}
        if($h.RegisterAfterCheckpoint){Add-FreshTestRegistration}
        return $h.Proof
    }
    if($arguments -contains '--get-resume-proof'){$h.Calls.Add('GetResume');return $h.Proof}
    throw 'Unexpected synthetic dotnet invocation.'
}
function New-FreshFixture {
    $folder=Join-Path $base ([guid]::NewGuid().ToString('N'))
    $global:PortalFreshFixture=@{Root=$folder;Calls=[Collections.Generic.List[string]]::new();Processes=@{};MainPid=4321;Start=[datetime]::UtcNow;Proof=('A'*64);ListenerChecks=0}
    $h=$global:PortalFreshFixture
    foreach($directory in @('scripts','config','src/SelfService.Portal/bin/Release/net10.0','.local/portal/azure-cli','.local/portal/agents')){New-Item -ItemType Directory -Path (Join-Path $folder $directory) -Force|Out-Null}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Restart-PortalFresh.ps1') -Destination (Join-Path $folder 'scripts/Restart-PortalFresh.ps1')
    '{"workloads":{"synthetic-a":{},"synthetic-b":{}}}'|Set-Content (Join-Path $folder 'config/workloads.json')
    '<Project/>'|Set-Content (Join-Path $folder 'src/SelfService.Portal/SelfService.Portal.csproj')
    $h.Assembly=Join-Path $folder 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll'
    [IO.File]::WriteAllText($h.Assembly,'synthetic old assembly')
    $h.Receipt=Join-Path $folder '.local/portal/process.json'
    $h.Checkpoint=Join-Path $folder '.local/portal/credential-cache/5087/restart.bin'
    $main=New-FreshTestProcess 4321 'C:\fixture\dotnet.exe' 0 ('dotnet "'+$h.Assembly+'"')
    @{pid=4321;executable=$main.Path;assembly=$h.Assembly;startedUtc=$main.StartTime.ToUniversalTime().ToString('O');url='http://localhost:5087'}|ConvertTo-Json|Set-Content $h.Receipt
    $h.Azure=Join-Path $folder ('.local/portal/azure-cli/'+('1'*32));New-Item -ItemType Directory -Path $h.Azure|Out-Null
    @{parentPid=4321;parentStartedUtc=$main.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $h.Azure 'owner.json')
    [IO.File]::WriteAllText((Join-Path $h.Azure 'azureProfile.json'),'synthetic profile; not real account data')
    [IO.File]::WriteAllText((Join-Path $h.Azure 'msal_token_cache.bin'),'synthetic credential bytes')
    $azure=New-FreshTestProcess 4322 'C:\fixture\python.exe' 4321 'python -IBm azure.cli account get-access-token'
    @{pid=$azure.Id;parentPid=4321;executable=$azure.Path;startedUtc=$azure.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $h.Azure 'process.json')
    $h.Codex=Join-Path $folder ('.local/portal/agents/'+('2'*32));New-Item -ItemType Directory -Path $h.Codex|Out-Null
    [IO.File]::WriteAllText((Join-Path $h.Codex 'auth.json'),'synthetic credential bytes')
    $codex=New-FreshTestProcess 4323 'C:\fixture\codex.exe' 4321 'codex app-server --listen stdio://'
    @{pid=$codex.Id;parentPid=4321;executable=$codex.Path;startedUtc=$codex.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $h.Codex 'process.json')
    $h.GitHubSentinel=Join-Path $folder 'normal-github-store.sentinel';[IO.File]::WriteAllText($h.GitHubSentinel,'untouched synthetic user credential store')
    @'
param([switch]$NoBrowser)
$h=$global:PortalFreshFixture
$h.Calls.Add('Start')
if($h.FailStart){throw 'Synthetic start failure.'}
if(!$NoBrowser){throw 'Fresh restart must start without passing its proof to startup logs.'}
$process=New-FreshTestProcess 5432 'C:\fixture\dotnet.exe' 0 ('dotnet "'+$h.Assembly+'"')
$h.MainPid=5432
@{pid=$process.Id;executable=$process.Path;assembly=$h.Assembly;startedUtc=$process.StartTime.ToUniversalTime().ToString('O');url='http://localhost:5087'}|ConvertTo-Json|Set-Content $h.Receipt
'@|Set-Content (Join-Path $folder 'scripts/Start-Portal.ps1')
    $h.Entry=Join-Path $folder 'scripts/Restart-PortalFresh.ps1'
    return $h
}
function Assert-OriginalFreshCredentials($h){Assert-FreshTest ((Test-Path (Join-Path $h.Azure 'msal_token_cache.bin')) -and (Test-Path (Join-Path $h.Codex 'auth.json'))) 'Original credentials were removed before a verified checkpoint.'}
function Assert-FreshShortcut($h){
    $shortcuts=@(Get-ChildItem (Join-Path $h.Root '.local/portal/fresh-restart') -Filter 'Resume-Portal.url' -Recurse)
    Assert-FreshTest ($shortcuts.Count -eq 1) 'Expected one private recovery shortcut.'
    $directory=Get-Acl -LiteralPath $shortcuts[0].DirectoryName
    Assert-FreshTest $directory.AreAccessRulesProtected 'Resume shortcut directory inherits broader access.'
    foreach($rule in $directory.Access){Assert-FreshTest ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18')) 'Private shortcut grants an unrelated identity.'}
    Assert-FreshTest ([IO.File]::ReadAllText($shortcuts[0].FullName).Contains($h.Proof)) 'Private resume shortcut lost the checkpoint capability.'
    foreach($log in Get-ChildItem (Join-Path $h.Root '.local/portal/fresh-restart') -Filter '*.log' -Recurse){Assert-FreshTest (![IO.File]::ReadAllText($log.FullName).Contains($h.Proof)) 'Resume proof leaked into a build log.'}
}

try{
    $h=New-FreshFixture;& $h.Entry -WhatIf;Assert-FreshTest ($h.Calls.Count -eq 0) 'WhatIf invoked lifecycle work.';Assert-OriginalFreshCredentials $h;$passed++
    foreach($flag in @('FailPreflight','DriftPreflight','HiddenMetadata','MetadataDenied','Denied','Foreign','WrongParent')){
        $h=New-FreshFixture;$h[$flag]=$true;Reject-FreshTest {& $h.Entry -NoBrowser};Assert-OriginalFreshCredentials $h
        Assert-FreshTest (!($h.Calls -contains 'StopMain') -and !($h.Calls -contains 'Checkpoint')) 'Preflight/ownership failure stopped a process.';$passed++
    }
    $h=New-FreshFixture;$h.Processes[4321].StartTime=$h.Start.AddDays(1);Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'PID reuse permitted a build or stop.';$passed++
    $h=New-FreshFixture;$record=Get-Content $h.Receipt -Raw|ConvertFrom-Json -AsHashtable;$record.url='http://localhost:5098';$record|ConvertTo-Json|Set-Content $h.Receipt
    Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'A different portal origin allowed a build or stop.';$passed++
    foreach($kind in @('Azure','Codex')){
        $h=New-FreshFixture;$copy=Join-Path (Split-Path $h[$kind] -Parent) ('3'*32);Copy-Item -LiteralPath $h[$kind] -Destination $copy -Recurse
        Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'Ambiguous sign-ins permitted lifecycle mutation.';$passed++
    }
    $h=New-FreshFixture;$h.LinkedPath=$h.Azure;Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'Linked credentials permitted lifecycle mutation.';$passed++
    $h=New-FreshFixture;$result=& $h.Entry -NoBrowser
    Assert-FreshTest ($result.State -eq 'Ready' -and ($h.Calls -join ',') -eq 'Preflight,StopMain,StopChild,StopChild,Checkpoint,Rebuild,Start') 'Fresh lifecycle order/readiness differs.'
    Assert-FreshTest (!(Test-Path $h.Azure) -and !(Test-Path (Join-Path $h.Codex 'auth.json')) -and (Test-Path $h.Checkpoint)) 'Approved credentials were not checkpointed before cleanup.'
    Assert-FreshTest ([IO.File]::ReadAllText($h.GitHubSentinel) -eq 'untouched synthetic user credential store') 'Normal GitHub store changed.'
    Assert-FreshShortcut $h;Assert-FreshTest (!(($result|ConvertTo-Json -Depth 5).Contains($h.Proof))) 'Result exposed the resume capability.';$passed++
    foreach($flag in @('StopNoop','ChildStopNoop','FailCheckpoint','BadProof','MissingCheckpoint')){
        $h=New-FreshFixture;$h[$flag]=$true;Reject-FreshTest {& $h.Entry -NoBrowser};Assert-OriginalFreshCredentials $h
        Assert-FreshTest (!($h.Calls -contains 'Rebuild') -and !($h.Calls -contains 'Start')) 'Stop/checkpoint failure permitted build/start.';$passed++
    }
    foreach($flag in @('FailRebuild','FailStart','BadHealth','DriftRebuild')){
        $h=New-FreshFixture;$h[$flag]=$true;Reject-FreshTest {& $h.Entry -NoBrowser}
        Assert-FreshTest (Test-Path $h.Checkpoint) 'Failed fresh build/start removed the encrypted checkpoint.';Assert-FreshShortcut $h
        if($flag -in @('FailRebuild','DriftRebuild')){Assert-FreshTest (!($h.Calls -contains 'Start')) 'Failed/drifting build started the portal.'};$passed++
    }
    $h=New-FreshFixture;$result=& $h.Entry;Assert-FreshTest (($h.Calls -join ',').EndsWith('Start,Browser')) 'Default restart did not launch the resume handoff.';Assert-FreshShortcut $h;$passed++
    $h=New-FreshFixture;New-Item -ItemType Directory -Path (Split-Path $h.Checkpoint -Parent) -Force|Out-Null;[IO.File]::WriteAllText($h.Checkpoint,'synthetic existing checkpoint')
    Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'Pending checkpoint was overwritten or portal stopped.'
    & $h.Entry -WhatIf;Assert-FreshTest ($h.Calls.Count -eq 0) 'WhatIf touched a pending checkpoint.';$passed++
    Reject-FreshTest {& $h.Entry -ResumePending -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'Recovery stopped an already running portal.';$passed++
    $h.Processes.Clear();Remove-Item -LiteralPath $h.Receipt;$h.Calls.Clear();$result=& $h.Entry -ResumePending -NoBrowser
    Assert-FreshTest ($result.State -eq 'Ready' -and ($h.Calls -join ',') -eq 'Preflight,GetResume,Rebuild,Start') 'Pending checkpoint recovery replaced credentials or used the wrong order.';Assert-FreshShortcut $h;$passed++
    $h=New-FreshFixture;$h.Processes.Clear();Reject-FreshTest {& $h.Entry -ResumePending -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'Recovery without a checkpoint mutated lifecycle.';$passed++
    $h=New-FreshFixture;$h.ReplaceMain=$true;Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest (($h.Calls -join ',') -eq 'Preflight') 'Changed ownership during preflight allowed a stop.';Assert-OriginalFreshCredentials $h;$passed++
    $h=New-FreshFixture;[void](New-FreshTestProcess 4325 'C:\fixture\unregistered.exe' $h.MainPid 'unregistered child');Reject-FreshTest {& $h.Entry -NoBrowser};Assert-FreshTest ($h.Calls.Count -eq 0) 'An unregistered writer permitted lifecycle mutation.';Assert-OriginalFreshCredentials $h;$passed++
    $h=New-FreshFixture;$h.LateKnownWriter=$true;$result=& $h.Entry -NoBrowser
    Assert-FreshTest ($result.State -eq 'Ready' -and ($h.Calls -join ',') -eq 'Preflight,StopMain,StopChild,StopChild,StopMcpTree,Checkpoint,Rebuild,Start') 'A late owned MCP writer was not stopped before checkpoint.';Assert-FreshShortcut $h;$passed++
    $h=New-FreshFixture;$h.LateUnknownWriter=$true;Reject-FreshTest {& $h.Entry -NoBrowser}
    Assert-FreshTest (($h.Calls -join ',') -eq 'Preflight,StopMain' -and $h.Processes.ContainsKey(4325)) 'An unreceipted late writer was guessed or checkpointed.';Assert-OriginalFreshCredentials $h;$passed++
    $h=New-FreshFixture;$h.Processes.Clear();Remove-Item -LiteralPath $h.Receipt;$h.LateColdRegistration=$true;Reject-FreshTest {& $h.Entry -NoBrowser}
    Assert-FreshTest (($h.Calls -join ',') -eq 'Preflight' -and $h.Processes.ContainsKey(8765) -and $h.Processes.ContainsKey(8766)) 'A late cold-start registration or its children were adopted.';Assert-OriginalFreshCredentials $h;$passed++
    $h=New-FreshFixture;$h.RegisterAfterCheckpoint=$true;Reject-FreshTest {& $h.Entry -NoBrowser}
    $record=Get-Content $h.Receipt -Raw|ConvertFrom-Json -AsHashtable
    Assert-FreshTest ($record.pid -eq 8765 -and $h.Processes.ContainsKey(8765) -and $h.Processes.ContainsKey(8766) -and !($h.Calls -contains 'Rebuild')) 'A replacement ownership receipt/process was removed after checkpoint.';Assert-OriginalFreshCredentials $h;Assert-FreshShortcut $h;$passed++
    $h=New-FreshFixture;$h.StaleAfterWriters=$true;Reject-FreshTest {& $h.Entry -NoBrowser};$record=Get-Content $h.Receipt -Raw|ConvertFrom-Json -AsHashtable
    Assert-FreshTest ($record.pid -eq 8765 -and ($h.Calls -join ',') -eq 'Preflight,StopMain,StopChild,StopChild') 'An exited replacement registration became the checkpoint baseline.';Assert-OriginalFreshCredentials $h;$passed++
    $h=New-FreshFixture;[void](New-FreshTestProcess 4324 (Join-Path ([Environment]::SystemDirectory) 'conhost.exe') $h.MainPid 'system console host');$result=& $h.Entry -NoBrowser
    Assert-FreshTest ($result.State -eq 'Ready' -and $h.Processes.ContainsKey(4324) -and ($h.Calls|Where-Object {$_ -like 'Stop*'}).Count -eq 3) 'The verified system console host was killed or treated as a credential writer.';$passed++
    foreach($flag in @('SpoofedConsole','ConsoleMissingDate','ConsolePredates')){
        $h=New-FreshFixture;$h[$flag]=$true;$image=if($flag -eq 'SpoofedConsole'){'C:\fixture\conhost.exe'}else{Join-Path ([Environment]::SystemDirectory) 'conhost.exe'}
        [void](New-FreshTestProcess 4324 $image $h.MainPid 'unverified console host');Reject-FreshTest {& $h.Entry -NoBrowser}
        Assert-FreshTest ($h.Calls.Count -eq 0 -and $h.Processes.ContainsKey(4324)) 'An unverified console process permitted mutation or was killed.';$passed++
    }
    [ordered]@{passed=$passed;scope='Synthetic fresh restart lifecycle; injected processes/build/checkpoint/browser; no real portal, sign-in, OAuth credentials, deployment or DPAPI acceptance';generatedUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $base 'results.json') -Encoding utf8NoBOM
    Write-Host "PASS: $passed synthetic fresh-restart scenarios. Evidence: $base/results.json"
}finally{$env:STUDIO_Portal__Port=$savedPort;Remove-Variable PortalFreshFixture -Scope Global}
