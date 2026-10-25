#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([ValidateSet('Studio','Full')][string]$Profile='Studio',[switch]$NoBrowser,[switch]$InstallMissing,[switch]$ResumePending)
# Forward common ShouldProcess parameters unchanged; the coordinator owns the single approval boundary.
& (Join-Path $PSScriptRoot 'Manage-Suite.ps1') -Action Start @PSBoundParameters
