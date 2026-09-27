#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DiscoveryDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Workload='',[string]$EnvironmentName='',[string]$EvaluatedUtc='')
$ErrorActionPreference='Stop'
if(!(Get-Command node -ErrorAction SilentlyContinue)){throw 'Install Node.js 22+ to generate the offline analysis report.'}
$arguments=@((Join-Path $PSScriptRoot analysis/cli.mjs),'--input',$DiscoveryDirectory,'--output',$OutputDirectory)
if($Workload){$arguments+=@('--workload',$Workload)}
if($EnvironmentName){$arguments+=@('--environment',$EnvironmentName)}
if($EvaluatedUtc){$arguments+=@('--at',$EvaluatedUtc)}
& node @arguments
if($LASTEXITCODE -ne 0){throw 'Saved-evidence analysis failed; no deployment decision was produced.'}
