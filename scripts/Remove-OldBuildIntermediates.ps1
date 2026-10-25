#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess,ConfirmImpact='Medium')]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[a-f0-9]{64}\z')][string]$CompletedSha256,
    [ValidateRange(2,100)][int]$KeepRecentBuilds=2,
    [ValidateRange(60,525600)][int]$MinimumAgeMinutes=60,
    [string[]]$KeepBuildDirectories=@(),
    [switch]$Apply
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FoundationBuildRetention.ps1')
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
Invoke-FoundationBuildRetention -Root $root -RunDirectory $RunDirectory -CompletedSha256 $CompletedSha256 -KeepRecentBuilds $KeepRecentBuilds -MinimumAgeMinutes $MinimumAgeMinutes -KeepBuildDirectories $KeepBuildDirectories -Apply:$Apply -WhatIf:$WhatIfPreference
