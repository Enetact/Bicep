#requires -Version 7.4
. "$PSScriptRoot/workload-preview-common.ps1"
. "$PSScriptRoot/../tests/managed-devops-pool/fixtures.ps1"
$root=Get-ProjectRoot;$count=0
function Check([bool]$Ok){if(!$Ok){throw "Managed pool assertion failed at check $script:count"};$script:count++}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
$t=Get-Content (Join-Path $root self-service/targets/devopspool.dev.json) -Raw|ConvertFrom-Json -AsHashtable
$p=(Get-Content (Join-Path $root artifacts/products/managed-devops-pool/dev.parameters.json) -Raw|ConvertFrom-Json -AsHashtable).parameters
$scope="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers";$vnetId="$scope/Microsoft.Network/virtualNetworks/platform"
$p.owner.value='platform-test';$p.costCenter.value='test';$p.existingLogAnalyticsWorkspaceId.value="$scope/Microsoft.OperationalInsights/workspaces/logs";$p.deploymentPrincipalObjectId.value='11111111-1111-1111-1111-111111111111'
Set-MdpTestParameters $p $scope $vnetId
Assert-ProductParameters $t $p;Check (!$t.enabled -and $t.agentPool -ceq 'platform-bootstrap')
foreach($pair in @(@('adoOrganizationUrl','https://dev.azure.com/contoso?token=x'),@('adoOrganizationUrl','https://contoso.example'),@('adoPoolName','private_agents'),@('adoPoolName','private.agents'),@('adoPoolName','private-agents-'),@('adoProjectNames',@()),@('adoProjectNames',@('Platform','platform')),@('poolAdministratorGroupEmails',@()),@('poolAdministratorGroupEmails',@('33333333-3333-3333-3333-333333333333')),@('maximumConcurrency',0),@('maximumConcurrency',11),@('maximumConcurrency','2'),@('agentVmSize','Standard_D4s_v6'),@('agentImageVersion','latest'),@('devOpsInfrastructurePrincipalObjectId','00000000-0000-0000-0000-000000000000'))){$bad=Clone $p;$bad[$pair[0]].value=$pair[1];Reject {Assert-ProductParameters $t $bad}}
$bad=Clone $t;$bad.agentPool=$p.adoPoolName.value;Reject {Assert-ProductParameters $bad $p}
$bad=Clone $t;$bad.agentPool='mdp-devopspool-dev-abc123';Reject {Assert-ProductParameters $bad $p}
$bad=Clone $p;$bad.agentSubnetId.value=$bad.agentSubnetId.value.Replace($t.subscriptionId,'99999999-9999-9999-9999-999999999999');Reject {Assert-ProductParameters $t $bad}
$subnet=New-MdpTestSubnet $p;$vnet=New-MdpTestVnet $p
Assert-MdpSubnet $p $subnet $vnet;Check $true
$bad=Clone $subnet;$bad.properties.delegations=@(@{properties=@{serviceName='Microsoft.Web/serverFarms'}});Reject {Assert-MdpSubnet $p $bad $vnet}
$bad=Clone $subnet;$bad.properties.addressPrefix='10.0.8.0/30';Reject {Assert-MdpSubnet $p $bad $vnet}
$bad=Clone $vnet;$bad.properties.addressSpace.addressPrefixes=@('172.16.0.0/12');Reject {Assert-MdpSubnet $p $subnet $bad}
$bad=Clone $vnet;$bad.location='westus';Reject {Assert-MdpSubnet $p $subnet $bad}
$bad=Clone $subnet;$bad.properties.ipConfigurations=@(@{id='foreign-nic'});Reject {Assert-MdpSubnet $p $bad $vnet}
$bad=Clone $subnet;$bad.properties.privateEndpoints=@(@{id='foreign-endpoint'});Reject {Assert-MdpSubnet $p $bad $vnet}
$poolId="/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)/providers/Microsoft.DevOpsInfrastructure/pools/mdp-devopspool-dev-abc123"
$associated=Clone $subnet;$associated.properties.ipConfigurations=@(@{id='running-agent'});$associated.properties.serviceAssociationLinks=@(@{properties=@{link=$poolId;linkedResourceType='Microsoft.DevOpsInfrastructure/pools'}})
Assert-MdpSubnet $p $associated $vnet $poolId;Check $true
$bad=Clone $associated;$bad.properties.serviceAssociationLinks[0].properties.link=$poolId+'-foreign';Reject {Assert-MdpSubnet $p $bad $vnet $poolId}
$inventory=@{resources=@();networks=@()};Set-MdpTestInventory $inventory $p $vnetId
Assert-MdpDiscovery $t $p $inventory;Check $true
$bad=Clone $inventory;$bad.resources=@();Reject {Assert-MdpDiscovery $t $p $bad}
$bad=Clone $inventory;$bad.networks[0].subnetQuery.status='Failed';Reject {Assert-MdpDiscovery $t $p $bad}
$originalJson=${function:Invoke-ServiceJson};$originalStack=${function:Get-WorkloadStack}
try {
    $scenario='healthy';$bundle=@{target=$t;parameters=@{parameters=$p}}
    function Get-WorkloadStack {return $null}
    function Invoke-ServiceJson([string[]]$Arguments){
        if($scenario -eq 'denied'){throw 'Denied fixture'}
        if($Arguments[0] -eq 'role'){
            return @('acdd72a7-3385-48ef-bd42-f606fba81ae7','4d97b98b-1d4f-4787-a291-c67834d212e7'|Where-Object {$scenario -ne 'missing-grant' -or $_ -ne '4d97b98b-1d4f-4787-a291-c67834d212e7'}|ForEach-Object {@{id="grant/$_";principalId=$(if($scenario -eq 'principal'){'foreign'}else{$p.devOpsInfrastructurePrincipalObjectId.value});principalType='ServicePrincipal';roleDefinitionId="/subscriptions/$($t.subscriptionId)/providers/Microsoft.Authorization/roleDefinitions/$_";scope=$vnetId;condition=$(if($scenario -eq 'conditional'){'attribute condition'}else{''})}})
        }
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
        if($id -ieq $vnetId){return $vnet}
        if($id -ieq $p.agentSubnetId.value){return $subnet}
        if($id -ieq $p.devCenterProjectResourceId.value){return @{id=$id;location=$(if($scenario -eq 'project-region'){'westus'}else{$p.location.value});properties=@{provisioningState='Succeeded';devCenterId="$scope/Microsoft.DevCenter/devcenters/platform"}}}
        throw 'Unexpected fixture request.'
    }
    Check ((Test-MdpPrerequisites $bundle).agentSubnetId -ceq $p.agentSubnetId.value)
    foreach($scenario in @('denied','missing-grant','principal','conditional','project-region')){Reject {Test-MdpPrerequisites $bundle}}
    $pool=New-MdpTestPool $p $poolId
    $outputs=@{managedDevOpsPoolId=@{value=$poolId};managedDevOpsPoolName=@{value=$pool.name};agentPoolName=@{value=$p.adoPoolName.value};workspaceId=@{value=$p.existingLogAnalyticsWorkspaceId.value}}
    function Invoke-ServiceJson {return $pool}
    Check (!(Test-MdpInfrastructure $bundle $outputs).agentJobVerified)
    $healthy=Clone $pool
    foreach($fault in @('public-projects','wrong-image','stateful','automatic-standby','ambient-identity','wrong-subnet','wrong-admin','wrong-alias')){
        $pool=Clone $healthy
        switch($fault){
            'public-projects'{$pool.properties.organizationProfile.organizations[0].openAccess=$true}
            'wrong-image'{$pool.properties.fabricProfile.images[0].wellKnownImageName='windows-2022-g2/latest'}
            'stateful'{$pool.properties.agentProfile.kind='Stateful'}
            'automatic-standby'{$pool.properties.agentProfile.resourcePredictionsProfile=@{kind='Automatic'}}
            'ambient-identity'{$pool.identity.type='SystemAssigned'}
            'wrong-subnet'{$pool.properties.fabricProfile.networkProfile.subnetId+='-foreign'}
            'wrong-admin'{$pool.properties.organizationProfile.permissionProfile.groups=@('foreign@contoso.com')}
            'wrong-alias'{$pool.properties.organizationProfile.organizations[0].alias='foreign'}
        }
        Reject {Test-MdpInfrastructure $bundle $outputs}
    }
} finally {Set-Item Function:Invoke-ServiceJson $originalJson;Set-Item Function:Get-WorkloadStack $originalStack}
Reject {Assert-ServiceChange @{resourceId=$poolId;changeType='Modify';delta=@(@{path='properties.organizationProfile';propertyChangeType='Modify'})}}
Assert-ServiceChange @{resourceId=$poolId;changeType='Modify';delta=@(@{path='tags.releaseActivated';propertyChangeType='Modify';before='false';after='true'})};Check $true
Check ((Get-RecoveryPolicy managed-devops-pool).policy.mode -ceq 'ManualRecovery')
Check ((Get-ProductCostEstimate $t $p).status -ceq 'Unavailable')
Write-Host "PASS: $count Managed DevOps Pool checks. Synthetic configuration/ARM evidence only; no Azure/ADO resources or jobs created."
