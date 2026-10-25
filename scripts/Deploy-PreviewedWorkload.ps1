#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewDirectory,[Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)][string]$BoundServiceConnection,
    [Parameter(Mandatory)][string]$BoundEnvironment,[Parameter(Mandatory)][string]$BoundAgentPool)
. "$PSScriptRoot/PlatformTrace.ps1"
$platformTrace = Start-PlatformTrace -Name 'platform.workflow.previewed_deploy'
try {
. "$PSScriptRoot/workload-preview-common.ps1"
Invoke-PreviewedWorkloadDeployment $PreviewDirectory $BundleDirectory $EvidenceDirectory $BoundServiceConnection $BoundEnvironment $BoundAgentPool -CaptureIac
} catch {
    Set-PlatformTraceFailure -Trace $platformTrace -Exception $_.Exception
    throw
} finally { Complete-PlatformTrace $platformTrace }
