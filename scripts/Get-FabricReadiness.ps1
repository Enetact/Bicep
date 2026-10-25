#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9]{2,39}$')][string]$Region
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/fabric-readiness-common.ps1"
# The browser cannot select paths, supply manifests or invoke live prerequisite checks.
Get-FabricReadiness (Split-Path $PSScriptRoot -Parent) $EnvironmentName $Region | ConvertTo-Json -Depth 30 -Compress
