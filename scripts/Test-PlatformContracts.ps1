#requires -Version 7.4
. "$PSScriptRoot/self-service-common.ps1"
. "$PSScriptRoot/platform-contract.ps1"
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition) { if (!$Condition) { throw 'Assertion failed.' } }
function Reject([scriptblock]$Body) { $rejected=$false; try { & $Body | Out-Null } catch { $rejected=$true }; Check $rejected }
function Case([string]$Name,[scriptblock]$Body) { & $Body; $results.Add(@{name=$Name;passed=$true}) }
function Clone($Value) { $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable }
$config=Read-PlatformConfiguration
$target=Get-Content (Join-Path (Get-ProjectRoot) self-service/targets/blobcopy.dev.json) -Raw | ConvertFrom-Json -AsHashtable
$target.enabled=$false
$request=@{workloadName='blobcopy';environment='dev';region='eastus2';workloadType='blob-transfer';capabilities=@{storage=$true;observability=$true}}
Case 'intent resolves exact registered target and required capabilities' {
    $result=Resolve-PlatformRequest $request @($target)
    Check (!$result.deploymentEnabled -and $result.target.serviceConnection -eq $target.serviceConnection -and $result.options.createDestinationPrivateEndpoints)
}
foreach ($field in @('subnetId','privateDnsZoneIds','subscription','network','publicAccess','agentPool')) {
    Case "request rejects implementation detail $field" { $bad=Clone $request; $bad[$field]='injected'; Reject { Resolve-PlatformRequest $bad @($target) } }
}
foreach ($pair in @(@('workloadType','private-function-api'),@('region','centralus'),@('workloadName','unregistered'))) {
    Case "unsupported intent $($pair[0])" { $bad=Clone $request; $bad[$pair[0]]=$pair[1]; Reject { Resolve-PlatformRequest $bad @($target) } }
}
Case 'unsupported SQL capability is rejected rather than silently ignored' { $bad=Clone $request; $bad.capabilities.sql=$true; Reject { Resolve-PlatformRequest $bad @($target) } }
Case 'duplicate intent cannot choose arbitrary topology' { $other=Clone $target; $other.networkProfile='other'; Reject { Resolve-PlatformRequest $request @($target,$other) } }
Case 'enabled new-network target needs an explicit architectural exception' { $t=Clone $target; $t.enabled=$true; Reject { Get-PlatformIntentTargets @($t) } }
Case 'documented isolated exception permits only the specified target' {
    $t=Clone $target; $t.enabled=$true; $c=Clone $config
    $c.isolatedNetworkExceptions=@(@{workloadName='blobcopy';environment='dev';region='eastus2';reason='Dedicated disconnected test subscription without shared DNS.';reviewReference='test-only-ADR'})
    Check (@(Get-PlatformIntentTargets @($t) $c).Count -eq 1)
    $t.environmentName='prod'; Reject { Get-PlatformIntentTargets @($t) $c }
}
Case 'existing network target can use central topology' { $t=Clone $target; $t.enabled=$true; $t.parameterOverrides.networkMode='existing'; Check (@(Get-PlatformIntentTargets @($t)).Count -eq 1) }
$id='/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/app/providers/Microsoft.Web/sites/app'
foreach ($path in @('identity.type','sku.name','location','properties.publicNetworkAccess','properties.virtualNetworkSubnetId','properties.networkAcls.defaultAction','properties.minimumTlsVersion','properties.httpsOnly')) {
    Case "what-if rejects sensitive Modify $path" { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Modify';delta=@(@{path=$path;propertyChangeType='Modify';before='old';after='new'})})} } }
}
foreach ($type in @('Microsoft.Network/privateEndpoints','Microsoft.Network/privateDnsZones','Microsoft.Network/routeTables','Microsoft.Network/azureFirewalls','Microsoft.ManagedIdentity/userAssignedIdentities','Microsoft.Authorization/roleAssignments')) {
    Case "what-if rejects topology/RBAC Modify $type" { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId="/subscriptions/x/resourceGroups/x/providers/$type/x";changeType='Modify';delta=@(@{path='properties';propertyChangeType='Modify'})})} } }
}
Case 'Modify without analyzed deltas fails closed' { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Modify'})} } }
Case 'nested public access change fails closed' { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Modify';delta=@(@{path='properties';propertyChangeType='Modify';children=@(@{path='publicNetworkAccess';propertyChangeType='Modify';after='Enabled'})})})} } }
Case 'object replacement cannot hide public access change' { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Modify';delta=@(@{path='properties';propertyChangeType='Modify';after=@{publicNetworkAccess='Enabled'}})})} } }
Case 'create with explicit public access is rejected' { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Create';after=@{properties=@{publicNetworkAccess='Enabled'}}})} } }
Case 'ordinary application setting update still reaches approval' {
    $changes=Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId=$id;changeType='Modify';delta=@(@{path='properties.siteConfig.appSettings';propertyChangeType='Array';children=@(@{path='0.value';propertyChangeType='Modify';after='releases/new.zip'})})})}
    Check ($changes.Count -eq 1)
}
Case 'shared monitoring ID validation accepts another subscription and rejects arbitrary endpoints' {
    $p=@{}; foreach($key in @('workload','environmentName','owner','costCenter','destinationSubscriptionId','destinationResourceGroupName','destinationStorageAccountName','destinationContainerName','destinationIsHnsEnabled','vnetAddressPrefix','integrationSubnetPrefix','privateEndpointSubnetPrefix')) { $p[$key]=@{value='test'} }
    $p.workload.value='blobcopy';$p.environmentName.value='dev';$p.destinationSubscriptionId.value='11111111-1111-1111-1111-111111111111';$p.destinationContainerName.value='incoming';$p.destinationIsHnsEnabled.value=$true
    $p.vnetAddressPrefix.value='10.40.0.0/16';$p.integrationSubnetPrefix.value='10.40.0.0/26';$p.privateEndpointSubnetPrefix.value='10.40.1.0/26'
    $p.existingLogAnalyticsWorkspaceId=@{value='/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/shared/providers/Microsoft.OperationalInsights/workspaces/central'}
    Assert-ServiceParameters $target $p
    $p.existingLogAnalyticsWorkspaceId.value='https://other.example';Reject { Assert-ServiceParameters $target $p }
}
Case 'hub resolver preflight accepts explicit forwarding and rejects wrong DNS or zone links' {
    $vnet="/subscriptions/$($target.subscriptionId)/resourceGroups/network/providers/Microsoft.Network/virtualNetworks/spoke"
    $hub='/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/hub/providers/Microsoft.Network/virtualNetworks/hub'
    $resolverId='/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/hub/providers/Microsoft.Network/dnsResolvers/central'
    $n=@{integrationSubnetId="$vnet/subnets/app";privateEndpointSubnetId="$vnet/subnets/endpoints";privateDnsZoneIds=@{};dnsResolver=@{id=$resolverId;inboundEndpointId="$resolverId/inboundEndpoints/inbound"}}
    foreach($key in @('blob','queue','table','dfs','web')) { $zone=if($key -eq 'web'){'privatelink.azurewebsites.net'}else{"privatelink.$key.core.windows.net"};$n.privateDnsZoneIds[$key]="/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/$zone" }
    $bundle=@{target=$target;parameters=@{parameters=@{networkMode=@{value='existing'};location=@{value='eastus2'};existingNetwork=@{value=$n}}}}
    $script:badForwarding=$false;$script:badLink=$false
    $original=${function:Invoke-ServiceJson}
    try {
        function Invoke-ServiceJson([string[]]$Arguments) {
            if ($Arguments[0] -eq 'rest') { return @{value=@(@{properties=@{virtualNetwork=@{id=$(if($script:badLink){$vnet}else{$hub})};provisioningState='Succeeded'}})} }
            $resource=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
            if($resource -eq $vnet){return @{location='eastus2';properties=@{dhcpOptions=@{dnsServers=@($(if($script:badForwarding){'10.99.0.1'}else{'10.0.0.4'}))}}}}
            if($resource -eq $n.integrationSubnetId){return @{properties=@{delegations=@(@{properties=@{serviceName='Microsoft.Web/serverFarms'}});addressPrefix='10.40.0.0/26'}}}
            if($resource -eq $n.privateEndpointSubnetId){return @{properties=@{delegations=@();privateEndpointNetworkPolicies='Disabled'}}}
            if($resource -eq $resolverId){return @{properties=@{virtualNetwork=@{id=$hub};provisioningState='Succeeded'}}}
            if($resource -eq $n.dnsResolver.inboundEndpointId){return @{properties=@{provisioningState='Succeeded';ipConfigurations=@(@{privateIpAddress='10.0.0.4'})}}}
            if($resource -in $n.privateDnsZoneIds.Values){return @{id=$resource}}
            throw 'Unexpected Azure call in resolver test.'
        }
        $state=Test-ServiceNetwork $bundle;Check ($state.dnsResolver.vnetId -eq $hub -and $state.zones.Count -eq 5)
        $script:badForwarding=$true;Reject { Test-ServiceNetwork $bundle };$script:badForwarding=$false
        $script:badLink=$true;Reject { Test-ServiceNetwork $bundle }
    } finally { Set-Item Function:Invoke-ServiceJson $original }
}
Case 'resolver reference cannot point to another resolver inbound endpoint' {
    $resolver='/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/hub/providers/Microsoft.Network/dnsResolvers/central'
    $network=@{integrationSubnetId="/subscriptions/$($target.subscriptionId)/resourceGroups/n/providers/Microsoft.Network/virtualNetworks/n/subnets/a";privateEndpointSubnetId="/subscriptions/$($target.subscriptionId)/resourceGroups/n/providers/Microsoft.Network/virtualNetworks/n/subnets/b";privateDnsZoneIds=@{};dnsResolver=@{id=$resolver;inboundEndpointId="$resolver/inboundEndpoints/inbound"}}
    foreach($key in @('blob','queue','table','dfs','web')) { $zone=if($key -eq 'web'){'privatelink.azurewebsites.net'}else{"privatelink.$key.core.windows.net"};$network.privateDnsZoneIds[$key]="/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/$zone" }
    Assert-ServiceNetworkIds $target $network
    $network.dnsResolver.inboundEndpointId=$resolver.Replace('/central','/different')+'/inboundEndpoints/inbound'
    $message=''; try { Assert-ServiceNetworkIds $target $network } catch { $message=$_.Exception.Message }
    Check ($message -eq 'Resolver mode requires explicit resolver and owned inbound endpoint IDs.')
}
$directory=Join-Path (Get-ProjectRoot) ('artifacts/platform-tests/'+[guid]::NewGuid().ToString('N'))
$report=@{passed=$results.Count;failed=0;azureCalls='none';cases=$results}
Write-ServiceJson $report (Join-Path $directory results.json)
# Included in the existing project-validation artifact in both pipeline entry points.
Write-ServiceJson $report (Join-Path (Get-ProjectRoot) artifacts/test-results/platform-contracts.json)
Write-Host "PASS: $($results.Count) platform contract/governance cases. Evidence: $directory"
