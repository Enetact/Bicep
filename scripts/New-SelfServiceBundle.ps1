#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Workload,[Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][string]$ReleaseDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SubscriptionAlias='', [string]$NetworkProfile='', [string]$DiscoveryDirectory='',
    [bool]$CreateDestinationPrivateEndpoints=$true, [bool]$EnableLogAlerts=$true)
. "$PSScriptRoot/discovery-manifest-common.ps1"
. "$PSScriptRoot/service-cost-common.ps1"
. "$PSScriptRoot/platform-contract.ps1"
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Bundle directory already exists; use a fresh run directory.' }
$target=Read-ServiceTarget $Workload $EnvironmentName $SubscriptionAlias $NetworkProfile
$discovery=if ($DiscoveryDirectory) { Read-DiscoveryManifest $DiscoveryDirectory $target $target.serviceConnection } else { $null }
$release=Get-Content (Join-Path $ReleaseDirectory release.json) -Raw | ConvertFrom-Json -AsHashtable
if ($release.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or $release.dirtyWorktree) { throw 'Self-service requires a clean, qualified release receipt.' }
$package=Resolve-ServicePath $ReleaseDirectory ($release.releaseId + '.zip')
if ((Get-ServiceHash $package) -cne $release.packageSha256) { throw 'Release package hash mismatch.' }
$root=Get-ProjectRoot
$commit=& git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -cne $release.sourceCommit) { throw 'Release source does not match selected checkout.' }
$null=Export-Templates -EnvironmentName $EnvironmentName -ParameterPath (Resolve-ServicePath $root $target.parameterFile) -OutputPath $OutputDirectory
$parameters=Get-Content (Join-Path $OutputDirectory parameters.json) -Raw | ConvertFrom-Json -AsHashtable
Set-ServiceProfileParameters $target $parameters.parameters
Set-ServiceDeploymentOptions $target $parameters.parameters $CreateDestinationPrivateEndpoints $EnableLogAlerts
Assert-ServiceParameters $target $parameters.parameters
$platform=Read-PlatformConfiguration
$intent=@(Get-PlatformIntentTargets @($target) $platform)[0]
if ((Get-ServiceParameter $parameters.parameters location '') -cne $intent.region) { throw 'Compiled region differs from the approved platform request.' }
Write-ServiceJson $parameters (Join-Path $OutputDirectory parameters.json)
Copy-Item -LiteralPath $package -Destination (Join-Path $OutputDirectory application.zip)
Copy-Item -LiteralPath (Join-Path $ReleaseDirectory functions.metadata) -Destination $OutputDirectory
Write-ServiceJson $target (Join-Path $OutputDirectory target.json)
$estimate=Get-ServiceCostEstimate $parameters.parameters
Write-ServiceJson $estimate (Join-Path $OutputDirectory cost-estimate.json)
Invoke-Bicep -Arguments @('build',(Join-Path $root workloads/blob-transfer/stack.bicep),'--outfile',(Join-Path $OutputDirectory stack-template.json))
$stackTemplate=Get-Content (Join-Path $OutputDirectory stack-template.json) -Raw | ConvertFrom-Json -AsHashtable
Write-ServiceJson (New-StackContract $target $stackTemplate) (Join-Path $OutputDirectory stack.json)
$files=@{}
foreach ($file in @('main.json','parameters.json','target.json','application.zip','functions.metadata','cost-estimate.json','stack.json','stack-template.json')) { $files[$file]=Get-ServiceHash (Join-Path $OutputDirectory $file) }
$receipt=@{schemaVersion=1;deploymentEngine='deploymentStack';releaseId=$release.releaseId;sourceCommit=$release.sourceCommit;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');files=$files;costEstimateIncluded=$true}
if ($discovery) {
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory discovery) -Force | Out-Null
    foreach ($file in @('manifest.json','inventory.json')) {
        $relative="discovery/$file"
        Copy-Item -LiteralPath (Join-Path $DiscoveryDirectory $file) -Destination (Join-Path $OutputDirectory $relative)
        $files[$relative]=Get-ServiceHash (Join-Path $OutputDirectory $relative)
    }
    $receipt.discoverySource=$discovery.source
}
Write-ServiceJson $receipt (Join-Path $OutputDirectory bundle.json)
$null=Read-ServiceBundle $OutputDirectory
$summaryPath=Join-Path (Split-Path -Parent $OutputDirectory) cost-summary.md
Write-ServiceCostSummary $estimate $summaryPath
if ($env:TF_BUILD -eq 'True') { Write-Host "##vso[task.uploadsummary]$summaryPath" }
Write-Host "Frozen self-service bundle: $OutputDirectory"
