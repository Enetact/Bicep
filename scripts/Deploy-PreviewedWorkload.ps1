#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewDirectory,[Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)][string]$BoundServiceConnection,
    [Parameter(Mandatory)][string]$BoundEnvironment,[Parameter(Mandatory)][string]$BoundAgentPool)
. "$PSScriptRoot/workload-preview-common.ps1"
Invoke-PreviewedWorkloadDeployment $PreviewDirectory $BundleDirectory $EvidenceDirectory $BoundServiceConnection $BoundEnvironment $BoundAgentPool
