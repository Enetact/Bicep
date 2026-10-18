#requires -Version 7.4
<#
.SYNOPSIS
Preflight, preserve approved sign-ins, rebuild and restart only the owned portal.
.EXAMPLE
./scripts/Restart-PortalFresh.ps1
.EXAMPLE
./scripts/Restart-PortalFresh.ps1 -ResumePending -NoBrowser
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$NoBrowser,[switch]$ResumePending)
$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'Fresh portal restart supports Windows only.'}
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$portal=Join-Path $root 'src/SelfService.Portal'
$assembly=Join-Path $portal 'bin/Release/net10.0/SelfService.Portal.dll'
$receipt=Join-Path $root '.local/portal/process.json'
$project=Join-Path $portal 'SelfService.Portal.csproj'
$settingsPath=Join-Path $portal 'portal.local.json'
$port=5087
if(Test-Path -LiteralPath $settingsPath){
    $settings=Get-Content -LiteralPath $settingsPath -Raw|ConvertFrom-Json -AsHashtable
    if($settings.Portal.Contains('Port')){$port=[int]$settings.Portal.Port}
    if($settings.Portal.Contains('RepositoryRoot') -and $settings.Portal.RepositoryRoot -and [IO.Path]::GetFullPath($settings.Portal.RepositoryRoot) -ne $root){throw 'Fresh restart requires the checkout to be the configured repository root.'}
}
if($env:STUDIO_Portal__Port){$port=[int]$env:STUDIO_Portal__Port}
if($port -lt 1024 -or $port -gt 65535){throw 'Invalid portal port.'}
$origin="http://localhost:$port"
$checkpoint=Join-Path $root ".local/portal/credential-cache/$port/restart.bin"

function Assert-FreshPath([string]$Path){
    $absolute=[IO.Path]::GetFullPath($Path)
    if(!$absolute.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Restart path escaped the checkout.'}
    for($item=$absolute;$item;$item=Split-Path $item -Parent){
        if(Test-Path -LiteralPath $item){if((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Restart refuses linked source, receipt or credential paths.'}}
        if($item -eq [IO.Path]::GetPathRoot($item)){break}
    }
}
function Read-FreshReceipt([string]$Path){
    Assert-FreshPath $Path
    if((Get-Item -LiteralPath $Path).Length -gt 65536){throw 'Oversized ownership receipt.'}
    Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -AsHashtable
}
function Get-FreshProcess([Collections.IDictionary]$Record,[int]$ParentPid=0,[string]$Kind='main'){
    $number=0
    if(![int]::TryParse([string]$Record.pid,[ref]$number) -or $number -le 0){throw 'Invalid owned process receipt.'}
    $process=Get-Process -Id $number -ErrorAction SilentlyContinue
    if(!$process){return $null}
    $info=Get-CimInstance Win32_Process -Filter "ProcessId=$number" -ErrorAction Stop
    if(!$process.Path -or !$info.CommandLine -or [IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath($Record.executable) -or
       $process.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$Record.startedUtc).UtcTicks){throw 'Process ownership is unavailable or mismatched. Nothing may be adopted.'}
    if($ParentPid -and ([int]$Record.parentPid -ne $ParentPid -or [int]$info.ParentProcessId -ne $ParentPid)){throw 'Child process parent ownership differs.'}
    $file=[IO.Path]::GetFileName($process.Path)
    $valid=switch($Kind){
        main {$file -eq 'dotnet.exe' -and $info.CommandLine.Contains('"'+$assembly+'"')}
        codex {$file -eq 'codex.exe' -and $info.CommandLine -match 'app-server.*stdio://'}
        azure {$file -eq 'az.exe' -or ($file -eq 'python.exe' -and $info.CommandLine -match '\bazure\.cli\b')}
        github {$file -eq 'gh.exe' -and $info.CommandLine -match 'auth (login|token) --hostname github\.com'}
        bicep {$file -eq 'Azure.Bicep.McpServer.exe'}
        'azure-mcp' {$file -eq 'azmcp.exe'}
        ado {$file -eq 'node.exe'}
        default {$false}
    }
    if(!$valid){throw 'Owned process command or executable kind differs.'}
    return $process
}
function Get-FreshSnapshot {
    param([switch]$AllowAmbiguous)
    $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|Where-Object LocalPort -eq $port)
    $snapshot=@{Record=$null;Main=$null;Children=@();AzureHomes=@();CodexHomes=@();AzureFolders=@();CodexFolders=@();Receipts=@()}
    if(!(Test-Path -LiteralPath $receipt)){
        if($listeners.Count){throw 'The portal port has an unowned listener.'}
        return $snapshot
    }
    $record=Read-FreshReceipt $receipt
    if([IO.Path]::GetFullPath($record.assembly) -ne $assembly){throw 'The registered assembly belongs to another checkout.'}
    if(!$record.Contains('url') -or $record.url -cne $origin){throw 'The registered portal origin differs from the requested restart port.'}
    $main=Get-FreshProcess $record
    if(@($listeners|Where-Object {!$main -or $_.OwningProcess -ne $main.Id}).Count){throw 'The configured port belongs to another process.'}
    $snapshot.Record=$record;$snapshot.Main=$main
    foreach($kind in @('azure-cli','agents','github-cli','mcp')){
        $directory=Join-Path $root ".local/portal/$kind"
        Assert-FreshPath $directory
        if(!(Test-Path -LiteralPath $directory)){continue}
        foreach($folder in Get-ChildItem -LiteralPath $directory -Directory -Force){
            if($folder.Name -notmatch '^[a-f0-9]{32}$'){continue}
            Assert-FreshPath $folder.FullName
            if($kind -eq 'mcp'){
                foreach($provider in Get-ChildItem -LiteralPath $folder.FullName -Directory -Force){
                    if($provider.Name -notin @('bicep','azure','ado')){continue}
                    $path=Join-Path $provider.FullName 'process.json'
                    Assert-FreshPath $path
                    if(!(Test-Path -LiteralPath $path)){continue}
                    $childRecord=Read-FreshReceipt $path
                    if($childRecord.schema -ne 'platform.mcp-process/v1' -or $childRecord.provider -ne $provider.Name){throw 'MCP process receipt schema differs.'}
                    if($childRecord.parentPid -ne $record.pid -or ([datetimeoffset]$childRecord.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
                    $childKind=if($provider.Name -eq 'azure'){'azure-mcp'}else{$provider.Name}
                    $child=Get-FreshProcess $childRecord $record.pid $childKind
                    $snapshot.Children+=@{Record=$childRecord;Process=$child;Kind=$childKind}
                    $snapshot.Receipts+=$path
                }
                continue
            }
            $processPath=Join-Path $folder.FullName 'process.json'
            Assert-FreshPath $processPath
            if($kind -eq 'azure-cli'){
                $ownerPath=Join-Path $folder.FullName 'owner.json'
                if(!(Test-Path -LiteralPath $ownerPath)){continue}
                $owner=Read-FreshReceipt $ownerPath
                if($owner.parentPid -ne $record.pid -or ([datetimeoffset]$owner.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
                $snapshot.AzureFolders+=$folder.FullName
                foreach($name in @('azureProfile.json','msal_token_cache.bin','msal_http_cache.bin')){Assert-FreshPath (Join-Path $folder.FullName $name)}
                if((Test-Path -LiteralPath (Join-Path $folder.FullName 'azureProfile.json')) -and (Test-Path -LiteralPath (Join-Path $folder.FullName 'msal_token_cache.bin'))){$snapshot.AzureHomes+=$folder.FullName}
            }elseif(!(Test-Path -LiteralPath $processPath)){continue}
            if(Test-Path -LiteralPath $processPath){
                $childRecord=Read-FreshReceipt $processPath
                if($childRecord.parentPid -ne $record.pid){continue}
                if($kind -eq 'github-cli' -and ([datetimeoffset]$childRecord.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
                if(([datetimeoffset]$childRecord.startedUtc).UtcTicks -lt ([datetimeoffset]$record.startedUtc).UtcTicks){throw 'Child receipt predates its claimed parent.'}
                $childKind=switch($kind){'azure-cli' {'azure'} 'agents' {'codex'} 'github-cli' {'github'}}
                $child=Get-FreshProcess $childRecord $record.pid $childKind
                $snapshot.Children+=@{Record=$childRecord;Process=$child;Kind=$childKind}
                $snapshot.Receipts+=$processPath
                if($kind -eq 'agents'){
                    $snapshot.CodexFolders+=$folder.FullName
                    $auth=Join-Path $folder.FullName 'auth.json';Assert-FreshPath $auth
                    if(Test-Path -LiteralPath $auth){$snapshot.CodexHomes+=$folder.FullName}
                }
            }
        }
    }
    if(!$AllowAmbiguous -and ($snapshot.AzureHomes.Count -gt 1 -or $snapshot.CodexHomes.Count -gt 1)){throw 'More than one eligible signed-in profile exists. Disconnect the extra portal sessions before restarting.'}
    $snapshot
}
function Assert-FreshChildInventory([Collections.IDictionary]$Snapshot){
    if(!$Snapshot.Record){return}
    $known=@($Snapshot.Children|Where-Object Process|ForEach-Object {[int]$_.Record.pid})
    foreach($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$([int]$Snapshot.Record.pid)" -ErrorAction Stop)){
        if([int]$child.ProcessId -in $known){continue}
        # Windows can give this console app its own system console host. It is not
        # a credential writer and is never added to our process termination list.
        $console=Join-Path ([Environment]::SystemDirectory) 'conhost.exe'
        if($child.Name -eq 'conhost.exe' -and $child.ExecutablePath -and $child.CreationDate -and
           [int]$child.ParentProcessId -eq [int]$Snapshot.Record.pid -and
           [IO.Path]::GetFullPath($child.ExecutablePath) -eq [IO.Path]::GetFullPath($console) -and
           ([datetimeoffset]$child.CreationDate).UtcTicks -ge ([datetimeoffset]$Snapshot.Record.startedUtc).UtcTicks){continue}
        throw 'An old-portal child has no verifiable ownership receipt. Credential directories are preserved; no unknown process is stopped.'
    }
}
function Assert-FreshRegistration([Collections.IDictionary]$Expected,[Collections.IDictionary]$Observed){
    if(($null -eq $Expected) -ne ($null -eq $Observed) -or ($Expected -and
       ($Expected.pid -ne $Observed.pid -or $Expected.startedUtc -ne $Observed.startedUtc -or
        $Expected.assembly -ne $Observed.assembly -or $Expected.executable -ne $Observed.executable -or $Expected.url -ne $Observed.url))){
        throw 'Portal registration changed during restart. No replacement process or receipt may be adopted; credential directories/checkpoint are preserved.'
    }
}
function Get-FreshSourceHash {
    $paths=@(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if($LASTEXITCODE -ne 0 -or !$paths.Count){throw 'Fresh restart requires the source Git checkout.'}
    if(Test-Path -LiteralPath $settingsPath){$paths+='src/SelfService.Portal/portal.local.json'}
    $text=[Text.StringBuilder]::new()
    foreach($relative in $paths|Sort-Object -Unique){
        if([IO.Path]::IsPathFullyQualified($relative) -or $relative -match '(^|/)\.\.(/|$)|[\r\n]'){throw 'Invalid source path.'}
        $path=Join-Path $root $relative;Assert-FreshPath $path
        $hash=if(Test-Path -LiteralPath $path -PathType Leaf){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}else{'MISSING'}
        [void]$text.Append($relative).Append([char]0).Append($hash).Append("`n")
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString())))
}
function Set-FreshPrivateDirectory([string]$Path){
    Assert-FreshPath $Path
    New-Item -ItemType Directory -Path $Path -Force|Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid,$null))){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Remove-FreshOwnedDirectory([string]$Path){
    Assert-FreshPath $Path
    foreach($item in Get-ChildItem -LiteralPath $Path -Force){
        Assert-FreshPath $item.FullName
        if($item.PSIsContainer){Remove-FreshOwnedDirectory $item.FullName}else{Remove-Item -LiteralPath $item.FullName -Force}
    }
    Remove-Item -LiteralPath $Path -Force
}
function Invoke-FreshBuild([string[]]$Arguments,[string]$Log){
    $output=@(& dotnet @Arguments 2>&1)
    $exit=$LASTEXITCODE
    [IO.File]::WriteAllText($Log,($output -join "`n"),[Text.UTF8Encoding]::new($false))
    if($exit -ne 0){throw "Portal build failed. Inspect $Log. Approved sign-ins and any private resume shortcut are retained."}
}
function Invoke-FreshCheckpoint([string]$Preflight,[string[]]$Arguments){
    # Deliberately capture all helper output. Never print it or put the proof in logs, receipts or exceptions.
    try{$output=@(& dotnet $Preflight @Arguments 2>&1);$exit=$LASTEXITCODE}catch{throw 'Credential checkpoint helper failed; original credential directories are preserved.'}
    $proof=($output -join '').Trim()
    if($exit -ne 0 -or $proof -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'Credential checkpoint was not verified; original credential directories are preserved.'}
    Assert-FreshPath $checkpoint
    if(!(Test-Path -LiteralPath $checkpoint -PathType Leaf)){throw 'Verified encrypted credential checkpoint is missing; original credential directories are preserved.'}
    return $proof
}

foreach($path in @($receipt,$assembly,$project,$checkpoint)){Assert-FreshPath $path}
if(!(Test-Path -LiteralPath $project)){throw 'Portal source project is missing.'}
$before=Get-FreshSnapshot
Assert-FreshChildInventory $before
$pending=Test-Path -LiteralPath $checkpoint -PathType Leaf
if($WhatIfPreference){[void]$PSCmdlet.ShouldProcess($origin,'Preflight source, preserve approved sign-ins, stop owned processes, rebuild fresh Release and start');return}
if($ResumePending -and (!$pending -or $before.Main -or $before.Children.Where({$_.Process}).Count)){throw 'ResumePending requires an existing checkpoint, a stopped registered process and no owned writers.'}
if($pending -and !$ResumePending){throw 'A previous encrypted restart checkpoint is waiting. Resume it first, or use -ResumePending once the portal and its writers are stopped.'}
if(!$PSCmdlet.ShouldProcess($origin,'Preflight source, preserve approved sign-ins, stop owned processes, rebuild fresh Release and start')){return}
$operation=Join-Path $root ('.local/portal/fresh-restart/'+[guid]::NewGuid().ToString('N'))
Set-FreshPrivateDirectory $operation
$preflightRoot=Join-Path $operation 'preflight'
$preflightAssembly=Join-Path $preflightRoot 'bin/SelfService.Portal/release/SelfService.Portal.dll'
$sourceHash=Get-FreshSourceHash
Invoke-FreshBuild @('build',$project,'-c','Release','--artifacts-path',$preflightRoot,'-p:RestoreLockedMode=true','-p:UseSharedCompilation=false','-m:1','-nodeReuse:false') (Join-Path $operation 'preflight-build.log')
if(!(Test-Path -LiteralPath $preflightAssembly -PathType Leaf)){throw 'Isolated preflight did not produce the expected application.'}
if((Get-FreshSourceHash) -cne $sourceHash){throw 'Source changed during preflight. The running portal and its sign-ins are unchanged.'}
$current=Get-FreshSnapshot
Assert-FreshChildInventory $current
if(($null -eq $before.Record) -ne ($null -eq $current.Record) -or ($before.Record -and ($before.Record.pid -ne $current.Record.pid -or $before.Record.startedUtc -ne $current.Record.startedUtc))){throw 'Portal ownership changed during preflight. No process was stopped.'}
if(($before.AzureHomes -join '|') -ne ($current.AzureHomes -join '|') -or ($before.CodexHomes -join '|') -ne ($current.CodexHomes -join '|')){throw 'Signed-in profiles changed during preflight. No process was stopped.'}
if($ResumePending -and ($current.Main -or $current.Children.Where({$_.Process}).Count)){throw 'A process started during recovery preflight. No process was stopped.'}
if(!$ResumePending){
    # Stop the old managed parent first: its Dispose handlers otherwise delete the credentials being migrated.
    if($current.Main){Stop-Process -InputObject $current.Main -Force;if(!$current.Main.WaitForExit(10000)){throw 'Owned portal did not exit; credential files are preserved.'}}
    # A request can start a helper after the pre-stop snapshot. Once the parent exits,
    # enumerate again and refuse any process whose receipt was not committed.
    $quiescing=Get-FreshSnapshot -AllowAmbiguous
    Assert-FreshChildInventory $quiescing
    Assert-FreshRegistration $current.Record $quiescing.Record
    if($quiescing.Main){throw 'A replacement portal started during shutdown. No replacement process is stopped; credentials are preserved.'}
    foreach($child in $quiescing.Children){
        $process=Get-FreshProcess $child.Record $quiescing.Record.pid $child.Kind
        if($process){
            # Match governed Stop-PortalMcp: verified MCP servers can own CLI descendants
            # that refresh the same cache. Never apply tree termination to desktop apps.
            if($child.Kind -in @('bicep','azure-mcp','ado')){$process.Kill($true)}else{Stop-Process -InputObject $process -Force}
            if(!$process.WaitForExit(10000)){throw 'Owned credential writer did not exit; credential files are preserved.'}
        }
    }
    $current=Get-FreshSnapshot
    Assert-FreshRegistration $quiescing.Record $current.Record
    Assert-FreshChildInventory $current
    if($current.Main -or $current.Children.Where({$_.Process}).Count){throw 'Owned credential writers remain active. Credential directories are preserved.'}
    if(@(Get-NetTCPConnection -State Listen -ErrorAction Stop|Where-Object LocalPort -eq $port).Count){throw 'The port is not free after stopping owned processes. Credential files are preserved.'}
    $arguments=@('--checkpoint-credentials','--repository',$root,'--port',[string]$port)
    if($current.AzureHomes.Count){$arguments+=@('--azure-home',$current.AzureHomes[0])}
    if($current.CodexHomes.Count){$arguments+=@('--codex-home',$current.CodexHomes[0])}
    $proof=Invoke-FreshCheckpoint $preflightAssembly $arguments
}else{$proof=Invoke-FreshCheckpoint $preflightAssembly @('--get-resume-proof','--repository',$root,'--port',[string]$port)}
$resumeUrl=$origin+'/#resume='+$proof
$shortcut=Join-Path $operation 'Resume-Portal.url'
[IO.File]::WriteAllText($shortcut,"[InternetShortcut]`r`nURL=$resumeUrl`r`n",[Text.UTF8Encoding]::new($false))
# Only after the encrypted checkpoint and restricted handoff file exist may ephemeral credentials be removed.
$cleanup=Get-FreshSnapshot -AllowAmbiguous
Assert-FreshRegistration $current.Record $cleanup.Record
if($cleanup.Main -or $cleanup.Children.Where({$_.Process}).Count){throw 'A portal or credential writer started after checkpoint. No cleanup performed; encrypted checkpoint and private resume shortcut retained.'}
foreach($directory in $current.AzureFolders){if(Test-Path -LiteralPath $directory){Remove-FreshOwnedDirectory $directory}}
foreach($directory in $current.CodexFolders){foreach($name in @('auth.json','process.json')){$path=Join-Path $directory $name;Assert-FreshPath $path;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}}
foreach($path in $current.Receipts){Assert-FreshPath $path;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
if(Test-Path -LiteralPath $receipt){
    $registered=Read-FreshReceipt $receipt
    Assert-FreshRegistration $current.Record $registered
    if(Get-FreshProcess $registered){throw 'A registered portal is running before receipt cleanup. No receipt was removed; checkpoint and shortcut retained.'}
    Remove-Item -LiteralPath $receipt -Force
}elseif($current.Record){throw 'The old ownership receipt disappeared before cleanup. Checkpoint and private resume shortcut retained.'}
if((Get-FreshSourceHash) -cne $sourceHash){throw "Source changed after checkpoint. Credentials are protected; private resume shortcut retained at $shortcut. Rerun with -ResumePending after source edits finish."}
Invoke-FreshBuild @('build',$project,'-c','Release','-t:Rebuild','-p:RestoreLockedMode=true','-p:UseSharedCompilation=false','-m:1','-nodeReuse:false') (Join-Path $operation 'fresh-build.log')
if((Get-FreshSourceHash) -cne $sourceHash){throw "Source changed during the fresh rebuild. No application was started; private resume shortcut retained at $shortcut."}
& (Join-Path $PSScriptRoot 'Start-Portal.ps1') -NoBrowser
$after=Get-FreshSnapshot
if(!$after.Main){throw "Fresh portal did not register a running owned process. Private resume shortcut retained at $shortcut."}
$expected=(Get-Content -LiteralPath (Join-Path $root 'config/workloads.json') -Raw|ConvertFrom-Json -AsHashtable).workloads.Count
$health=Invoke-RestMethod "$origin/api/bootstrap" -TimeoutSec 5 -MaximumRedirection 0
if($health.products.Count -ne $expected){throw "Fresh application workload catalog differs. Private resume shortcut retained at $shortcut."}
if(!$NoBrowser){Start-Process -FilePath $resumeUrl -WindowStyle Hidden;Write-Host 'Fresh portal started. The private sign-in resume link opened in your browser.'}
else{Write-Host "Fresh portal started. Open the restricted local shortcut to resume sign-ins: $shortcut"}
$proof=$null;$resumeUrl=$null
[pscustomobject]@{State='Ready';Url=$origin;ProcessId=$after.Main.Id;SourceSha256=$sourceHash;AssemblySha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant();CredentialCheckpoint='Encrypted; one-time resume required';Shortcut=$shortcut;WorkflowState='Fresh evidence, plans and approvals required'}
