#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][int]$PortalPid,[Parameter(Mandatory)][datetimeoffset]$PortalStartedUtc)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '.local/portal/github-cli'))
if(!(Test-Path -LiteralPath $root)){return}
foreach($folder in Get-ChildItem -LiteralPath $root -Directory){
    if($folder.Name -notmatch '^[a-f0-9]{32}$' -or $folder.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
    $receipt=Join-Path $folder.FullName 'process.json'
    if(!(Test-Path -LiteralPath $receipt)){continue}
    $record=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json
    if($record.parentPid -ne $PortalPid -or ([datetimeoffset]$record.parentStartedUtc).UtcTicks -ne $PortalStartedUtc.UtcTicks){continue}
    $child=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if($child){
        $info=Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.pid)"
        if($info.ParentProcessId -ne $PortalPid -or [IO.Path]::GetFullPath($child.Path) -ne [IO.Path]::GetFullPath($record.executable) -or [IO.Path]::GetFileName($child.Path) -ne 'gh.exe' -or $child.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$record.startedUtc).UtcTicks -or $info.CommandLine -notmatch 'auth (login|token) --hostname github.com'){throw 'GitHub CLI child ownership could not be verified.'}
        Stop-Process -Id $child.Id
        $child.WaitForExit(10000)|Out-Null
    }
    # Exact generated receipt only. Do not touch the user's GitHub credential store.
    try { [IO.File]::Delete($receipt) } catch [IO.DirectoryNotFoundException] { } # Concurrent child cleanup.
}
