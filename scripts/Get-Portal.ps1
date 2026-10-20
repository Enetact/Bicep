#requires -Version 7.4
[CmdletBinding()]
param()
# Keep the established ownership/readiness projection available independently of mutations.
& (Join-Path $PSScriptRoot 'Manage-Portal.ps1') -Action Status
