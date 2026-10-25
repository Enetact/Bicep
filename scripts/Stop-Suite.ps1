#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet('Studio','Full')][string]$Profile='Studio',[switch]$DiscardConnections)
& (Join-Path $PSScriptRoot 'Manage-Suite.ps1') -Action Stop @PSBoundParameters
