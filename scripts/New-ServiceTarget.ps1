#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{3,10}$')][string]$Workload,
    [Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')][string]$SubscriptionAlias,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')][string]$NetworkProfile,
    [Parameter(Mandatory)][string]$IntegrationSubnetId,
    [Parameter(Mandatory)][string]$PrivateEndpointSubnetId,
    [Parameter(Mandatory)][string]$ServiceConnectionId,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9]{1,3}$')][string]$OrganizationCode,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9]{1,4}$')][string]$RegionCode,
    [ValidatePattern('^[0-9]{3}$')][string]$Instance='001',
    [string]$AgentPool='blob-transfer-private', [string]$ResourceGroup='',
    [string]$DeploymentPrincipalObjectId='', [hashtable]$PrivateDnsZoneIds=@{},
    [string]$ParameterFile='', [string]$OutputDirectory=''
)
. "$PSScriptRoot/self-service-common.ps1"
$inventory=Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json -AsHashtable
if ($inventory.schemaVersion -ne 1 -or $inventory.readOnly -isnot [bool] -or !$inventory.readOnly) { throw 'Expected a read-only discovery inventory.' }
$connections=@($inventory.serviceConnections | Where-Object { $_.id -ieq $ServiceConnectionId -and $_.subscriptionId -ieq $inventory.subscription.id -and $_.ready -eq $true })
if ($connections.Count -ne 1) { throw 'Select exactly one ready service connection from this subscription inventory.' }
$connection=$connections[0]
if ($connection.scheme -ne 'WorkloadIdentityFederation') { throw 'This catalog requires a federated service connection.' }
$null=[guid]::Parse($connection.id)
if (!$DeploymentPrincipalObjectId) { $DeploymentPrincipalObjectId=$connection.principalObjectId }
if (!$DeploymentPrincipalObjectId -or [guid]::Parse($DeploymentPrincipalObjectId) -eq [guid]::Empty) { throw 'A verified service principal OBJECT ID is required; application/client ID is not sufficient.' }
if ($connection.principalObjectId -and $connection.principalObjectId -ine $DeploymentPrincipalObjectId) { throw 'Principal differs from the discovered service connection identity.' }
$vnets=@($inventory.networks | Where-Object { @($_.subnets | Where-Object { $_.id -ieq $IntegrationSubnetId -and $_.integrationCandidate }).Count -eq 1 -and @($_.subnets | Where-Object { $_.id -ieq $PrivateEndpointSubnetId -and $_.privateEndpointCandidate }).Count -eq 1 })
if ($vnets.Count -ne 1) { throw 'Select eligible integration and private endpoint subnets from the same discovered VNet.' }
$zones=@{}
foreach ($key in @('blob','queue','table','dfs','web')) {
    if ($PrivateDnsZoneIds.ContainsKey($key)) { $zones[$key]=$PrivateDnsZoneIds[$key]; continue }
    $name=if ($key -eq 'web') {'privatelink.azurewebsites.net'} else {"privatelink.$key.core.windows.net"}
    $matches=@($inventory.privateDnsZones | Where-Object { $_.name -ieq $name })
    if ($matches.Count -ne 1) { throw "DNS zone '$name' is missing or ambiguous; supply its approved ID using -PrivateDnsZoneIds." }
    $zones[$key]=$matches[0].id
}
$suffix="$OrganizationCode-$RegionCode-$Instance"
$stem="$Workload-$EnvironmentName-$suffix"
if (!$ResourceGroup) { $ResourceGroup="rg-$stem" }
if (!$ParameterFile) { $ParameterFile="environments/$EnvironmentName.bicepparam" }
$target=@{schemaVersion=2;enabled=$false;workload=$Workload;environmentName=$EnvironmentName;subscriptionId=$inventory.subscription.id;subscriptionAlias=$SubscriptionAlias;networkProfile=$NetworkProfile;resourceGroup=$ResourceGroup;parameterFile=$ParameterFile;serviceConnection=$connection.id;agentPool=$AgentPool;deploymentEnvironment=$stem;smokePrefix='smoke/';parameterOverrides=@{
    namingSuffix=$suffix;location=$vnets[0].location;networkMode='existing';deploymentPrincipalObjectId=$DeploymentPrincipalObjectId;
    existingNetwork=@{integrationSubnetId=$IntegrationSubnetId;privateEndpointSubnetId=$PrivateEndpointSubnetId;privateDnsZoneIds=$zones}
}}
Assert-ServiceTarget $target $Workload $EnvironmentName -AllowDisabled
Assert-ServiceNetworkIds $target $target.parameterOverrides.existingNetwork
if ($suffix.Length -gt 14) { throw 'Naming components exceed the 14-character suffix limit.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path (Get-ProjectRoot) 'self-service/targets' }
$path=Join-Path $OutputDirectory "$Workload.$EnvironmentName.$SubscriptionAlias.$NetworkProfile.json"
if (Test-Path -LiteralPath $path) { throw 'Target file already exists. Review/edit it rather than overwriting its approved mapping.' }
Write-ServiceJson $target $path
Write-Host "Disabled target written: $path"
Write-Host 'Review parameters, existing RG, network, identity and approvals, then run Update-ServiceCatalog.ps1. No Azure changes were made.'
