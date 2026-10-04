#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/workload-preview-common.ps1"
$root=Get-ProjectRoot
$t=Get-Content (Join-Path $root self-service/targets/dataplat.dev.json) -Raw|ConvertFrom-Json -AsHashtable
$fixtureScope="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers"
$fixtureVnet="$fixtureScope/Microsoft.Network/virtualNetworks/data"
$p=@{}
foreach($pair in @(@('workload','dataplat'),@('environmentName','dev'),@('location','eastus2'),@('owner','tests'),@('costCenter','tests'),@('deploymentPrincipalObjectId','11111111-1111-1111-1111-111111111111'),@('existingLogAnalyticsWorkspaceId',"$fixtureScope/Microsoft.OperationalInsights/workspaces/logs"),@('privateEndpointSubnetId',"$fixtureVnet/subnets/pe"),@('databricksHostSubnetId',"$fixtureVnet/subnets/host"),@('databricksContainerSubnetId',"$fixtureVnet/subnets/container"),@('browserAuthenticationPrivateEndpointId',"$fixtureScope/Microsoft.Network/privateEndpoints/auth"),@('sparkVersion','15.4.x-scala2.12'),@('nodeType','Standard_D4ds_v5'),@('notebookPath','/Shared/platform-studio/acceptance'))){$p[$pair[0]]=@{value=$pair[1]}}
$p.inputFileName=@{value='sample.csv'};$p.databricksJobId=@{value=0};$p.maxWorkers=@{value=2};$p.privateDnsZoneIds=@{value=@{}}
foreach($key in (Get-WorkloadDefinition 'private-data-platform').dnsKeys){$p.privateDnsZoneIds.value[$key]="$fixtureScope/Microsoft.Network/privateDnsZones/$((Get-ProductDnsNames)[$key])"}
$bundle=@{target=$t;parameters=@{parameters=$p}}
$cases=[Collections.Generic.List[object]]::new()
function Case([string]$Name,[scriptblock]$Body){try{& $Body;$cases.Add(@{name=$Name;passed=$true})}catch{throw "$Name : $($_.Exception.Message) $($_.ScriptStackTrace)"}}
function Check([bool]$Ok){if(!$Ok){throw 'Assertion failed.'}}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Clone($Value){return $Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
Case 'source target remains disabled' {Check (!$t.enabled);Reject {Assert-ServiceTarget $t dataplat dev}}
Case 'reviewed parameters accepted' {Assert-ProductParameters $t $p}
Case 'unsafe input filename is rejected' {$bad=Clone $p;$bad.inputFileName.value='../other.csv';Reject {Assert-ProductParameters $t $bad}}
Case 'unreviewed job binding is rejected' {$bad=Clone $p;$bad.databricksJobId.value='123';Reject {Assert-ProductParameters $t $bad}}
foreach($fault in @('same-subnet','different-vnet','foreign-auth','bad-runtime','bad-size','unbounded-workers','arbitrary-notebook')){
    Case "parameter rejection: $fault" {
        $bad=Clone $p
        switch($fault){
            same-subnet {$bad.databricksHostSubnetId.value=$p.privateEndpointSubnetId.value}
            different-vnet {$bad.databricksHostSubnetId.value=$p.databricksHostSubnetId.value.Replace('/data/','/other/')}
            foreign-auth {$bad.browserAuthenticationPrivateEndpointId.value=$p.browserAuthenticationPrivateEndpointId.value.Replace($t.subscriptionId,'99999999-9999-9999-9999-999999999999')}
            bad-runtime {$bad.sparkVersion.value='latest'}
            bad-size {$bad.nodeType.value='arbitrary'}
            unbounded-workers {$bad.maxWorkers.value=100}
            arbitrary-notebook {$bad.notebookPath.value='/Users/unreviewed'}
        }
        Reject {Assert-ProductParameters $t $bad}
    }
}
$inv=@{networks=@(@{location='eastus2';subnetQuery=@{status='Succeeded'};subnets=@($p.databricksHostSubnetId.value,$p.databricksContainerSubnetId.value|ForEach-Object {@{id=$_;delegations=@('Microsoft.Databricks/workspaces');addressPrefixes=@('10.1.0.0/26')}})});resources=@(@{id=$p.browserAuthenticationPrivateEndpointId.value;type='Microsoft.Network/privateEndpoints'})}
Case 'saved dedicated subnet and shared auth evidence accepted' {Assert-DataPlatformDiscovery $t $p $inv}
Case 'failed subnet discovery remains unknown' {$bad=Clone $inv;$bad.networks[0].subnetQuery.status='Failed';Reject {Assert-DataPlatformDiscovery $t $p $bad}}
Case 'missing regional browser auth blocks' {$bad=Clone $inv;$bad.resources=@();Reject {Assert-DataPlatformDiscovery $t $p $bad}}
Case 'wrong compute delegation blocks' {$bad=Clone $inv;$bad.networks[0].subnets[0].delegations=@('Microsoft.Web/serverFarms');Reject {Assert-DataPlatformDiscovery $t $p $bad}}
Case 'small compute subnet blocks' {$bad=Clone $inv;$bad.networks[0].subnets[0].addressPrefixes=@('10.1.0.0/28');Reject {Assert-DataPlatformDiscovery $t $p $bad}}
$originalJson=${function:Invoke-ServiceJson};$originalCollection=${function:Get-StackRestCollection};$originalDns=${function:Wait-ProductPrivateHost}
try {
    $scenario='healthy'
    function Invoke-ServiceJson([string[]]$Arguments){
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
        if($scenario -eq 'denied'){throw 'AuthorizationFailed (fixture)'}
        $result=@{id=$id;location='eastus2';properties=@{provisioningState='Succeeded'}}
        if($scenario -eq 'identity'){$result.id='wrong'}
        if($id -match '/subnets/(host|container)$'){
            $result.properties+=@{delegations=@(@{properties=@{serviceName=$(if($scenario -eq 'delegation'){'Microsoft.Web/serverFarms'}else{'Microsoft.Databricks/workspaces'})}});addressPrefix=$(if($scenario -eq 'size'){'10.1.0.0/28'}else{'10.1.0.0/26'});networkSecurityGroup=@{id="$fixtureScope/Microsoft.Network/networkSecurityGroups/db"};natGateway=@{id="$fixtureScope/Microsoft.Network/natGateways/egress"}}
            if($scenario -eq 'no-nsg'){$result.properties.Remove('networkSecurityGroup')}
            if($scenario -eq 'no-egress'){$result.properties.Remove('natGateway')}
            if($scenario -eq 'udr'){$result.properties.routeTable=@{id='unqualified'}}
        }elseif($id -match '/subnets/pe$'){$result.properties.addressPrefix=$(if($scenario -eq 'pe-size'){'10.1.1.0/29'}else{'10.1.1.0/26'})}
        elseif($id -match '/natGateways/'){$result.properties.publicIpAddresses=@(@{id='fixture-public-ip'});if($scenario -eq 'nat-no-address'){$result.properties.publicIpAddresses=@()}}
        elseif($id -match '/privateEndpoints/'){
            $result.properties.subnet=@{id="$fixtureVnet/subnets/pe"}
            $result.properties.privateLinkServiceConnections=@(@{properties=@{privateLinkServiceId="$fixtureScope/Microsoft.Databricks/workspaces/browser";groupIds=@('browser_authentication');privateLinkServiceConnectionState=@{status=$(if($scenario -eq 'pending-auth'){'Pending'}else{'Approved'})}}})
            if($scenario -eq 'region'){$result.location='westus'}
            if($scenario -eq 'wrong-auth-group'){$result.properties.privateLinkServiceConnections[0].properties.groupIds=@('databricks_ui_api')}
        }elseif($id -match '/Microsoft.Databricks/workspaces/'){$result.sku=@{name='premium'}}
        return $result
    }
    function Get-StackRestCollection([string]$Url){return @(@{properties=@{privateDnsZoneConfigs=@(@{properties=@{privateDnsZoneId=$(if($scenario -eq 'dns'){'wrong'}else{$p.privateDnsZoneIds.value.databricks})}})}})}
    Case 'live prerequisite reader accepts configured NAT and regional auth (mock)' {$result=Test-DataPlatformPrerequisites $bundle;Check ($result.Count -gt 0)}
    foreach($scenario in @('denied','identity','delegation','size','pe-size','no-nsg','no-egress','udr','nat-no-address','pending-auth','region','wrong-auth-group','dns')){Case "live prerequisite rejects $scenario (mock)" {Reject {Test-DataPlatformPrerequisites $bundle}}}
    # Post-deploy endpoint approval verification uses only GET. No approval mutation is implemented.
    $owned="/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)/providers"
    $o=@{databricksResourceId=@{value="$owned/Microsoft.Databricks/workspaces/db"};factoryResourceId=@{value="$owned/Microsoft.DataFactory/factories/adf"};storageResourceId=@{value="$owned/Microsoft.Storage/storageAccounts/lake"};databricksUrl=@{value='adb-123.4.azuredatabricks.net'}}
    function Wait-ProductPrivateHost([string]$HostName){if($scenario -eq 'public-dns'){throw 'Public DNS (fixture)'}}
    function Invoke-ServiceJson([string[]]$Arguments){
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
        $r=@{id=$id;properties=@{provisioningState='Succeeded'}}
        if($id -eq $o.databricksResourceId.value){$r.sku=@{name='premium'};$r.properties+=@{publicNetworkAccess=$(if($scenario -eq 'public'){'Enabled'}else{'Disabled'});requiredNsgRules='NoAzureDatabricksRules';defaultStorageFirewall='Enabled';managedResourceGroupId="/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)-databricks-managed";parameters=@{storageAccountName=@{value='dbfstest'};enableNoPublicIp=@{value=$true}};workspaceUrl=$o.databricksUrl.value}}
        elseif($id -eq $o.factoryResourceId.value){$r.identity=@{type='SystemAssigned'};$r.properties.publicNetworkAccess='Disabled'}
        elseif($id.EndsWith('/dbfstest')){$r.properties.networkAcls=@{defaultAction=$(if($scenario -eq 'root-public'){'Allow'}else{'Deny'})}}
        elseif($id -eq $o.storageResourceId.value){$r.properties.isHnsEnabled=$true}
        else {
            $key=($id -split '/')[-1];$r.properties.privateLinkResourceId=$(if($key -eq 'databricks'){$o.databricksResourceId.value}else{$o.storageResourceId.value})
            $r.properties.groupId=$(if($key -eq 'databricks'){'databricks_ui_api'}elseif($key -eq 'lake-dfs'){'dfs'}else{'blob'})
            $r.properties.connectionState=@{status=$(if($scenario -eq 'pending-mpe'){'Pending'}else{'Approved'})}
            if($scenario -eq 'wrong-mpe-target'){$r.properties.privateLinkResourceId='wrong'}
        };return $r
    }
    function Get-StackRestCollection([string]$Url){
        if($scenario -eq 'missing-pe'){return @()}
        return @(@("/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)-databricks-managed/providers/Microsoft.Storage/storageAccounts/dbfstest",'blob'),@("/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)-databricks-managed/providers/Microsoft.Storage/storageAccounts/dbfstest",'dfs'),@($o.databricksResourceId.value,'databricks_ui_api'),@($o.storageResourceId.value,'blob'),@($o.storageResourceId.value,'dfs'),@($o.factoryResourceId.value,'dataFactory'),@($o.factoryResourceId.value,'portal')|ForEach-Object {@{properties=@{subnet=@{id=$p.privateEndpointSubnetId.value};privateLinkServiceConnections=@(@{properties=@{privateLinkServiceId=$_[0];groupIds=@($_[1]);privateLinkServiceConnectionState=@{status='Approved'}}})}}})
    }
    $scenario='healthy'
    Case 'infrastructure acceptance never claims notebook execution (mock)' {$result=Test-DataPlatformInfrastructure $bundle $o;Check (!$result.dataPlaneVerified -and !$result.notebookExecuted -and !$result.unityCatalogVerified)}
    foreach($scenario in @('public','root-public','public-dns','pending-mpe','wrong-mpe-target','missing-pe')){Case "post-deploy rejects $scenario (mock)" {Reject {Test-DataPlatformInfrastructure $bundle $o}}}
}finally{Set-Item Function:Invoke-ServiceJson $originalJson;Set-Item Function:Get-StackRestCollection $originalCollection;Set-Item Function:Wait-ProductPrivateHost $originalDns}
foreach($property in @('defaultStorageFirewall','accessConnector','enableNoPublicIp','managedResourceGroupId')){
    Case "Preview blocks security migration: $property" {Reject {Assert-ServiceChange @{resourceId="$owned/Microsoft.Databricks/workspaces/db";changeType='Modify';delta=@(@{path="properties.$property";propertyChangeType='Modify';after='changed'})}}}
}
Case 'Preview blocks embedded Databricks PAT' {Reject {Assert-ServiceChange @{resourceId="$owned/Microsoft.DataFactory/factories/adf/linkedservices/db";changeType='Create';after=@{properties=@{typeProperties=@{accessToken='fixture-not-a-secret'}}}}}}
Case 'Preview blocks arbitrary ADF execution change' {Reject {Assert-ServiceChange @{resourceId="$owned/Microsoft.DataFactory/factories/adf/pipelines/run-reviewed-notebook";changeType='Modify';delta=@(@{path='properties.activities';propertyChangeType='Modify';after=@()})}}}
Case 'Preview blocks source dataset redirection' {Reject {Assert-ServiceChange @{resourceId="$owned/Microsoft.DataFactory/factories/adf/datasets/reviewed-landing-file";changeType='Modify';delta=@(@{path='properties.typeProperties.location';propertyChangeType='Modify';after=@{fileSystem='other'}})}}}
$dir=Join-Path $root artifacts/data-platform;New-Item -ItemType Directory -Path $dir -Force|Out-Null
Write-ServiceJson @{passed=$cases.Count;failed=0;azureCalls=$false;adoCalls=$false;cases=$cases} (Join-Path $dir contracts.json)
Write-Host "PASS: $($cases.Count) data platform contracts (mocked Azure)."
