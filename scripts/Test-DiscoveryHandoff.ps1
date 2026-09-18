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
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$DiscoveryRunId
)
. "$PSScriptRoot/discovery-manifest-common.ps1"
$target=Read-ServiceTarget $Workload $EnvironmentName $SubscriptionAlias $NetworkProfile
$manifest=Read-DiscoveryManifest $Directory $target $BoundServiceConnection
if (!$env:SYSTEM_ACCESSTOKEN -or $env:SYSTEM_COLLECTIONURI -cnotmatch '^https://dev\.azure\.com/[a-zA-Z0-9][a-zA-Z0-9-]*/$' -or !$env:SYSTEM_TEAMPROJECTID -or !$env:BUILD_REPOSITORY_ID) { throw 'Azure DevOps build-read context is required to verify discovery provenance.' }
$projectId=[guid]::Parse($env:SYSTEM_TEAMPROJECTID).ToString()
$url="$($env:SYSTEM_COLLECTIONURI)$projectId/_apis/build/builds/${DiscoveryRunId}?api-version=7.1"
$build=Invoke-RestMethod -Uri $url -Headers @{Authorization="Bearer $env:SYSTEM_ACCESSTOKEN"} -Method Get
Assert-DiscoveryRun $manifest $build $DiscoveryPipelineId $DiscoveryRunId $projectId $env:BUILD_REPOSITORY_ID
Write-Host "Verified discovery run $DiscoveryRunId, subscription $($manifest.subscriptionId). Live deployment preflight will recheck Azure state."
