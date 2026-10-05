#requires -Version 7.4
# Called by the governed stop script after stopping the verified portal process.
[CmdletBinding()]
param([Parameter(Mandatory)][int]$PortalPid,[Parameter(Mandatory)][datetimeoffset]$PortalStartedUtc)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$cacheRoot=[IO.Path]::GetFullPath((Join-Path $root '.local/portal/azure-cli'))
if(!(Test-Path -LiteralPath $cacheRoot)){return}
for($parent=[IO.DirectoryInfo]::new($cacheRoot);$null -ne $parent;$parent=$parent.Parent){
    if($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refusing linked CLI credential cache.'}
}
foreach($folder in Get-ChildItem -LiteralPath $cacheRoot -Directory){
    $resolved=[IO.Path]::GetFullPath($folder.FullName)
    if($folder.Name -notmatch '^[0-9a-f]{32}$' -or !$resolved.StartsWith($cacheRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){continue}
    if($folder.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing linked CLI session.'}
    $ownerPath=Join-Path $resolved 'owner.json'
    if(!(Test-Path -LiteralPath $ownerPath)){continue}
    $owner=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json
    if($owner.parentPid -ne $PortalPid -or ([datetimeoffset]$owner.parentStartedUtc).UtcTicks -ne $PortalStartedUtc.UtcTicks){continue}
    $childReceipt=Join-Path $resolved 'process.json'
    if(Test-Path -LiteralPath $childReceipt){
        $record=Get-Content -LiteralPath $childReceipt -Raw|ConvertFrom-Json
        $child=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if($child){
            $info=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.pid)"
            if($record.parentPid -ne $PortalPid -or $info.ParentProcessId -ne $PortalPid -or $child.Path -ne $record.executable -or $child.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$record.startedUtc).UtcTicks -or !([IO.Path]::GetFileName($child.Path) -eq 'az.exe' -or ([IO.Path]::GetFileName($child.Path) -eq 'python.exe' -and $info.CommandLine -match 'azure\.cli'))){throw 'CLI process ownership could not be verified; credentials preserved for manual review.'}
            Stop-Process -Id $child.Id
            if(!$child.WaitForExit(10000)){throw 'CLI did not stop; credential cleanup deferred.'}
        }
    }
    # Enumerate one directory at a time, rejecting links before descending or deleting.
    function Remove-OwnedCliDirectory([string]$Path){
        $absolute=[IO.Path]::GetFullPath($Path)
        if(!$absolute.StartsWith($cacheRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'CLI cleanup escaped cache root.'}
        $entry=Get-Item -LiteralPath $absolute -Force
        if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing linked CLI cache entry.'}
        foreach($item in Get-ChildItem -LiteralPath $absolute -Force){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing linked CLI cache entry.'}
            if($item.PSIsContainer){Remove-OwnedCliDirectory $item.FullName}else{Remove-Item -LiteralPath $item.FullName -Force}
        }
        Remove-Item -LiteralPath $absolute -Force
    }
    Remove-OwnedCliDirectory $resolved
}
