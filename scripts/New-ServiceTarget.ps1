#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{3,10}$')][string]$Workload,
    [Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')][string]$SubscriptionAlias,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')][string]$NetworkProfile,
    [ValidateSet('new','existing')][string]$NetworkMode='existing',
    [string]$IntegrationSubnetId='',
    [string]$PrivateEndpointSubnetId='',
    [string]$Location='',
    [string]$VnetAddressPrefix='',
    [string]$IntegrationSubnetPrefix='',
    [string]$PrivateEndpointSubnetPrefix='',
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
if ($inventory.ContainsKey('discoveryStatus') -and $inventory.discoveryStatus -ne 'Complete') { throw 'Discovery inventory is incomplete. Resolve the failed query and rerun discovery before registering a target.' }
$connections=@($inventory.serviceConnections | Where-Object { $_.id -ieq $ServiceConnectionId -and $_.subscriptionId -ieq $inventory.subscription.id -and $_.ready -eq $true })
if ($connections.Count -ne 1) { throw 'Select exactly one ready service connection from this subscription inventory.' }
$connection=$connections[0]
if ($connection.scheme -ne 'WorkloadIdentityFederation') { throw 'This catalog requires a federated service connection.' }
$null=[guid]::Parse($connection.id)
if (!$DeploymentPrincipalObjectId) { $DeploymentPrincipalObjectId=$connection.principalObjectId }
if (!$DeploymentPrincipalObjectId -or [guid]::Parse($DeploymentPrincipalObjectId) -eq [guid]::Empty) { throw 'A verified service principal OBJECT ID is required; application/client ID is not sufficient.' }
if ($connection.principalObjectId -and $connection.principalObjectId -ine $DeploymentPrincipalObjectId) { throw 'Principal differs from the discovered service connection identity.' }
$suffix="$OrganizationCode-$RegionCode-$Instance"
$stem="$Workload-$EnvironmentName-$suffix"
if (!$ResourceGroup) { $ResourceGroup="rg-$stem" }
$overrides=@{namingSuffix=$suffix;networkMode=$NetworkMode;deploymentPrincipalObjectId=$DeploymentPrincipalObjectId}
if ($NetworkMode -eq 'new') {
    if ($IntegrationSubnetId -or $PrivateEndpointSubnetId -or $PrivateDnsZoneIds.Count) { throw 'New-network mode cannot include existing subnet or DNS IDs. Use existing mode for shared networks.' }
    if ($Location -cnotmatch '^[a-z][a-z0-9]+$') { throw 'New-network mode requires an explicit Azure location, for example eastus2.' }
    $overrides.location=$Location
    $overrides.vnetAddressPrefix=$VnetAddressPrefix
    $overrides.integrationSubnetPrefix=$IntegrationSubnetPrefix
    $overrides.privateEndpointSubnetPrefix=$PrivateEndpointSubnetPrefix
    $networkParameters=@{}; foreach ($key in $overrides.Keys) { $networkParameters[$key]=@{value=$overrides[$key]} }
    Assert-ServiceNewNetwork $networkParameters
    # Keep exact names in the selected RG. Do not adopt a similarly named shared VNet.
    $owned=@($inventory.networks | Where-Object { $_.name -ieq "vnet-$stem" -and $_.resourceGroup -ieq $ResourceGroup })
    if ($owned.Count -gt 1 -or ($owned.Count -eq 1 -and $owned[0].location -ine $Location)) { throw 'The standard-name VNet conflicts with the selected location or is ambiguous.' }
} else {
    if ($VnetAddressPrefix -or $IntegrationSubnetPrefix -or $PrivateEndpointSubnetPrefix) { throw 'Existing-network mode takes subnet IDs, not new address ranges.' }
    $vnets=@($inventory.networks | Where-Object { @($_.subnets | Where-Object { $_.id -ieq $IntegrationSubnetId -and $_.integrationCandidate }).Count -eq 1 -and @($_.subnets | Where-Object { $_.id -ieq $PrivateEndpointSubnetId -and $_.privateEndpointCandidate }).Count -eq 1 })
    if ($vnets.Count -ne 1) { throw 'Select eligible integration and private endpoint subnets from the same discovered VNet, or use -NetworkMode new with approved address ranges to provision a dedicated network.' }
    if ($Location -and $Location -ine $vnets[0].location) { throw 'Selected location differs from the discovered VNet.' }
    $zones=@{}
    foreach ($key in @('blob','queue','table','dfs','web')) {
        if ($PrivateDnsZoneIds.ContainsKey($key)) { $zones[$key]=$PrivateDnsZoneIds[$key]; continue }
        $name=if ($key -eq 'web') {'privatelink.azurewebsites.net'} else {"privatelink.$key.core.windows.net"}
        $matches=@($inventory.privateDnsZones | Where-Object { $_.name -ieq $name })
        if ($matches.Count -ne 1) { throw "DNS zone '$name' is missing or ambiguous; supply its approved ID using -PrivateDnsZoneIds." }
        $zones[$key]=$matches[0].id
    }
    $overrides.location=$vnets[0].location
    $overrides.existingNetwork=@{integrationSubnetId=$IntegrationSubnetId;privateEndpointSubnetId=$PrivateEndpointSubnetId;privateDnsZoneIds=$zones}
}
if (!$ParameterFile) { $ParameterFile="workloads/blob-transfer/environments/main.$EnvironmentName.bicepparam" }
$target=@{schemaVersion=2;enabled=$false;workload=$Workload;environmentName=$EnvironmentName;subscriptionId=$inventory.subscription.id;subscriptionAlias=$SubscriptionAlias;networkProfile=$NetworkProfile;resourceGroup=$ResourceGroup;parameterFile=$ParameterFile;serviceConnection=$connection.id;agentPool=$AgentPool;deploymentEnvironment=$stem;smokePrefix='smoke/';parameterOverrides=$overrides}
Assert-ServiceTarget $target $Workload $EnvironmentName -AllowDisabled
if ($NetworkMode -eq 'existing') { Assert-ServiceNetworkIds $target $target.parameterOverrides.existingNetwork }
if ($suffix.Length -gt 14) { throw 'Naming components exceed the 14-character suffix limit.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path (Get-ProjectRoot) 'self-service/targets' }
$path=Join-Path $OutputDirectory "$Workload.$EnvironmentName.$SubscriptionAlias.$NetworkProfile.json"
if (Test-Path -LiteralPath $path) { throw 'Target file already exists. Review/edit it rather than overwriting its approved mapping.' }
Write-ServiceJson $target $path
Write-Host "Disabled target written: $path"
Write-Host 'Review parameters, stack-owned RG name (do not precreate), network, identity and approvals, then run Update-ServiceCatalog.ps1. Existing RGs need adoption review. No Azure changes were made.'
