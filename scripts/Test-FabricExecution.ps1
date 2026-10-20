#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/fabric-workspace-common.ps1"
. "$PSScriptRoot/fabric-execution-common.ps1"
$script:ActualFabricExecutionRead=${function:Invoke-FabricExecutionRead}
. "$PSScriptRoot/../tests/fabric/execution-fixtures.ps1"
$script:ExecutionResults=[Collections.Generic.List[object]]::new()
$environmentKeys=@('BOUND_FABRIC_AGENT_POOL','SYSTEM_ACCESSTOKEN','AGENT_ID','AGENT_OS','SYSTEM_JOBID','SYSTEM_PLANID','SYSTEM_TEAMPROJECTID','SYSTEM_COLLECTIONURI','SYSTEM_TEAMPROJECT')
$savedEnvironment=@{};foreach($key in $environmentKeys){$savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process')}
function Assert-ExecutionTest([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Assert-ExecutionRejected([scriptblock]$Action,[string]$Pattern='.'){
    $message='';try{& $Action|Out-Null}catch{$message=$_.Exception.Message}
    Assert-ExecutionTest ($message -match $Pattern) "Expected rejection matching '$Pattern'; received '$message'."
}
function Test-ExecutionCase([string]$Name,[scriptblock]$Action){
    $script:manifest=New-FabricExecutionFixtureManifest
    Set-FabricExecutionFixtureEnvironment $manifest
    $script:e=$manifest.execution;$script:w=$e.privateWorkspace
    $script:fixture=New-FabricExecutionFixture $manifest
    $fixture.web=[Collections.Generic.Queue[object]]::new();$fixture.webCalls=[Collections.Generic.List[object]]::new()
    Set-FabricApiContext $manifest
    try{& $Action | Out-Null;$script:ExecutionResults.Add(@{name=$Name;passed=$true})}
    catch{$script:ExecutionResults.Add(@{name=$Name;passed=$false;error=$_.Exception.Message});Write-Host "FAIL: $Name - $($_.Exception.Message)"}
}
# All external boundaries are replaced. Any accidental unmocked network/CLI access fails.
function az {throw 'External Azure CLI access is forbidden in Fabric execution tests.'}
function Invoke-WebRequest {
    [CmdletBinding()]param($Uri,$Method,$Headers,$MaximumRedirection,$TimeoutSec,[switch]$SkipHttpErrorCheck)
    $fixture.webCalls.Add(@{uri=$Uri;method=$Method;maximumRedirection=$MaximumRedirection;timeoutSec=$TimeoutSec;skipHttpErrorCheck=[bool]$SkipHttpErrorCheck;authorizationPresent=[bool]$Headers.Authorization})
    if(!$fixture.web.Count){throw 'External HTTP access is forbidden in Fabric execution tests.'}
    $response=$fixture.web.Dequeue()
    if($response.Contains('transportFailure')){throw $response.transportFailure}
    return $response
}
function Invoke-FabricExecutionAz([string[]]$Arguments){
    $fixture.calls.Add(@{kind='az';arguments=$Arguments})
    if($Arguments[0] -ceq 'role'){return $fixture.grants}
    if($Arguments[0] -ceq 'resource'){
        $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
        if(!$fixture.arm.Contains($id)){throw "Unexpected synthetic ARM resource: $id"}
        return $fixture.arm[$id]
    }
    throw 'Unexpected synthetic Azure read.'
}
function Get-FabricExecutionArm([string]$Id,[string]$ApiVersion){
    $fixture.calls.Add(@{kind='arm';id=$Id;apiVersion=$ApiVersion})
    if(!$fixture.arm.Contains($Id)){throw "Unexpected synthetic ARM resource: $Id"}
    return $fixture.arm[$Id]
}
function Get-FabricExecutionArmCollection([string]$Id,[string]$ApiVersion){
    $fixture.calls.Add(@{kind='collection';id=$Id;apiVersion=$ApiVersion})
    if(!$fixture.collections.Contains($Id)){throw "Unexpected synthetic ARM collection: $Id"}
    return @($fixture.collections[$Id])
}
function Invoke-FabricExecutionRead([string]$Uri,[string]$Token,[string]$Service){
    $fixture.calls.Add(@{kind='http-read';uri=$Uri;service=$Service})
    $ado=$e.adoOrganizationUrl+'/_apis/distributedtask/pools/'+$e.adoPoolId
    if($Uri -ceq ($ado+'?api-version=7.1')){return $fixture.adoPool}
    if($Uri -ceq ($ado+'/agents/'+$env:AGENT_ID+'?includeAssignedRequest=true&api-version=7.0')){return $fixture.adoAgent}
    if($Uri -ceq ('https://api.fabric.microsoft.com/v1/workspaces/'+$w.workspaceId+'/networking/communicationPolicy')){return $fixture.policy}
    throw "Unexpected synthetic authenticated read: $Uri"
}
function Resolve-FabricExecutionAddresses([string]$HostName){
    $fixture.calls.Add(@{kind='dns';hostName=$HostName})
    if($HostName -cne ([uri]$w.apiBaseUrl).Host){throw 'Unexpected DNS scope.'}
    return $fixture.dns
}
function Test-FabricExecutionTcp([string]$Address){
    $fixture.calls.Add(@{kind='tcp';address=$Address})
    if($fixture.tcpFails){throw 'Synthetic TCP endpoint is unreachable.'}
}
function Invoke-FabricApi([string]$Method,[string]$Path,$Body=$null){
    $fixture.calls.Add(@{kind='fabric-read';method=$Method;path=$Path})
    if($Method -cne 'GET' -or $null -ne $Body -or $Path -cne ('workspaces/'+$w.workspaceId+'?preferWorkspaceSpecificEndpoints=true')){throw 'Unexpected synthetic Fabric operation; writes are forbidden.'}
    return $fixture.workspace
}
try{
    Test-ExecutionCase 'Managed private execution verifies pool, assigned job, endpoint and network without writes' {
        Assert-FabricManifest $manifest;Assert-FabricExecutionProfile $manifest
        $result=Test-FabricExecutionRequirements $manifest
        Assert-ExecutionTest ($result.kind -ceq 'ManagedDevOpsPool' -and $result.poolResourceId -ceq $e.poolResourceId -and $result.ado.poolId -eq $e.adoPoolId) 'Managed-pool identity was not retained.'
        Assert-ExecutionTest ($result.privateWorkspace.workspaceId -ceq $w.workspaceId -and $result.privateWorkspace.publicAccess -ceq 'Deny' -and $result.privateWorkspace.resolvedAddresses[0] -ceq '10.0.4.5') 'Private endpoint evidence missing.'
        Assert-ExecutionTest (@($fixture.calls|Where-Object kind -ceq 'tcp').Count -eq 1 -and @($fixture.calls|Where-Object kind -ceq 'fabric-read').Count -eq 1) 'Private DNS/TCP/API proof incomplete.'
        Assert-ExecutionTest ((ConvertTo-FabricCanonical $result) -notmatch 'synthetic-job-token|jobId|planId|scopeId') 'Ephemeral assignment or credentials entered durable binding.'
    }
    Test-ExecutionCase 'Stateless jobs may change while durable managed-pool proof stays identical' {
        $before=Test-FabricExecutionRequirements $manifest
        $env:AGENT_ID='8';$fixture.adoAgent.id=8
        $env:SYSTEM_JOBID='80000000-0000-0000-0000-000000000011';$fixture.adoAgent.assignedRequest.jobId=$env:SYSTEM_JOBID
        $after=Test-FabricExecutionRequirements $manifest
        Assert-ExecutionTest ((ConvertTo-FabricCanonical $before) -ceq (ConvertTo-FabricCanonical $after)) 'Stateless stage agent assignment changed durable resource proof.'
    }
    Test-ExecutionCase 'Bound pipeline cannot omit its managed-pool profile' {
        $manifest.Remove('execution');Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'source-reviewed managed-pool'
        Assert-ExecutionTest ($fixture.calls.Count -eq 0) 'Unbound pipeline performed external reads.'
    }
    Test-ExecutionCase 'Legacy unbound global helper remains explicit' {
        $manifest.Remove('execution');$env:BOUND_FABRIC_AGENT_POOL=$null
        $proof=Test-FabricExecutionRequirements $manifest
        Assert-ExecutionTest ($proof.kind -ceq 'UnboundGlobalApi' -and !$proof.privateWorkspace -and $fixture.calls.Count -eq 0) 'Global helper implicitly acquired private proof.'
    }
    foreach($field in @('kind','poolResourceId','adoOrganizationUrl','adoProjectName','adoPoolName','adoPoolId','location','agentSubnetId','devCenterProjectResourceId','maximumConcurrency','agentVmSize','agentImageVersion','poolAdministratorGroupEmails','devOpsInfrastructurePrincipalObjectId')){
        Test-ExecutionCase "Required managed-pool field $field cannot be omitted" {
            $e.Remove($field);Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'Missing Fabric managed-pool field'
            Assert-ExecutionTest ($fixture.calls.Count -eq 0) 'Invalid source caused an external read.'
        }
    }
    foreach($pair in @(@('poolResourceId','/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/platform/providers/Microsoft.DevOpsInfrastructure/pools/foreign'),@('adoPoolId',0),@('adoPoolId','123'),@('agentImageVersion','latest'),@('adoOrganizationUrl','https://dev.azure.com/contoso?token=x'),@('adoPoolName','REPLACE_REVIEWED_POOL'),@('maximumConcurrency',11),@('devOpsInfrastructurePrincipalObjectId','00000000-0000-0000-0000-000000000000'))){
        Test-ExecutionCase "Managed source rejects invalid $($pair[0]) value $($pair[1])" {$e[$pair[0]]=$pair[1];Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest}}
    }
    foreach($field in @('kind','poolResourceId','adoOrganizationUrl','adoProjectName','adoPoolName','location','agentSubnetId','devCenterProjectResourceId','agentVmSize','agentImageVersion','devOpsInfrastructurePrincipalObjectId')){
        Test-ExecutionCase "Managed identifier $field rejects a singleton array" {$e[$field]=@($e[$field]);Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'must be scalar strings'}
        Test-ExecutionCase "Managed identifier $field rejects a numeric scalar" {$e[$field]=123;Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'must be scalar strings'}
    }
    Test-ExecutionCase 'Unknown managed-pool fields are never ignored' {$e.extra='ignored';Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'Unsupported'}
    Test-ExecutionCase 'Pipeline alias and source profile must match' {$env:BOUND_FABRIC_AGENT_POOL='another-pool';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'Pipeline pool selection'}
    foreach($pair in @(@('apiBaseUrl','https://api.fabric.microsoft.com'),@('virtualNetworkResourceId','/subscriptions/foreign/resourceGroups/shared/providers/Microsoft.Network/virtualNetworks/foreign'),@('privateDnsZoneResourceId','/subscriptions/foreign/resourceGroups/shared/providers/Microsoft.Network/privateDnsZones/privatelink.fabric.microsoft.com'),@('workspaceId','00000000-0000-0000-0000-000000000000'))){
        Test-ExecutionCase "Private source rejects invalid $($pair[0])" {$w[$pair[0]]=$pair[1];Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest}}
    }
    foreach($field in @('workspaceId','apiBaseUrl','privateEndpointResourceId','virtualNetworkResourceId','privateDnsZoneResourceId')){
        Test-ExecutionCase "Private identifier $field rejects a singleton array" {$w[$field]=@($w[$field]);Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'must be scalar strings'}
        Test-ExecutionCase "Private identifier $field rejects an object" {$w[$field]=@{value=$w[$field]};Assert-ExecutionRejected {Assert-FabricExecutionProfile $manifest} 'must be scalar strings'}
    }
    Test-ExecutionCase 'Managed pool alias drift is rejected from live ARM proof' {$fixture.arm[$e.poolResourceId].properties.organizationProfile.organizations[0].alias='foreign-pool';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'Azure DevOps project/alias/admin'}
    Test-ExecutionCase 'Managed pool image drift is rejected' {$fixture.arm[$e.poolResourceId].properties.fabricProfile.images[0].wellKnownImageName='windows-2022-g2/latest';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'image'}
    Test-ExecutionCase 'Managed pool cannot expose all projects' {$fixture.arm[$e.poolResourceId].properties.organizationProfile.organizations[0].openAccess=$true;Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'Azure DevOps project/alias/admin'}
    Test-ExecutionCase 'Managed pool cannot acquire an ambient Azure identity' {$fixture.arm[$e.poolResourceId].identity.type='SystemAssigned';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'identity'}
    Test-ExecutionCase 'Agent subnet delegation drift is rejected' {$fixture.arm[$e.agentSubnetId].properties.delegations[0].properties.serviceName='Microsoft.Web/serverFarms';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'exclusively delegated'}
    Test-ExecutionCase 'Agent subnet cannot contain a private endpoint' {$fixture.arm[$e.agentSubnetId].properties.privateEndpoints=@(@{id=$w.privateEndpointResourceId});Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'shared'}
    Test-ExecutionCase 'Foreign pool cannot claim occupied agent subnet' {$fixture.arm[$e.agentSubnetId].properties.serviceAssociationLinks[0].properties.link+='-foreign';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'associated with a different resource'}
    Test-ExecutionCase 'Agent VNet cannot overlap internal Managed DevOps Pool addresses' {$fixture.arm[$w.virtualNetworkResourceId].properties.addressSpace.addressPrefixes=@('172.16.0.0/12');Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} '172.17'}
    Test-ExecutionCase 'DevCenter project region drift is rejected' {$fixture.arm[$e.devCenterProjectResourceId].location='westus';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'DevCenter project'}
    foreach($fault in @('missing','conditional','principal','user','child-scope')){
        Test-ExecutionCase "Managed VNet grants reject $fault evidence" {
            switch($fault){'missing'{$fixture.grants=@($fixture.grants[0])};'conditional'{$fixture.grants[1].condition='attribute condition'};'principal'{$fixture.grants[1].principalId='99999999-9999-9999-9999-999999999999'};'user'{$fixture.grants[1].principalType='User'};'child-scope'{$fixture.grants[1].scope=$e.agentSubnetId}}
            Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'unconditional Reader and Network Contributor'
        }
    }
    foreach($pair in @(@('SYSTEM_ACCESSTOKEN',''),@('AGENT_ID','0'),@('SYSTEM_JOBID','not-a-guid'),@('SYSTEM_COLLECTIONURI','https://dev.azure.com/foreign/'),@('SYSTEM_TEAMPROJECT','Foreign'),@('AGENT_OS','Linux'))){
        Test-ExecutionCase "ADO runtime rejects invalid $($pair[0])" {[Environment]::SetEnvironmentVariable($pair[0],$pair[1],'Process');Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest}}
    }
    Test-ExecutionCase 'ADO pool ID and alias proof cannot be substituted' {$fixture.adoPool.name='foreign';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'pool ID/name/type'}
    foreach($field in @('jobId','planId','scopeId','poolId')){
        Test-ExecutionCase "Current ADO assigned request must match $field" {$fixture.adoAgent.assignedRequest[$field]=$(if($field -eq 'poolId'){999}else{'99999999-9999-9999-9999-999999999999'});Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'not assigned'}
    }
    Test-ExecutionCase 'Offline ADO agent cannot prove this running job' {$fixture.adoAgent.status='offline';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'not assigned'}
    Test-ExecutionCase 'Custom DNS requires separate network qualification' {$fixture.arm[$w.virtualNetworkResourceId].properties.dhcpOptions.dnsServers=@('10.0.0.4');Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'direct Azure DNS'}
    Test-ExecutionCase 'Unapproved Fabric endpoint is rejected' {$fixture.arm[$w.privateEndpointResourceId].properties.privateLinkServiceConnections[0].properties.privateLinkServiceConnectionState.status='Pending';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'not approved'}
    Test-ExecutionCase 'Fabric endpoint cannot share the delegated agent subnet' {$fixture.arm[$w.privateEndpointResourceId].properties.subnet.id=$e.agentSubnetId;Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'separate subnet'}
    Test-ExecutionCase 'Fabric endpoint cannot connect to another workspace tenant' {$service=$fixture.arm[$w.privateEndpointResourceId].properties.privateLinkServiceConnections[0].properties.privateLinkServiceId;$fixture.arm[$service].properties.tenantId='99999999-9999-9999-9999-999999999999';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'another Fabric workspace or tenant'}
    Test-ExecutionCase 'Fabric endpoint subnet cannot be delegated' {$subnet=$fixture.arm[$w.privateEndpointResourceId].properties.subnet.id;$fixture.arm[$subnet].properties.delegations=@(@{properties=@{serviceName='Microsoft.Web/serverFarms'}});Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'nondelegated'}
    Test-ExecutionCase 'Fabric endpoint subnet must disable private endpoint network policies' {$subnet=$fixture.arm[$w.privateEndpointResourceId].properties.subnet.id;$fixture.arm[$subnet].properties.privateEndpointNetworkPolicies='Enabled';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'policies disabled'}
    Test-ExecutionCase 'Fabric endpoint zone group cannot point to a foreign zone' {$fixture.collections[$w.privateEndpointResourceId+'/privateDnsZoneGroups'][0].properties.privateDnsZoneConfigs[0].properties.privateDnsZoneId+='-foreign';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'DNS zone group'}
    Test-ExecutionCase 'Fabric DNS link requires completed provisioning' {$fixture.collections[$w.privateDnsZoneResourceId+'/virtualNetworkLinks'][0].properties.virtualNetworkLinkState='InProgress';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'completed, nonregistering direct link'}
    Test-ExecutionCase 'Fabric DNS must disable VM registration' {$fixture.collections[$w.privateDnsZoneResourceId+'/virtualNetworkLinks'][0].properties.registrationEnabled=$true;Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'completed, nonregistering direct link'}
    Test-ExecutionCase 'Fabric NIC cannot belong to another endpoint' {$nic=$fixture.arm[$w.privateEndpointResourceId].properties.networkInterfaces[0].id;$fixture.arm[$nic].properties.privateEndpoint.id+='-foreign';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'NIC identity'}
    foreach($address in @('20.1.2.3','172.17.0.4','::1','10.0.4.99')){
        Test-ExecutionCase "Fabric DNS rejects unbound address $address" {$fixture.dns=@('10.0.4.5',$address);Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'resolve exclusively'}
    }
    Test-ExecutionCase 'Empty Fabric DNS answer is rejected' {$fixture.dns=@();Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'resolve exclusively'}
    Test-ExecutionCase 'Fabric TCP reachability is required' {$fixture.tcpFails=$true;Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'unreachable'}
    Test-ExecutionCase 'Fabric workspace must already deny public access' {$fixture.policy.inbound.publicAccessRules.defaultAction='Allow';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'already deny public access'}
    Test-ExecutionCase 'Workspace response cannot replace the private API endpoint' {$fixture.workspace.apiEndpoint='https://api.fabric.microsoft.com';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'private API endpoint'}
    Test-ExecutionCase 'Workspace response must prove assigned capacity' {$fixture.workspace.capacityId='00000000-0000-0000-0000-000000000000';Assert-ExecutionRejected {Test-FabricExecutionRequirements $manifest} 'GUID'}
    Test-ExecutionCase 'Private IP classifier allows only supported RFC1918 IPv4 addresses' {
        foreach($ip in @('10.0.0.1','172.16.0.1','172.18.0.1','172.31.255.254','192.168.1.1')){Assert-ExecutionTest (Test-FabricExecutionPrivateIp $ip) "Rejected private IPv4 $ip"}
        foreach($ip in @('8.8.8.8','127.0.0.1','169.254.169.254','172.17.0.1','172.32.0.1','192.169.1.1','::1','fc00::1','invalid')){Assert-ExecutionTest (!(Test-FabricExecutionPrivateIp $ip)) "Accepted unsupported address $ip"}
    }
    Test-ExecutionCase 'Authenticated read accepts only a successful object response without redirects or retries' {
        $fixture.web.Enqueue(@{StatusCode=200;Content='{"id":123,"name":"reviewed"}'})
        $data=& $script:ActualFabricExecutionRead 'https://synthetic.invalid/reviewed' 'synthetic-token' 'Synthetic protocol'
        Assert-ExecutionTest ($data -is [Collections.IDictionary] -and $data.id -eq 123 -and $data.name -ceq 'reviewed') 'Successful protocol object was not retained.'
        $call=$fixture.webCalls[0]
        Assert-ExecutionTest ($fixture.webCalls.Count -eq 1 -and $call.method -ceq 'GET' -and $call.maximumRedirection -eq 0 -and $call.timeoutSec -eq 60 -and $call.skipHttpErrorCheck -and $call.authorizationPresent) 'Authenticated read changed protocol controls.'
    }
    Test-ExecutionCase 'Authenticated read redacts malformed JSON content' {
        $canary='SYNTHETIC_SENSITIVE_JSON_CONTENT'
        $fixture.web.Enqueue(@{StatusCode=200;Content=('{"secret":"'+$canary+'", malformed')})
        $message='';try{& $script:ActualFabricExecutionRead 'https://synthetic.invalid/reviewed' 'synthetic-token' 'Synthetic protocol'|Out-Null}catch{$message=$_.Exception.Message}
        Assert-ExecutionTest ($message -match 'invalid JSON; response content is withheld' -and $message -notmatch $canary -and $fixture.webCalls.Count -eq 1) 'Malformed response leaked content or was retried.'
    }
    foreach($json in @('null','true','123','"synthetic string"','[]','[{"id":1}]','[{"id":1},{"id":2}]')){
        Test-ExecutionCase "Authenticated read rejects nonobject JSON $json" {
            $fixture.web.Enqueue(@{StatusCode=200;Content=$json})
            Assert-ExecutionRejected {& $script:ActualFabricExecutionRead 'https://synthetic.invalid/reviewed' 'synthetic-token' 'Synthetic protocol'} 'invalid object'
            Assert-ExecutionTest ($fixture.webCalls.Count -eq 1) 'Invalid response was retried.'
        }
    }
    foreach($status in @(403,302)){
        Test-ExecutionCase "Authenticated read rejects HTTP $status and withholds response content" {
            $canary='SYNTHETIC_SENSITIVE_HTTP_BODY'
            $fixture.web.Enqueue(@{StatusCode=$status;Content=$canary;Headers=@{Location='https://untrusted.invalid/redirect'}})
            $message='';try{& $script:ActualFabricExecutionRead 'https://synthetic.invalid/reviewed' 'synthetic-token' 'Synthetic protocol'|Out-Null}catch{$message=$_.Exception.Message}
            Assert-ExecutionTest ($message -match "returned HTTP $status" -and $message -notmatch $canary -and $fixture.webCalls.Count -eq 1) 'Failure body leaked, redirect was followed or read was retried.'
        }
    }
    Test-ExecutionCase 'Authenticated read redacts transport errors containing sensitive data' {
        $canary='SYNTHETIC_SENSITIVE_TRANSPORT_BODY'
        $fixture.web.Enqueue(@{transportFailure="Bearer synthetic-token; upstream response $canary"})
        $message='';try{& $script:ActualFabricExecutionRead 'https://synthetic.invalid/reviewed' 'synthetic-token' 'Synthetic protocol'|Out-Null}catch{$message=$_.Exception.Message}
        Assert-ExecutionTest ($message -match 'response bodies and credentials are withheld' -and $message -notmatch "$canary|Bearer|synthetic-token" -and $fixture.webCalls.Count -eq 1) 'Sensitive transport error leaked or was retried.'
    }
}finally{foreach($key in $environmentKeys){[Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process')}}
$directory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/fabric-execution-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force|Out-Null
$failed=@($script:ExecutionResults|Where-Object {!$_.passed}).Count
$receipt=@{passed=$script:ExecutionResults.Count-$failed;failed=$failed;scope='Synthetic code-only Fabric/Managed DevOps Pool/ADO/private-network prerequisite tests. No Azure, Azure DevOps or Fabric network calls.';scenarios=$script:ExecutionResults.ToArray()}
$receipt|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $directory receipt.json)
if($failed){throw "$failed Fabric execution scenarios failed. Receipt: $directory/receipt.json"}
Write-Host "PASS: $($receipt.passed) Fabric execution scenarios. Synthetic evidence only. Receipt: $directory/receipt.json"
