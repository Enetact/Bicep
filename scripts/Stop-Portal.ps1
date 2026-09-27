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
    Stop-Process -Id $p.Id
    $p.WaitForExit(10000)|Out-Null
}
Remove-Item -LiteralPath $receipt
Write-Host 'Portal stopped; local reports and logs preserved.'
