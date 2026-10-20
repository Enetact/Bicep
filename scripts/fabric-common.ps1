# Microsoft Fabric Azure infrastructure adapter. Workspace/item delivery is a separate protected Fabric REST workflow.
function Get-FabricParameterKeys {
    return @('capacitySku','capacityAdministrators','enableWorkspacePrivateLink','existingFabricWorkspaceId','privateEndpointSubnetId','privateDnsZoneIds')
}
function Assert-FabricParameters($Target,$P) {
    if($P.capacitySku.value -cnotin @('F64','F128','F256','F512','F1024','F2048')){throw 'Fabric premium profile requires a reviewed F64-or-larger capacity SKU.'}
    $admins=$P.capacityAdministrators.value
    if($admins -isnot [array] -or !$admins.Count -or $admins.Count -gt 20){throw 'Configure one to twenty reviewed Fabric capacity administrator user identities.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($admin in $admins){if($admin -isnot [string] -or $admin.Length -gt 254 -or $admin -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+%-]*@[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$' -or !$seen.Add($admin)){throw 'Fabric capacity administrators must be distinct reviewed user principal names.'}}
    if($P.enableWorkspacePrivateLink.value -isnot [bool]){throw 'Fabric workspace private link selection must be Boolean.'}
    if($P.privateDnsZoneIds.value -isnot [Collections.IDictionary]){throw 'Fabric private DNS bindings must be an object.'}
    if($P.enableWorkspacePrivateLink.value){
        $workspace=[guid]::Parse($P.existingFabricWorkspaceId.value)
        if($workspace -eq [guid]::Empty){throw 'Private Link requires a reviewed existing Fabric workspace ID.'}
        $scope='^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/'
        if($P.privateEndpointSubnetId.value -notmatch ($scope+'Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$')){throw 'Fabric endpoint subnet must be in the registered subscription.'}
        $zones=$P.privateDnsZoneIds.value
        if($zones.Count -ne 1 -or !$zones.Contains('fabric') -or $zones.fabric -notmatch ($scope+'Microsoft.Network/privateDnsZones/privatelink\.fabric\.microsoft\.com$')){throw 'Supply exactly the reviewed privatelink.fabric.microsoft.com DNS zone binding.'}
    }elseif($P.existingFabricWorkspaceId.value -or $P.privateEndpointSubnetId.value -or $P.privateDnsZoneIds.value.Count){throw 'Fabric private link bindings must be empty until the private link pass is explicitly selected.'}
}
function Assert-FabricDiscovery($Target,$P,$Inventory) {
    if(!$P.enableWorkspacePrivateLink.value){return}
    $vnetId=$P.privateEndpointSubnetId.value -replace '/subnets/[^/]+$',''
    $networks=@($Inventory.networks|Where-Object {$_.id -ieq $vnetId -and $_.location -ieq $P.location.value -and $_.subnetQuery.status -ceq 'Succeeded'})
    if($networks.Count -ne 1 -or @($networks[0].subnets|Where-Object {$_.id -ieq $P.privateEndpointSubnetId.value -and $_.privateEndpointCandidate -eq $true}).Count -ne 1){throw 'Fabric endpoint subnet discovery is missing, failed or ineligible.'}
    if(@($Inventory.privateDnsZones|Where-Object {$_.id -ieq $P.privateDnsZoneIds.value.fabric}).Count -ne 1){throw 'Fabric private DNS zone is missing from discovery.'}
}
function Test-FabricPrerequisites($Bundle) {
    $p=$Bundle.parameters.parameters;$evidence=@{workspaceDelivery='Separate protected Fabric REST Preview/Apply/Verify; Azure capacity readiness does not verify workspace readiness.'}
    if(!$p.enableWorkspacePrivateLink.value){return $evidence}
    $vnetId=$p.privateEndpointSubnetId.value -replace '/subnets/[^/]+$',''
    $vnet=Invoke-ServiceJson @('resource','show','--ids',$vnetId,'--api-version','2024-05-01')
    if($vnet.id -ine $vnetId -or $vnet.location -ine $p.location.value -or ($vnet.properties.Contains('dhcpOptions') -and @($vnet.properties.dhcpOptions.dnsServers).Count)){throw 'Fabric private link requires the approved same-region VNet with qualified Azure DNS.'}
    $subnet=Invoke-ServiceJson @('resource','show','--ids',$p.privateEndpointSubnetId.value,'--api-version','2024-05-01')
    if($subnet.id -ine $p.privateEndpointSubnetId.value -or @($subnet.properties.delegations).Count -or $subnet.properties.privateEndpointNetworkPolicies -cne 'Disabled'){throw 'Fabric endpoint subnet is no longer eligible.'}
    $links=Get-StackRestCollection "https://management.azure.com$($p.privateDnsZoneIds.value.fabric)/virtualNetworkLinks?api-version=2024-06-01"
    if(@($links|Where-Object {$_.properties.virtualNetwork.id -ieq $vnetId -and $_.properties.provisioningState -ceq 'Succeeded'}).Count -ne 1){throw 'Fabric private DNS zone must have one successful direct VNet link.'}
    # A workspace GUID is a Fabric API identity, never the Azure capacity ARM resource ID.
    $workspace=Invoke-ServiceJson @('rest','--method','get','--url',"https://api.fabric.microsoft.com/v1/workspaces/$($p.existingFabricWorkspaceId.value)",'--resource','https://api.fabric.microsoft.com')
    if($workspace.id -ine $p.existingFabricWorkspaceId.value){throw 'Fabric workspace identity could not be verified.'}
    $evidence.workspaceId=$workspace.id;$evidence.subnetId=$subnet.id;$evidence.dnsZoneId=$p.privateDnsZoneIds.value.fabric
    return $evidence
}
function Test-FabricInfrastructure($Bundle,$Outputs) {
    $p=$Bundle.parameters.parameters
    $capacity=Invoke-ServiceJson @('resource','show','--ids',$Outputs.fabricCapacityResourceId.value,'--api-version','2023-11-01')
    if($capacity.id -ine $Outputs.fabricCapacityResourceId.value -or $capacity.location -ine $p.location.value -or $capacity.sku.name -cne $p.capacitySku.value -or $capacity.sku.tier -cne 'Fabric' -or $capacity.properties.provisioningState -cne 'Succeeded'){throw 'Fabric capacity read-back differs from the approved configuration.'}
    $expected=@($p.capacityAdministrators.value|ForEach-Object {$_.ToLowerInvariant()}|Sort-Object)
    $actual=@($capacity.properties.administration.members|ForEach-Object {$_.ToLowerInvariant()}|Sort-Object)
    if((Get-ValueHash $actual) -cne (Get-ValueHash $expected)){throw 'Fabric capacity administrator read-back differs from reviewed user identities.'}
    $checks=@('Fabric capacity identity, SKU, region, provisioning and administrator configuration')
    if($p.enableWorkspacePrivateLink.value){
        $pe=Invoke-ServiceJson @('resource','show','--ids',$Outputs.workspacePrivateEndpointId.value,'--api-version','2024-05-01')
        $connections=@($pe.properties.privateLinkServiceConnections|Where-Object {$_.properties.privateLinkServiceId -ieq $Outputs.workspacePrivateLinkServiceId.value -and $_.properties.privateLinkServiceConnectionState.status -ceq 'Approved'})
        if($pe.id -ine $Outputs.workspacePrivateEndpointId.value -or $pe.properties.provisioningState -cne 'Succeeded' -or $connections.Count -ne 1){throw 'Fabric workspace private endpoint is not approved and provisioned.'}
        $checks+='Workspace Private Link Azure endpoint approved; Fabric public-access policy and client connectivity need separate acceptance'
    }
    return @{checks=$checks;dataPlaneVerified=$false;workspaceDeliveryVerified=$false;note='Azure capacity and selected endpoint configuration only. Fabric workspace, items, connections, deployment pipeline, deny-public policy and data access require separate Fabric Verify and live acceptance.'}
}
