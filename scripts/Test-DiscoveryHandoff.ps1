#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Workload,
    [Parameter(Mandatory)][string]$EnvironmentName,
    [Parameter(Mandatory)][string]$SubscriptionAlias,
    [Parameter(Mandatory)][string]$NetworkProfile,
    [Parameter(Mandatory)][string]$BoundServiceConnection,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$DiscoveryPipelineId,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$DiscoveryRunId,
    [switch]$AllowDisabled
)
. "$PSScriptRoot/discovery-manifest-common.ps1"
$target=Read-ServiceTarget $Workload $EnvironmentName $SubscriptionAlias $NetworkProfile -AllowDisabled:$AllowDisabled
$manifest=Read-DiscoveryManifest $Directory $target $BoundServiceConnection
if (!$env:SYSTEM_ACCESSTOKEN -or !$env:SYSTEM_COLLECTIONURI -or !$env:SYSTEM_TEAMPROJECTID -or !$env:BUILD_REPOSITORY_ID) { throw 'Azure DevOps build-read context is required to verify discovery provenance.' }
$collectionUrl=Resolve-ServiceOrganizationUrl $env:SYSTEM_COLLECTIONURI
$projectId=[guid]::Parse($env:SYSTEM_TEAMPROJECTID).ToString()
$url="${collectionUrl}$projectId/_apis/build/builds/${DiscoveryRunId}?api-version=7.1"
$build=Invoke-RestMethod -Uri $url -Headers @{Authorization="Bearer $env:SYSTEM_ACCESSTOKEN"} -Method Get
Assert-DiscoveryRun $manifest $build $DiscoveryPipelineId $DiscoveryRunId $projectId $env:BUILD_REPOSITORY_ID
Write-Host "Verified discovery run $DiscoveryRunId, subscription $($manifest.subscriptionId). Live deployment preflight will recheck Azure state."
