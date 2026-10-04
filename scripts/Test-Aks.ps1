#requires -Version 7.4
. "$PSScriptRoot/workload-preview-common.ps1"
. "$PSScriptRoot/../tests/aks/fixtures.ps1"
$root=Get-ProjectRoot;$t=Get-Content (Join-Path $root self-service/targets/aksapps.dev.json) -Raw|ConvertFrom-Json -AsHashtable
$p=(Get-Content (Join-Path $root artifacts/products/private-aks/dev.parameters.json) -Raw|ConvertFrom-Json -AsHashtable).parameters
$scope="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers";$fixtureAksVnet="$scope/Microsoft.Network/virtualNetworks/test"
$p.owner.value='test';$p.costCenter.value='test';$p.existingLogAnalyticsWorkspaceId.value="$scope/Microsoft.OperationalInsights/workspaces/logs";$p.deploymentPrincipalObjectId.value='11111111-1111-1111-1111-111111111111'
Set-AksTestParameters $p $scope $fixtureAksVnet
$count=0
function Check([bool]$Value){if(!$Value){throw 'AKS assertion failed.'};$script:count++}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
Assert-ProductParameters $t $p;Check (!$t.enabled)
foreach($pair in @(@('podCidr','10.201.0.0/16'),@('serviceCidr','10.201.0.1/24'),@('dnsServiceIP','10.201.0.1'),@('kubernetesVersion','latest'),@('applicationNamespace','kube-system'),@('maxUserNodes',100),@('controlPlaneIdentityId',$p.kubeletIdentityId.value),@('nodeSubnetId','/wrong/subnet'))){$bad=Clone $p;$bad[$pair[0]].value=$pair[1];Reject {Assert-ProductParameters $t $bad}}
foreach($cidr in @('010.1.0.0/24','256.0.0.0/24','10.1.0.1/24','10.0.0.0/7','::/64')){Reject {Get-AksRange $cidr}}
$inv=@{resources=@();networks=@();privateDnsZones=@()};Set-AksTestInventory $inv $p $fixtureAksVnet;Assert-AksDiscovery $t $p $inv
$bad=Clone $inv;$bad.resources=@();Reject {Assert-AksDiscovery $t $p $bad}
$bad=Clone $inv;$bad.networks[0].subnetQuery.status='Failed';Reject {Assert-AksDiscovery $t $p $bad}
$originalJson=${function:Invoke-ServiceJson};$originalLinks=${function:Get-StackRestCollection};$originalDns=${function:Wait-ProductPrivateHost}
try{
    $fault='none'
    function Invoke-ServiceJson([string[]]$Arguments){
        if($fault -eq 'denied'){throw 'Denied fixture'}
        if($Arguments[0] -eq 'role'){
            if($fault -eq 'role'){return @()}
            $principal=$Arguments[[Array]::IndexOf($Arguments,'--assignee-object-id')+1]
            return @('4d97b98b-1d4f-4787-a291-c67834d212e7','b12aa53e-6015-4669-85d0-8515ebb3ae7f','f1a07417-d97a-45cb-824c-7a7467783830','7f951dda-4ed3-4680-a7ca-43fe172d538d'|ForEach-Object {@{principalId=$principal;roleDefinitionId="/roles/$_";condition=$null}})
        }
        if($Arguments[0] -eq 'aks'){return @{values=@(@{isPreview=$false;patchVersions=$(if($fault -eq 'version'){@{}}else{@{'1.35.3'=@{}}})})}}
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1];$props=@{provisioningState='Succeeded'};$result=@{id=$id;location='eastus2';properties=$props}
        if($id -eq $fixtureAksVnet){$props.addressSpace=@{addressPrefixes=@($(if($fault -eq 'overlap'){'10.200.0.0/16'}else{'10.10.0.0/16'}))};$props.dhcpOptions=@{dnsServers=$(if($fault -eq 'custom-dns'){@('10.10.0.4')}else{@()})}}
        elseif($id -match '/subnets/'){$props.addressPrefix=$(if($fault -eq 'small-subnet'){'10.10.0.0/28'}else{'10.10.0.0/24'});$props.delegations=@();$props.natGateway=@{id="$scope/Microsoft.Network/natGateways/test"};if($fault -eq 'udr'){$props.routeTable=@{id='unqualified'}}}
        elseif($id -match '/natGateways/'){$props.publicIpAddresses=@(@{id='fixture'})}
        elseif($id -match '/userAssignedIdentities/'){$props.clientId=$p.kubeletClientId.value;$props.principalId=$(if($fault -eq 'kubelet'){'wrong'}else{$p.kubeletObjectId.value})}
        elseif($id -match '/registries/'){$result.sku=@{name='Premium'};$props.adminUserEnabled=($fault -eq 'acr-admin');$props.publicNetworkAccess='Disabled';$props.loginServer='fixtureacr.azurecr.io';$props.privateEndpointConnections=@(@{properties=@{privateLinkServiceConnectionState=@{status='Approved'}}})}
        return $result
    }
    function Get-StackRestCollection {if($fault -eq 'dns-link'){return @()};return @(@{properties=@{virtualNetwork=@{id=$fixtureAksVnet};provisioningState='Succeeded'}})}
    function Wait-ProductPrivateHost {if($fault -eq 'public-dns'){throw 'Public address fixture'}}
    $bundle=@{target=$t;parameters=@{parameters=$p}}
    Check ((Test-AksPrerequisites $bundle).Count -gt 5)
    foreach($fault in @('denied','role','version','overlap','custom-dns','small-subnet','udr','kubelet','acr-admin','dns-link','public-dns')){Reject {Test-AksPrerequisites $bundle}}
}finally{Set-Item Function:Invoke-ServiceJson $originalJson;Set-Item Function:Get-StackRestCollection $originalLinks;Set-Item Function:Wait-ProductPrivateHost $originalDns}
Reject {Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/test/providers/Microsoft.ContainerService/managedClusters/test';changeType='Modify'}}
foreach($flag in @('enablePrivateCluster','disableLocalAccounts','enableRBAC','enableAzureRBAC')){Reject {Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/test/providers/Microsoft.ContainerService/managedClusters/test';changeType='Create';after=@{properties=@{$flag=$false}}}}}
Assert-ServiceChange @{resourceId='/subscriptions/test/resourceGroups/test/providers/Microsoft.ContainerService/managedClusters/test';changeType='Modify';delta=@(@{path='tags.releaseActivated';propertyChangeType='Modify';before='false';after='true'})}
$arm=Get-Content (Join-Path $root artifacts/products/private-aks/main.json) -Raw|ConvertFrom-Json -AsHashtable
$compiled=$arm|ConvertTo-Json -Depth 100 -Compress
Check ($compiled.Contains('"enablePrivateClusterPublicFQDN":false') -and $compiled.Contains('"osSKU":"AzureLinux3"') -and $compiled.Contains('"outboundType":"userAssignedNATGateway"'))
Write-Host "PASS: $count AKS contract checks (mocked Azure). No deployment."
