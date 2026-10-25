#requires -Version 7.4
. "$PSScriptRoot/workload-preview-common.ps1"
$root=Get-ProjectRoot
$t=Get-Content (Join-Path $root self-service/targets/fabric.dev.json) -Raw|ConvertFrom-Json -AsHashtable
$p=@{workload=@{value='fabric'};environmentName=@{value='dev'};location=@{value='eastus2'};owner=@{value='platform-test'};costCenter=@{value='test'};existingLogAnalyticsWorkspaceId=@{value="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers/Microsoft.OperationalInsights/workspaces/logs"};deploymentPrincipalObjectId=@{value='11111111-1111-1111-1111-111111111111'};capacitySku=@{value='F64'};capacityAdministrators=@{value=@('fabric-admin@contoso.com')};enableWorkspacePrivateLink=@{value=$false};existingFabricWorkspaceId=@{value=''};privateEndpointSubnetId=@{value=''};privateDnsZoneIds=@{value=@{}}}
$count=0
function Check([bool]$Value){if(!$Value){throw 'Fabric assertion failed.'};$script:count++}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
Assert-ProductParameters $t $p;Check (!$t.enabled)
foreach($pair in @(@('capacitySku','F2'),@('capacityAdministrators',@()),@('capacityAdministrators',@('same@contoso.com','SAME@contoso.com')),@('capacityAdministrators',@('11111111-1111-1111-1111-111111111111')),@('enableWorkspacePrivateLink','true'),@('existingFabricWorkspaceId','22222222-2222-2222-2222-222222222222'))){$bad=Clone $p;$bad[$pair[0]].value=$pair[1];Reject {Assert-ProductParameters $t $bad}}
$scope="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers";$fixtureFabricVnet="$scope/Microsoft.Network/virtualNetworks/test"
$p.enableWorkspacePrivateLink.value=$true;$p.existingFabricWorkspaceId.value='22222222-2222-2222-2222-222222222222';$p.privateEndpointSubnetId.value="$fixtureFabricVnet/subnets/endpoints";$p.privateDnsZoneIds.value=@{fabric="$scope/Microsoft.Network/privateDnsZones/privatelink.fabric.microsoft.com"}
Assert-ProductParameters $t $p;Check $true
foreach($key in @('existingFabricWorkspaceId','privateEndpointSubnetId')){$bad=Clone $p;$bad[$key].value='';Reject {Assert-ProductParameters $t $bad}}
$bad=Clone $p;$bad.privateDnsZoneIds.value.fabric="$scope/Microsoft.Network/privateDnsZones/privatelink.powerbi.com";Reject {Assert-ProductParameters $t $bad}
$bad=Clone $p;$bad.privateEndpointSubnetId.value=$bad.privateEndpointSubnetId.value.Replace($t.subscriptionId,'99999999-9999-9999-9999-999999999999');Reject {Assert-ProductParameters $t $bad}
$inventory=@{networks=@(@{id=$fixtureFabricVnet;location='eastus2';subnetQuery=@{status='Succeeded'};subnets=@(@{id=$p.privateEndpointSubnetId.value;privateEndpointCandidate=$true})});privateDnsZones=@(@{id=$p.privateDnsZoneIds.value.fabric})}
Assert-FabricDiscovery $t $p $inventory;Check $true
$bad=Clone $inventory;$bad.networks[0].subnetQuery.status='Failed';Reject {Assert-FabricDiscovery $t $p $bad}
$bad=Clone $inventory;$bad.privateDnsZones=@();Reject {Assert-FabricDiscovery $t $p $bad}
$originalJson=${function:Invoke-ServiceJson};$originalLinks=${function:Get-StackRestCollection}
try{
    $fault='none'
    function Invoke-ServiceJson([string[]]$Arguments){
        if($fault -eq 'denied'){throw 'Denied fixture'}
        if($Arguments[0] -eq 'rest'){return @{id=$(if($fault -eq 'workspace'){'wrong'}else{$p.existingFabricWorkspaceId.value})}}
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
        if($id -eq $fixtureFabricVnet){return @{id=$id;location=$(if($fault -eq 'region'){'westus'}else{'eastus2'});properties=@{dhcpOptions=@{dnsServers=$(if($fault -eq 'dns'){@('10.0.0.4')}else{@()})}}}}
        return @{id=$id;properties=@{delegations=$(if($fault -eq 'delegation'){@(@{name='other'})}else{@()});privateEndpointNetworkPolicies='Disabled'}}
    }
    function Get-StackRestCollection {if($fault -eq 'link'){return @()};return @(@{properties=@{virtualNetwork=@{id=$fixtureFabricVnet};provisioningState='Succeeded'}})}
    $bundle=@{target=$t;parameters=@{parameters=$p}}
    Check ((Test-FabricPrerequisites $bundle).workspaceId -ceq $p.existingFabricWorkspaceId.value)
    foreach($fault in @('denied','workspace','region','dns','delegation','link')){Reject {Test-FabricPrerequisites $bundle}}
}finally{Set-Item Function:Invoke-ServiceJson $originalJson;Set-Item Function:Get-StackRestCollection $originalLinks}
Check ((Get-ProductCostEstimate $t $p).status -ceq 'Unavailable')
Check ((Get-RecoveryPolicy fabric).policy.mode -ceq 'ManualRecovery')
$originalStack=${function:Get-WorkloadStack}
try {
    $owned="/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)/providers"
    $fixtureFabricOutputs=@{fabricCapacityResourceId=@{value="$owned/Microsoft.Fabric/capacities/testcapacity"};workspacePrivateLinkServiceId=@{value="$owned/Microsoft.Fabric/privateLinkServicesForFabric/testprivate"};workspacePrivateEndpointId=@{value="$owned/Microsoft.Network/privateEndpoints/testendpoint"};workspaceId=@{value=$p.existingLogAnalyticsWorkspaceId.value}}
    function Get-WorkloadStack {return @{properties=@{outputs=$fixtureFabricOutputs}}}
    $bundle=@{target=$t;parameters=@{parameters=$p}}
    Check ((Get-ProductOutputs $bundle).workspacePrivateEndpointId.value -ceq $fixtureFabricOutputs.workspacePrivateEndpointId.value)
    $fixtureFabricOutputs.workspacePrivateEndpointId.value="$scope/Microsoft.Network/privateEndpoints/unowned"
    Reject {Get-ProductOutputs $bundle}
} finally {Set-Item Function:Get-WorkloadStack $originalStack}
Reject {Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/owned/providers/Microsoft.Fabric/capacities/test';changeType='Modify';delta=@(@{path='properties.administration.members';propertyChangeType='Modify'})}}
Reject {Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/owned/providers/Microsoft.Fabric/privateLinkServicesForFabric/test';changeType='Modify';delta=@(@{path='properties.workspaceId';propertyChangeType='Modify'})}}
Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/owned/providers/Microsoft.Fabric/capacities/test';changeType='Modify';delta=@(@{path='tags.releaseActivated';propertyChangeType='Modify';before='false';after='true'})};Check $true
Write-Host "PASS: $count Fabric infrastructure contract checks (synthetic Azure/Fabric reads). No deployment."
