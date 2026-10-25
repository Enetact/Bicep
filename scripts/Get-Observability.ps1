#requires -Version 7.4
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Observability.Common.ps1')
Get-ObservabilityStatus
