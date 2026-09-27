#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$receipt=Join-Path $root '.local/portal/process.json'
if(!(Test-Path -LiteralPath $receipt)){Write-Host 'No portal process is registered.';return}
$record=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
$p=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
if($p){
    $expected=[IO.Path]::GetFullPath((Join-Path $root 'src/SelfService.Portal/bin/Release/net10.0/SelfService.Portal.dll'))
    $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.pid)").CommandLine
    if([IO.Path]::GetFullPath($record.assembly) -ne $expected -or $p.Path -ne $record.executable -or $p.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$record.startedUtc).UtcTicks -or !$command.Contains('"'+$expected+'"')){throw 'Portal process ownership could not be verified. No process was stopped.'}
    # Verify each native Codex child independently before stopping it. Never touch desktop Codex.
    $agentRoot=[IO.Path]::GetFullPath((Join-Path $root '.local/portal/agents'))
    if(Test-Path -LiteralPath $agentRoot){
        foreach($folder in Get-ChildItem -LiteralPath $agentRoot -Directory){
            if($folder.Name -notmatch '^[0-9a-f]{32}$'){continue}
            $childReceipt=Join-Path $folder.FullName 'process.json'
            if(!(Test-Path -LiteralPath $childReceipt)){continue}
            $childRecord=Get-Content -LiteralPath $childReceipt -Raw|ConvertFrom-Json
            if($childRecord.parentPid -ne $record.pid){continue}
            $child=Get-Process -Id $childRecord.pid -ErrorAction SilentlyContinue
            if($child){
                $childInfo=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$childRecord.pid)"
                if($childInfo.ParentProcessId -ne $record.pid -or $child.Path -ne $childRecord.executable -or [IO.Path]::GetFileName($child.Path) -ne 'codex.exe' -or $child.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$childRecord.startedUtc).UtcTicks -or $childInfo.CommandLine -notmatch 'app-server.*stdio://'){throw 'Codex child ownership could not be verified. No further process was stopped.'}
                Stop-Process -Id $child.Id
                $child.WaitForExit(10000)|Out-Null
            }
            # These two exact files are confined to the verified generated agent directory.
            $resolvedFolder=[IO.Path]::GetFullPath($folder.FullName)
            if(!$resolvedFolder.StartsWith($agentRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Agent directory escaped the portal root.'}
            foreach($name in @('auth.json','process.json')){ $file=Join-Path $resolvedFolder $name; if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file} }
        }
    }
    Stop-Process -Id $p.Id
    $p.WaitForExit(10000)|Out-Null
}
Remove-Item -LiteralPath $receipt
Write-Host 'Portal stopped; local reports and logs preserved.'
