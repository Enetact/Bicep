# Private Data Platform contract. Azure operations remain behind the shared mockable reader.
function Assert-DataPlatformParameters($Target,$P) {
    if($P.inputFileName.value -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\.csv$'){throw 'Input must be one approved CSV filename in landing/inbox.'}
    if(($P.databricksJobId.value -isnot [int] -and $P.databricksJobId.value -isnot [long]) -or $P.databricksJobId.value -lt 0 -or $P.databricksJobId.value -gt 9007199254740991){throw 'Job binding must be zero or a reviewed positive safe integer.'}
    $prefix='^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    $vnet=$P.privateEndpointSubnetId.value -replace '/subnets/[^/]+$',''
    $subnets=@($P.privateEndpointSubnetId.value,$P.databricksHostSubnetId.value,$P.databricksContainerSubnetId.value)
    if(@($subnets|Sort-Object -Unique).Count -ne 3){throw 'Data platform needs three distinct subnets.'}
    foreach($id in $subnets){if($id -notmatch ($prefix+'Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$') -or ($id -replace '/subnets/[^/]+$','') -ine $vnet){throw 'Data platform subnets must share the approved same-subscription VNet.'}}
    if($P.browserAuthenticationPrivateEndpointId.value -notmatch ($prefix+'Microsoft.Network/privateEndpoints/[^/]+$')){throw 'Specify the approved shared browser authentication endpoint.'}
    if($P.sparkVersion.value -cnotmatch '^\d+\.\d+\.x-scala2\.\d+$'){throw 'Select and qualify a Databricks runtime key.'}
    if($P.nodeType.value -cnotin @('Standard_D4ds_v5','Standard_D8ds_v5') -or $P.maxWorkers.value -isnot [long] -and $P.maxWorkers.value -isnot [int] -or $P.maxWorkers.value -lt 1 -or $P.maxWorkers.value -gt 4){throw 'Unapproved bounded Databricks compute profile.'}
    if($P.notebookPath.value -cnotmatch '^/Shared/platform-studio/[a-zA-Z0-9][a-zA-Z0-9/_-]{0,119}$' -or $P.notebookPath.value.Contains('//')){throw 'Notebook must use the reviewed platform-studio workspace folder.'}
    if($P.workload.value -cnotmatch '^[a-z][a-z0-9]{2,9}$'){throw 'Data platform naming profile requires 3-10 lowercase alphanumeric characters.'}
}
function Assert-DataPlatformDiscovery($Target,$P,$Inventory) {
    foreach($key in @('databricksHostSubnetId','databricksContainerSubnetId')){
        $found=@($Inventory.networks|Where-Object {$_.location -ieq $P.location.value -and $_.subnetQuery.status -eq 'Succeeded'}|ForEach-Object {$_.subnets}|Where-Object {$_.id -ieq $P[$key].value})
        if($found.Count -ne 1){throw "Data platform subnet absent or unknown: $key"}
        $s=$found[0]
        if(@($s.delegations).Count -ne 1 -or $s.delegations[0] -cne 'Microsoft.Databricks/workspaces' -or @($s.addressPrefixes).Count -ne 1 -or $s.addressPrefixes[0] -notmatch '^\d+\.\d+\.\d+\.\d+/(\d+)$' -or [int]$Matches[1] -gt 26){throw 'Databricks subnets require dedicated delegation and IPv4 /26 or larger in this profile.'}
    }
    if(@($Inventory.resources|Where-Object {$_.id -ieq $P.browserAuthenticationPrivateEndpointId.value -and $_.type -ieq 'Microsoft.Network/privateEndpoints'}).Count -ne 1){throw 'Shared browser-auth endpoint is absent from discovery; provision it in the regional platform first.'}
}
function Get-DataPlatformResource([string]$Id,[string]$Version) {
    $r=Invoke-ServiceJson @('resource','show','--ids',$Id,'--api-version',$Version)
    if($r.id -ine $Id -or $r.properties.provisioningState -cne 'Succeeded'){throw "Data platform dependency not ready or wrong identity: $Id"}
    return $r
}
function Test-DataPlatformPrerequisites($Bundle) {
    $p=$Bundle.parameters.parameters;$t=$Bundle.target;$state=@{}
    $vnet=$p.privateEndpointSubnetId.value -replace '/subnets/[^/]+$',''
    $scope='^/subscriptions/'+[regex]::Escape($t.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    $natIds=@()
    $endpointSubnet=Get-DataPlatformResource $p.privateEndpointSubnetId.value '2024-05-01'
    $endpointPrefixes=@(if($endpointSubnet.properties.Contains('addressPrefixes')){$endpointSubnet.properties.addressPrefixes}else{$endpointSubnet.properties.addressPrefix})
    if($endpointPrefixes.Count -ne 1 -or $endpointPrefixes[0] -notmatch '^\d+\.\d+\.\d+\.\d+/(\d+)$' -or [int]$Matches[1] -gt 28){throw 'Data platform endpoint subnet requires IPv4 /28 or larger; platform IPAM must separately confirm capacity.'}
    $state.endpointSubnet=$endpointSubnet
    foreach($key in @('databricksHostSubnetId','databricksContainerSubnetId')){
        $r=Get-DataPlatformResource $p[$key].value '2024-05-01';$s=$r.properties
        $delegations=@($s.delegations|ForEach-Object {$_.properties.serviceName})
        $cidrs=@(if($s.Contains('addressPrefixes')){$s.addressPrefixes}else{$s.addressPrefix})
        if($delegations.Count -ne 1 -or $delegations[0] -cne 'Microsoft.Databricks/workspaces' -or $cidrs.Count -ne 1 -or $cidrs[0] -notmatch '^\d+\.\d+\.\d+\.\d+/(\d+)$' -or [int]$Matches[1] -gt 26 -or ($s.Contains('privateEndpoints') -and @($s.privateEndpoints).Count)){throw 'Databricks subnet delegation/size/private endpoint isolation failed.'}
        if(!$s.Contains('networkSecurityGroup') -or $s.networkSecurityGroup.id -notmatch ($scope+'Microsoft.Network/networkSecurityGroups/[^/]+$')){throw 'Databricks subnets require approved NSGs.'}
        $state[$s.networkSecurityGroup.id]=Get-DataPlatformResource $s.networkSecurityGroup.id '2024-05-01'
        if($s.Contains('routeTable') -and $s.routeTable){throw 'Custom route/firewall profiles require separate qualification; NAT-only data profile cannot infer reachability.'}
        if(!$s.Contains('natGateway') -or $s.natGateway.id -notmatch ($scope+'Microsoft.Network/natGateways/[^/]+$')){throw 'Explicit NAT egress is required on both Databricks compute subnets.'}
        $nat=Get-DataPlatformResource $s.natGateway.id '2024-05-01'
        $addresses=@();if($nat.properties.Contains('publicIpAddresses')){$addresses+=@($nat.properties.publicIpAddresses)};if($nat.properties.Contains('publicIpPrefixes')){$addresses+=@($nat.properties.publicIpPrefixes)}
        if($nat.location -ine $p.location.value -or !$addresses.Count){throw 'NAT egress has no public address/prefix or wrong region.'}
        $state[$key]=$r;$state[$nat.id]=$nat;$natIds+=$nat.id
    }
    if(@($natIds|Sort-Object -Unique).Count -ne 1){throw 'Both compute subnets must use the same approved NAT gateway.'}
    $auth=Get-DataPlatformResource $p.browserAuthenticationPrivateEndpointId.value '2024-05-01'
    if($auth.location -ine $p.location.value -or ($auth.properties.subnet.id -replace '/subnets/[^/]+$','') -ine $vnet){throw 'Browser authentication endpoint must match the region and connected VNet in this profile.'}
    $connections=@($auth.properties.privateLinkServiceConnections)
    if($connections.Count -ne 1 -or @($connections[0].properties.groupIds).Count -ne 1 -or $connections[0].properties.groupIds[0] -cne 'browser_authentication' -or $connections[0].properties.privateLinkServiceConnectionState.status -cne 'Approved'){throw 'Shared Databricks browser authentication endpoint is not approved.'}
    $authWorkspaceId=$connections[0].properties.privateLinkServiceId
    if($authWorkspaceId -notmatch ($scope+'Microsoft.Databricks/workspaces/[^/]+$')){throw 'Unapproved browser authentication workspace scope.'}
    $authWorkspace=Get-DataPlatformResource $authWorkspaceId '2026-01-01'
    if($authWorkspace.location -ine $p.location.value -or $authWorkspace.sku.name -ine 'premium'){throw 'Browser authentication workspace must be Premium in the same region.'}
    $groups=Get-StackRestCollection "https://management.azure.com$($auth.id)/privateDnsZoneGroups?api-version=2024-05-01"
    $configs=@($groups|ForEach-Object {$_.properties.privateDnsZoneConfigs}|Where-Object {$_.properties.privateDnsZoneId -ieq $p.privateDnsZoneIds.value.databricks})
    if($configs.Count -ne 1){throw 'Browser authentication endpoint must use the selected Databricks private DNS zone.'}
    $state.browserAuthentication=$auth;$state.browserDnsGroups=$groups;$state.browserWorkspace=$authWorkspace
    return $state
}
function Test-DataPlatformInfrastructure($Bundle,$Outputs) {
    $db=Get-DataPlatformResource $Outputs.databricksResourceId.value '2026-01-01'
    if($db.sku.name -ine 'premium' -or $db.properties.publicNetworkAccess -cne 'Disabled' -or $db.properties.requiredNsgRules -cne 'NoAzureDatabricksRules' -or $db.properties.defaultStorageFirewall -cne 'Enabled' -or $db.properties.parameters.enableNoPublicIp.value -ne $true){throw 'Databricks private Premium configuration failed.'}
    if($db.properties.workspaceUrl -cnotmatch '^adb-[0-9]+\.[0-9]+\.azuredatabricks\.net$' -or $db.properties.workspaceUrl -cne $Outputs.databricksUrl.value){throw 'Databricks workspace hostname mismatch.'}
    Wait-ProductPrivateHost $db.properties.workspaceUrl
    $managedGroup="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)-databricks-managed"
    if($db.properties.managedResourceGroupId -ine $managedGroup -or $db.properties.parameters.storageAccountName.value -cnotmatch '^dbfs[a-z0-9]{3,20}$'){throw 'Unexpected provider-managed storage binding.'}
    $rootStorageId="$managedGroup/providers/Microsoft.Storage/storageAccounts/$($db.properties.parameters.storageAccountName.value)"
    $rootStorage=Get-DataPlatformResource $rootStorageId '2025-01-01'
    if($rootStorage.properties.networkAcls.defaultAction -cne 'Deny'){throw 'Provider-managed root storage firewall is not enabled.'}
    foreach($service in @('blob','dfs')){Wait-ProductPrivateHost "$($db.properties.parameters.storageAccountName.value).$service.core.windows.net"}
    $factory=Get-DataPlatformResource $Outputs.factoryResourceId.value '2018-06-01'
    if($factory.properties.publicNetworkAccess -cne 'Disabled' -or $factory.identity.type -cne 'SystemAssigned'){throw 'Data Factory private managed identity configuration failed.'}
    $lake=Get-DataPlatformResource $Outputs.storageResourceId.value '2025-01-01'
    if($lake.properties.isHnsEnabled -ne $true){throw 'ADLS Gen2 hierarchical namespace is required.'}
    # Report approval gaps; do not grant approvals to any request, including unrelated endpoints.
    $expected=@{databricks=@($db.id,'databricks_ui_api');'lake-dfs'=@($lake.id,'dfs');'lake-blob'=@($lake.id,'blob')}
    foreach($name in $expected.Keys){
        $id="$($factory.id)/managedVirtualNetworks/default/managedPrivateEndpoints/$name"
        $endpoint=Get-DataPlatformResource $id '2018-06-01'
        if($endpoint.properties.privateLinkResourceId -ine $expected[$name][0] -or $endpoint.properties.groupId -cne $expected[$name][1]){throw 'Data Factory managed endpoint target differs from reviewed composition.'}
        if($endpoint.properties.connectionState.status -cne 'Approved'){throw "Managed private endpoint approval required: $id. Review its exact target-side connection, approve separately, then rerun Preview/Deploy."}
    }
    $rgScope="https://management.azure.com/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)/providers/Microsoft.Network/privateEndpoints"
    $endpoints=Get-StackRestCollection "$($rgScope)?api-version=2024-05-01"
    foreach($pair in @(@($rootStorageId,'blob'),@($rootStorageId,'dfs'),@($db.id,'databricks_ui_api'),@($lake.id,'blob'),@($lake.id,'dfs'),@($factory.id,'dataFactory'),@($factory.id,'portal'))){
        $matches=@($endpoints|Where-Object {$_.properties.subnet.id -ieq $Bundle.parameters.parameters.privateEndpointSubnetId.value}|ForEach-Object {$_.properties.privateLinkServiceConnections}|Where-Object {$_.properties.privateLinkServiceId -ieq $pair[0] -and $pair[1] -cin @($_.properties.groupIds) -and $_.properties.privateLinkServiceConnectionState.status -ceq 'Approved'})
        if($matches.Count -ne 1){throw 'Expected exactly one approved workload private connection for each declared service.'}
    }
    return @{checks=@('Premium private workspace and secure cluster connectivity','Private managed-identity factory','ADLS Gen2','Seven private connections and three managed endpoint approvals');dataPlaneVerified=$false;notebookExecuted=$false;unityCatalogVerified=$false;note='Infrastructure only. Workspace identity grants, Unity Catalog, notebook import and a controlled execution remain mandatory runtime acceptance.'}
}
