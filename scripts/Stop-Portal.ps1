#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([switch]$DiscardConnections)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Suite.Common.ps1')
$root=Get-SuiteRoot
$receipt=Assert-SuitePath (Join-Path $root '.local/portal/process.json')
if(!(Test-Path -LiteralPath $receipt)){Write-Host 'No portal process is registered.';return}
if(!$PSCmdlet.ShouldProcess($root,'Stop the owned portal and its helpers; preserve approved sign-ins unless explicitly discarded')){return}
if(!$DiscardConnections){
    & (Join-Path $PSScriptRoot 'Restart-PortalFresh.ps1') -CheckpointOnly -NoBrowser -Confirm:$false
    return
}
$assembly=Assert-SuitePath (Join-Path $root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll')

function Read-DiscardReceipt([string]$Path){
    $safe=Assert-SuitePath $Path
    if((Get-Item -LiteralPath $safe -Force).Length -gt 65536){throw 'Oversized portal ownership receipt; credential cleanup deferred.'}
    Get-Content -LiteralPath $safe -Raw|ConvertFrom-Json -AsHashtable
}
function Get-DiscardProcess([Collections.IDictionary]$Record,[int]$Parent=0,[string]$Kind='portal'){
    $number=0
    if(![int]::TryParse([string]$Record.pid,[ref]$number) -or $number -le 0){throw 'Invalid owned portal process receipt.'}
    try{$process=Get-Process -Id $number -ErrorAction Stop}catch [Microsoft.PowerShell.Commands.ProcessCommandException]{
        if($_.FullyQualifiedErrorId -notlike 'NoProcessFoundForGivenId*'){throw};return $null
    }
    if(!$process){return $null}
    $info=Get-CimInstance Win32_Process -Filter "ProcessId=$number" -ErrorAction Stop
    if(!$process.Path -or !$info -or !$info.CommandLine -or
       [IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath($Record.executable) -or
       $process.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$Record.startedUtc).UtcTicks){
        throw 'Portal process ownership could not be verified; no replacement process may be stopped.'
    }
    if($Parent -and ([int]$Record.parentPid -ne $Parent -or [int]$info.ParentProcessId -ne $Parent)){throw 'Portal child parent ownership differs.'}
    $file=[IO.Path]::GetFileName($process.Path)
    $valid=switch($Kind){
        portal {$info.CommandLine.Contains('"'+$assembly+'"')}
        codex {$file -eq 'codex.exe' -and $info.CommandLine -match 'app-server.*stdio://'}
        azure {$file -eq 'az.exe' -or ($file -eq 'python.exe' -and $info.CommandLine -match 'azure\.cli')}
        github {$file -eq 'gh.exe' -and $info.CommandLine -match 'auth (login|token) --hostname github.com'}
        bicep {$file -eq 'Azure.Bicep.McpServer.exe'}
        'azure-mcp' {$file -eq 'azmcp.exe'}
        ado {$file -eq 'node.exe'}
        aspire {
            $runtimeRoot=[IO.Path]::GetFullPath((Join-Path $root '.tools/aspire-cli'))+[IO.Path]::DirectorySeparatorChar
            $keyPattern=[regex]::Escape([IO.Path]::GetFullPath((Join-Path $root '.local/portal/mcp')))+'\\[a-f0-9]{32}\\aspire\\api-key-[a-f0-9]{32}\.rsp'
            $file -eq 'aspire.exe' -and $process.Path.StartsWith($runtimeRoot,[StringComparison]::OrdinalIgnoreCase) -and
                $info.CommandLine -match ('\sagent\s+mcp\s+--dashboard-url\s+http://127\.0\.0\.1:[0-9]{4,5}/?\s+(?:"@'+$keyPattern+'"|@'+$keyPattern+')\s+--log-level\s+None\s+--non-interactive\s+--nologo\s*$')
        }
        default {$false}
    }
    if(!$valid){throw 'Portal child executable or command differs; credential cleanup deferred.'}
    $process
}
function Assert-DiscardDirectory([string]$Path){
    $safe=Assert-SuitePath $Path
    foreach($entry in Get-ChildItem -LiteralPath $safe -Force){
        $null=Assert-SuitePath $entry.FullName
        if($entry.PSIsContainer){Assert-DiscardDirectory $entry.FullName}
    }
}
function Get-DiscardSnapshot {
    $record=Read-DiscardReceipt $receipt
    if([IO.Path]::GetFullPath($record.assembly) -ne $assembly){throw 'Portal ownership assembly belongs to another checkout.'}
    $snapshot=@{Record=$record;Main=(Get-DiscardProcess $record);Children=@();Files=@();AzureFolders=@()}
    foreach($kind in @('agents','github-cli','mcp','azure-cli')){
        $directory=Assert-SuitePath (Join-Path $root ".local/portal/$kind")
        if(!(Test-Path -LiteralPath $directory)){continue}
        foreach($folder in Get-ChildItem -LiteralPath $directory -Directory -Force){
            if($folder.Name -notmatch '^[a-f0-9]{32}$'){continue}
            $null=Assert-SuitePath $folder.FullName
            if($kind -eq 'mcp'){
                foreach($provider in Get-ChildItem -LiteralPath $folder.FullName -Directory -Force){
                    if($provider.Name -notin @('bicep','azure','ado','aspire')){continue}
                    $null=Assert-SuitePath $provider.FullName
                    $path=Assert-SuitePath (Join-Path $provider.FullName 'process.json')
                    # Validate response files before any parent or helper can be stopped.
                    foreach($entry in Get-ChildItem -LiteralPath $provider.FullName -Force){$null=Assert-SuitePath $entry.FullName}
                    if(!(Test-Path -LiteralPath $path)){continue}
                    $childRecord=Read-DiscardReceipt $path
                    if($childRecord.schema -ne 'platform.mcp-process/v1' -or $childRecord.provider -ne $provider.Name){throw 'Invalid MCP ownership receipt.'}
                    if($childRecord.parentPid -ne $record.pid -or ([datetimeoffset]$childRecord.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
                    $childKind=if($provider.Name -eq 'azure'){'azure-mcp'}else{$provider.Name}
                    $snapshot.Children+=@{Record=$childRecord;Kind=$childKind;Process=(Get-DiscardProcess $childRecord $record.pid $childKind)}
                    $snapshot.Files+=$path
                }
                continue
            }
            $path=Assert-SuitePath (Join-Path $folder.FullName 'process.json')
            $auth=Assert-SuitePath (Join-Path $folder.FullName 'auth.json')
            if($kind -eq 'azure-cli'){
                $ownerPath=Assert-SuitePath (Join-Path $folder.FullName 'owner.json')
                if(!(Test-Path -LiteralPath $ownerPath)){continue}
                $owner=Read-DiscardReceipt $ownerPath
                if($owner.parentPid -ne $record.pid -or ([datetimeoffset]$owner.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
                Assert-DiscardDirectory $folder.FullName
                $snapshot.AzureFolders+=$folder.FullName
            }elseif(!(Test-Path -LiteralPath $path)){continue}
            if(!(Test-Path -LiteralPath $path)){continue}
            $childRecord=Read-DiscardReceipt $path
            if($childRecord.parentPid -ne $record.pid){
                if($kind -eq 'azure-cli'){throw 'CLI process ownership differs from its credential owner; credential cleanup deferred.'}
                continue
            }
            if($kind -eq 'github-cli' -and ([datetimeoffset]$childRecord.parentStartedUtc).UtcTicks -ne ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
            if(([datetimeoffset]$childRecord.startedUtc).UtcTicks -lt ([datetimeoffset]$record.startedUtc).UtcTicks){throw 'Portal child receipt predates its claimed parent.'}
            $childKind=switch($kind){agents {'codex'} 'github-cli' {'github'} 'azure-cli' {'azure'}}
            $snapshot.Children+=@{Record=$childRecord;Kind=$childKind;Process=(Get-DiscardProcess $childRecord $record.pid $childKind)}
            if($kind -ne 'azure-cli'){$snapshot.Files+=$path}
            if($kind -eq 'agents'){$snapshot.Files+=$auth}
        }
    }
    # Do not stop an unregistered child, or discard credentials while it can still write them.
    $known=@($snapshot.Children|Where-Object Process|ForEach-Object {[int]$_.Record.pid})
    foreach($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$([int]$record.pid)" -ErrorAction Stop)){
        if([int]$child.ProcessId -in $known){continue}
        $console=Join-Path ([Environment]::SystemDirectory) 'conhost.exe'
        if($child.Name -eq 'conhost.exe' -and $child.ExecutablePath -and $child.CreationDate -and
           [IO.Path]::GetFullPath($child.ExecutablePath) -eq [IO.Path]::GetFullPath($console) -and
           ([datetimeoffset]$child.CreationDate).UtcTicks -ge ([datetimeoffset]$record.startedUtc).UtcTicks){continue}
        throw 'Unregistered portal child exists; credential cleanup deferred.'
    }
    $snapshot
}
function Assert-DiscardRegistration($Expected,$Observed){
    foreach($name in @('pid','startedUtc','assembly','executable')){
        if($Expected[$name] -cne $Observed[$name]){throw 'Portal registration changed; no replacement process or credential files may be adopted.'}
    }
}
function Remove-DiscardDirectory([string]$Path){
    $safe=Assert-SuitePath $Path
    foreach($entry in Get-ChildItem -LiteralPath $safe -Force){
        $null=Assert-SuitePath $entry.FullName
        if($entry.PSIsContainer){Remove-DiscardDirectory $entry.FullName}else{Remove-Item -LiteralPath $entry.FullName -Force}
    }
    Remove-Item -LiteralPath $safe -Force
}

$before=Get-DiscardSnapshot
# Quiesce the parent before helpers so requests cannot create another credential writer.
if($before.Main){
    $main=Get-DiscardProcess $before.Record
    if($main){Stop-Process -InputObject $main -Force;if(!$main.WaitForExit(10000)){throw 'Portal did not exit within the stop budget; credential files and receipts preserved.'}}
}
$current=Get-DiscardSnapshot
Assert-DiscardRegistration $before.Record $current.Record
if($current.Main){throw 'A replacement portal is running; no replacement process was stopped and credentials are preserved.'}
foreach($child in $current.Children){
    $process=Get-DiscardProcess $child.Record $current.Record.pid $child.Kind
    if(!$process){continue}
    # Preserve the existing MCP leaf authority over its verified server descendants.
    if($child.Kind -in @('bicep','azure-mcp','ado','aspire')){$process.Kill($true)}else{Stop-Process -InputObject $process -Force}
    if(!$process.WaitForExit(10000)){throw 'Portal helper did not exit within the stop budget; credential files and receipts preserved.'}
}
$cleanup=Get-DiscardSnapshot
Assert-DiscardRegistration $current.Record $cleanup.Record
if($cleanup.Main -or @($cleanup.Children|Where-Object Process).Count){throw 'Portal credential writers remain; credential cleanup deferred.'}
# Only after every registered writer is proven absent may exact owned state be discarded.
foreach($directory in $cleanup.AzureFolders){if(Test-Path -LiteralPath $directory){Assert-DiscardDirectory $directory;Remove-DiscardDirectory $directory}}
foreach($path in $cleanup.Files){$safe=Assert-SuitePath $path;if(Test-Path -LiteralPath $safe){Remove-Item -LiteralPath $safe -Force}}
$registered=Read-DiscardReceipt $receipt
Assert-DiscardRegistration $cleanup.Record $registered
if(Get-DiscardProcess $registered){throw 'A portal started before receipt cleanup; its receipt is preserved.'}
$null=Assert-SuitePath $receipt
Remove-Item -LiteralPath $receipt -Force
Write-Host 'Portal stopped; local reports and logs preserved.'
