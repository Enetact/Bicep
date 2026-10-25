# Reviewed Managed DevOps Pool adapter. Shared network and Azure DevOps grants are prerequisites, never inferred mutations.
function Get-MdpParameterKeys {
    @('devCenterProjectResourceId','agentSubnetId','adoOrganizationUrl','adoPoolName','adoProjectNames','poolAdministratorGroupEmails','maximumConcurrency','agentVmSize','agentImageVersion','devOpsInfrastructurePrincipalObjectId')
}
function Get-MdpArray($Map,[string]$Key) {
    if($Map.Contains($Key) -and $null -ne $Map[$Key]){foreach($item in $Map[$Key]){if($null -ne $item){$item}}}
}
function Get-MdpPoolName($P) {
    if($P.Contains('resourceNames') -and $P.resourceNames.value -and $P.resourceNames.value.Contains('pool')){return [string]$P.resourceNames.value.pool}
    return ''
}
function Test-MdpPoolName($P,[string]$Name) {
    $reviewed=Get-MdpPoolName $P
    if($reviewed){return $Name -ceq $reviewed}
    return $Name -cmatch ('^mdp-'+[regex]::Escape($P.workload.value)+'-'+[regex]::Escape($P.environmentName.value)+'-[a-z0-9]{6}$')
}
function Assert-MdpParameters($Target,$P) {
    $scope='^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    if($P.devCenterProjectResourceId.value -cnotmatch ($scope+'Microsoft.DevCenter/projects/[a-zA-Z0-9][a-zA-Z0-9-]{2,62}$')){throw 'Managed pool requires a reviewed existing same-subscription DevCenter project.'}
    if($P.agentSubnetId.value -cnotmatch ($scope+'Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$')){throw 'Managed pool requires an existing dedicated subnet in its subscription.'}
    if($P.adoOrganizationUrl.value -cnotmatch '^https://dev\.azure\.com/[A-Za-z0-9][A-Za-z0-9-]{0,49}$'){throw 'Use one reviewed Azure DevOps organization URL; credentials, paths and query strings are forbidden.'}
    if($P.adoPoolName.value -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,62}[A-Za-z0-9]$'){throw 'Use a reviewed Azure DevOps agent pool alias (3-64 safe characters).'}
    if($Target.agentPool -ieq $P.adoPoolName.value -or (Test-MdpPoolName $P $Target.agentPool.ToLowerInvariant())){throw 'Bootstrap agent pool must already exist and differ from the new Azure pool and ADO alias.'}
    $projects=$P.adoProjectNames.value
    if($projects -isnot [array] -or !$projects.Count -or $projects.Count -gt 10){throw 'Supply one to ten explicit approved Azure DevOps projects; all-project access is unsupported.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($project in $projects){if($project -isnot [string] -or $project -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$' -or $project.Trim() -cne $project -or !$seen.Add($project)){throw 'Azure DevOps project names must be distinct reviewed plain names.'}}
    $groups=$P.poolAdministratorGroupEmails.value
    if($groups -isnot [array] -or !$groups.Count -or $groups.Count -gt 20){throw 'Supply one to twenty reviewed administrator group email addresses.'}
    $seen.Clear()
    foreach($group in $groups){if($group -isnot [string] -or $group.Length -gt 254 -or $group -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+%-]*@[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$' -or !$seen.Add($group)){throw 'Pool administrators must be distinct reviewed security-group emails, not object IDs.'}}
    if(($P.maximumConcurrency.value -isnot [int] -and $P.maximumConcurrency.value -isnot [long]) -or $P.maximumConcurrency.value -lt 1 -or $P.maximumConcurrency.value -gt 10){throw 'Managed pool concurrency must be an integer from one to ten.'}
    if($P.agentVmSize.value -cnotin @('Standard_D2ds_v5','Standard_D4ds_v5')){throw 'VM size is outside the reviewed Windows Gen2 profile.'}
    if($P.agentImageVersion.value -cnotmatch '^20[0-9]{6}\.[0-9]{1,4}\.[0-9]{1,4}$'){throw 'Resolve an approved published immutable windows-2022-g2 image version; latest and guessed labels are unsupported.'}
    if($P.devOpsInfrastructurePrincipalObjectId.value -cnotmatch '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -or [guid]$P.devOpsInfrastructurePrincipalObjectId.value -eq [guid]::Empty){throw 'Supply the observed tenant DevOpsInfrastructure service-principal object ID.'}
}
function Get-MdpCidr([string]$Cidr) {
    if($Cidr -cnotmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/(\d{1,2})$'){throw 'Managed pool requires reviewed IPv4 network prefixes.'}
    $octets=@([int]$Matches[1],[int]$Matches[2],[int]$Matches[3],[int]$Matches[4]);$prefix=[int]$Matches[5]
    if(@($octets|Where-Object {$_ -gt 255}).Count -or $prefix -gt 32){throw 'Invalid IPv4 network prefix.'}
    $number=[uint64]$octets[0]*16777216+[uint64]$octets[1]*65536+[uint64]$octets[2]*256+[uint64]$octets[3]
    $size=[uint64][Math]::Pow(2,32-$prefix);$start=[uint64]([Math]::Floor($number/$size)*$size)
    if($number -ne $start){throw 'Supply network-aligned IPv4 prefixes.'}
    return @{start=$start;end=$start+$size-1;size=$size}
}
function Assert-MdpSubnet($P,$Subnet,$Vnet,[string]$OwnedPoolId='') {
    if($Vnet.id -ine ($P.agentSubnetId.value -replace '/subnets/[^/]+$','') -or $Vnet.location -ine $P.location.value -or $Subnet.id -ine $P.agentSubnetId.value){throw 'Managed pool subnet/VNet identity or region changed.'}
    $props=if($Subnet.Contains('properties')){$Subnet.properties}else{$Subnet}
    $vnetProps=if($Vnet.Contains('properties')){$Vnet.properties}else{$Vnet}
    $delegations=@($props.delegations|ForEach-Object {if($_ -is [string]){$_}elseif($_.Contains('properties')){$_.properties.serviceName}else{$_.serviceName}})
    if($delegations.Count -ne 1 -or $delegations[0] -cne 'Microsoft.DevOpsInfrastructure/pools'){throw 'Agent subnet must be exclusively delegated to Microsoft.DevOpsInfrastructure/pools.'}
    $prefixes=@(if($props.Contains('addressPrefixes')){@($props.addressPrefixes)}else{@($props.addressPrefix)})
    if($prefixes.Count -ne 1){throw 'Initial managed pool profile requires one dedicated IPv4 subnet prefix.'}
    $subnetRange=Get-MdpCidr $prefixes[0]
    if($subnetRange.size -lt ([int]$P.maximumConcurrency.value+5)){throw 'Agent subnet lacks address capacity for maximum concurrency plus Azure reserved addresses.'}
    $vnetPrefixes=@(if($vnetProps.Contains('addressSpace')){@($vnetProps.addressSpace.addressPrefixes)}else{@($vnetProps.addressPrefixes)})
    if(!$vnetPrefixes.Count){throw 'VNet address evidence is incomplete.'}
    $reserved=Get-MdpCidr '172.17.0.0/16'
    foreach($prefix in $vnetPrefixes){$range=Get-MdpCidr $prefix;if($range.start -le $reserved.end -and $range.end -ge $reserved.start){throw 'VNet overlaps Managed DevOps Pools internal 172.17.0.0/16 range.'}}
    if(@(Get-MdpArray $props privateEndpoints).Count -or ($props.Contains('privateEndpointCount') -and [int]$props.privateEndpointCount -gt 0) -or @(Get-MdpArray $props resourceNavigationLinks).Count){throw 'Agent subnet is shared with unrelated endpoints or service resources.'}
    $links=@(Get-MdpArray $props serviceAssociationLinks);$configs=@(Get-MdpArray $props ipConfigurations)
    if($props.Contains('ipConfigurationCount') -and [int]$props.ipConfigurationCount -gt 0 -and !$configs.Count){$configs=@('recorded')}
    if($links.Count -or $configs.Count){
        if(!$OwnedPoolId -or !$links.Count){throw 'Agent subnet is already occupied or associated; platform ownership review required.'}
        foreach($link in $links){$details=if($link.Contains('properties')){$link.properties}else{$link};if($details.link -ine $OwnedPoolId -or $details.linkedResourceType -cne 'Microsoft.DevOpsInfrastructure/pools'){throw 'Agent subnet is associated with a different resource.'}}
    }
}
function Assert-MdpDiscovery($Target,$P,$Inventory) {
    $project=@($Inventory.resources|Where-Object {$_.id -ieq $P.devCenterProjectResourceId.value -and $_.type -ceq 'Microsoft.DevCenter/projects'})
    if($project.Count -ne 1){throw 'Approved existing DevCenter project is missing from saved discovery.'}
    $vnetId=$P.agentSubnetId.value -replace '/subnets/[^/]+$',''
    $nets=@($Inventory.networks|Where-Object {$_.id -ieq $vnetId -and $_.subnetQuery.status -ceq 'Succeeded'})
    if($nets.Count -ne 1){throw 'Dedicated agent subnet discovery is missing or partial.'}
    $subnets=@($nets[0].subnets|Where-Object {$_.id -ieq $P.agentSubnetId.value})
    if($subnets.Count -ne 1){throw 'Selected agent subnet is missing.'}
    # Existing pool subnet occupancy is reconciled against the actual stack in live prerequisites.
    $copy=$subnets[0]|ConvertTo-Json -Depth 50|ConvertFrom-Json -AsHashtable
    $copy.serviceAssociationLinks=@();$copy.ipConfigurations=@();$copy.ipConfigurationCount=0
    Assert-MdpSubnet $P $copy $nets[0]
}
function Test-MdpPrerequisites($Bundle) {
    $p=$Bundle.parameters.parameters;Assert-MdpParameters $Bundle.target $p
    $vnetId=$p.agentSubnetId.value -replace '/subnets/[^/]+$',''
    $vnet=Invoke-ServiceJson @('resource','show','--ids',$vnetId,'--api-version','2024-05-01')
    $subnet=Invoke-ServiceJson @('resource','show','--ids',$p.agentSubnetId.value,'--api-version','2024-05-01')
    $project=Invoke-ServiceJson @('resource','show','--ids',$p.devCenterProjectResourceId.value,'--api-version','2025-02-01')
    if($project.id -ine $p.devCenterProjectResourceId.value -or $project.location -ine $p.location.value -or $project.properties.provisioningState -cne 'Succeeded' -or !$project.properties.devCenterId){throw 'DevCenter project identity, region or provisioning is unverified.'}
    $stack=Get-WorkloadStack $Bundle;$ownedPoolId=''
    if($stack -and $stack.properties.outputs.managedDevOpsPoolId.value){
        $poolId=[string]$stack.properties.outputs.managedDevOpsPoolId.value
        $prefix="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)/providers/Microsoft.DevOpsInfrastructure/pools/"
        if(!$poolId.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !(Test-MdpPoolName $p $poolId.Substring($prefix.Length))){throw 'Managed pool stack output identity is outside the reviewed source scope.'}
        if(@($stack.properties.resources|Where-Object {$_.id -ieq $poolId -and (!$_.Contains('status') -or $_.status -ieq 'managed')}).Count -eq 1){$ownedPoolId=$poolId}
    }
    Assert-MdpSubnet $p $subnet $vnet $ownedPoolId
    $grants=@(Invoke-ServiceJson @('role','assignment','list','--assignee-object-id',$p.devOpsInfrastructurePrincipalObjectId.value,'--scope',$vnetId,'--include-inherited','--all'))
    foreach($role in @('acdd72a7-3385-48ef-bd42-f606fba81ae7','4d97b98b-1d4f-4787-a291-c67834d212e7')){
        if(!@($grants|Where-Object {$_.principalId -ieq $p.devOpsInfrastructurePrincipalObjectId.value -and $_.principalType -ceq 'ServicePrincipal' -and $_.roleDefinitionId -imatch ('/'+$role+'$') -and !$_['condition'] -and ($vnetId -ieq $_.scope -or $vnetId.StartsWith($_.scope.TrimEnd('/')+'/',[StringComparison]::OrdinalIgnoreCase))}).Count){throw 'DevOpsInfrastructure needs reviewed unconditional Reader and Network Contributor grants on the VNet (or inherited); this workflow does not grant them.'}
    }
    return @{devCenterProjectId=$project.id;agentSubnetId=$subnet.id;devOpsInfrastructurePrincipalObjectId=$p.devOpsInfrastructurePrincipalObjectId.value;networkRoleAssignments=@($grants|ForEach-Object {@{id=$_.id;principalId=$_.principalId;roleDefinitionId=$_.roleDefinitionId;scope=$_.scope}});subnetProperties=$subnet.properties;vnetAddressSpace=$vnet.properties.addressSpace;note='Configuration preconditions only. DNS/egress, VM quota/image availability, tenant service-principal provenance, Azure DevOps permissions and a real consumer job require platform acceptance.'}
}
function Test-MdpInfrastructure($Bundle,$Outputs) {
    $p=$Bundle.parameters.parameters
    $pool=Invoke-ServiceJson @('resource','show','--ids',$Outputs.managedDevOpsPoolId.value,'--api-version','2026-06-02')
    if($pool.id -ine $Outputs.managedDevOpsPoolId.value -or $pool.name -cne $Outputs.managedDevOpsPoolName.value -or !(Test-MdpPoolName $p $pool.name) -or $Outputs.agentPoolName.value -cne $p.adoPoolName.value -or $pool.location -ine $p.location.value -or $pool.properties.provisioningState -cne 'Succeeded' -or ($pool['identity'] -and $pool.identity.type -cne 'None')){throw 'Managed pool identity, region, provisioning or ambient identity differs from reviewed input.'}
    $v=$pool.properties;$f=$v.fabricProfile;$org=$v.organizationProfile;$predictions=$v.agentProfile['resourcePredictionsProfile']
    if($v.devCenterProjectResourceId -ine $p.devCenterProjectResourceId.value -or $v.maximumConcurrency -ne $p.maximumConcurrency.value -or $v.agentProfile.kind -cne 'Stateless' -or ($predictions -and ($predictions.kind -cne 'Manual' -or @($predictions.Keys | Where-Object {$_ -cne 'kind'}).Count)) -or $v.agentProfile['resourcePredictions'] -or $f.kind -cne 'Vmss' -or $f.networkProfile.subnetId -ine $p.agentSubnetId.value -or $f.networkProfile['staticIpAddressCount'] -or $f.sku.name -cne $p.agentVmSize.value -or $f.osProfile.logonType -cne 'Service' -or $f.storageProfile.osDiskStorageAccountType -cne 'StandardSSD' -or @($f.storageProfile.dataDisks).Count){throw 'Managed pool stateless network, sizing or storage configuration drifted.'}
    if(@($f.images).Count -ne 1 -or $f.images[0].wellKnownImageName -cne ('windows-2022-g2/'+$p.agentImageVersion.value) -or (Get-ValueHash @($f.images[0].aliases)) -cne (Get-ValueHash @('platform-windows'))){throw 'Managed pool image differs from its reviewed immutable Gen2 pin.'}
    if($org.kind -cne 'AzureDevOps' -or @($org.organizations).Count -ne 1 -or $org.organizations[0].url -cne $p.adoOrganizationUrl.value -or $org.organizations[0].alias -cne $p.adoPoolName.value -or $org.organizations[0].openAccess -ne $false -or $org.organizations[0].parallelism -ne $p.maximumConcurrency.value -or (Get-ValueHash @($org.organizations[0].projects|Sort-Object)) -cne (Get-ValueHash @($p.adoProjectNames.value|Sort-Object)) -or $org.permissionProfile.kind -cne 'SpecificAccounts' -or @($org.permissionProfile.users).Count -or (Get-ValueHash @($org.permissionProfile.groups|Sort-Object)) -cne (Get-ValueHash @($p.poolAdministratorGroupEmails.value|Sort-Object))){throw 'Managed pool Azure DevOps project/alias/admin settings differ from reviewed source.'}
    return @{checks=@('ARM managed pool scope, immutable Gen2 image, stateless zero-standby profile, concurrency, subnet and explicit ADO configuration read-back');agentJobVerified=$false;networkConnectivityVerified=$false;dataPlaneVerified=$false;note='InfrastructureReady only. Azure DevOps creation-time policy, pipeline authorization, DevOpsInfrastructure provenance, image/VM quota, outbound and private endpoint connectivity require direct acceptance and a real consumer job.'}
}
