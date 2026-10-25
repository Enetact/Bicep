# Fabric consumers reuse the reviewed managed-pool contract; this code never creates a pool or grants access.
. "$PSScriptRoot/managed-devops-pool-common.ps1"

function Get-FabricManagedPoolParameters($Manifest) {
    $e=$Manifest.execution
    $p=@{workload=@{value='devopspool'};environmentName=@{value=$Manifest.environmentName};location=@{value=$e.location};resourceNames=@{value=@{pool=($e.poolResourceId -split '/')[-1]}}}
    foreach($key in @('devCenterProjectResourceId','agentSubnetId','adoOrganizationUrl','adoPoolName','poolAdministratorGroupEmails','maximumConcurrency','agentVmSize','agentImageVersion','devOpsInfrastructurePrincipalObjectId')){$p[$key]=@{value=$e[$key]}}
    $p.adoProjectNames=@{value=@($e.adoProjectName)}
    return $p
}
function Assert-FabricExecutionProfile($Manifest) {
    if(!$Manifest.Contains('execution')){
        if($env:BOUND_FABRIC_AGENT_POOL){throw 'The Fabric pipeline requires a source-reviewed managed-pool execution profile.'}
        return
    }
    $e=$Manifest.execution
    $keys=@('kind','poolResourceId','adoOrganizationUrl','adoProjectName','adoPoolName','adoPoolId','location','agentSubnetId','devCenterProjectResourceId','maximumConcurrency','agentVmSize','agentImageVersion','poolAdministratorGroupEmails','devOpsInfrastructurePrincipalObjectId')
    Assert-FabricKeys $e ($keys+@('privateWorkspace'))
    foreach($key in $keys){if(!$e.Contains($key)){throw "Missing Fabric managed-pool field: $key"}}
    foreach($key in $keys|Where-Object {$_ -cnotin @('maximumConcurrency','adoPoolId','poolAdministratorGroupEmails')}){if($e[$key] -isnot [string]){throw 'Managed-pool identifiers and names must be scalar strings.'}}
    $scope='^/subscriptions/'+[regex]::Escape($Manifest.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    if($e.kind -cne 'ManagedDevOpsPool' -or $e.poolResourceId -cnotmatch ($scope+'Microsoft.DevOpsInfrastructure/pools/[A-Za-z0-9][A-Za-z0-9.-]{1,42}[A-Za-z0-9]$')){throw 'Fabric requires a reviewed same-subscription Microsoft Managed DevOps Pool resource.'}
    if(($e.adoPoolId -isnot [int] -and $e.adoPoolId -isnot [long]) -or $e.adoPoolId -lt 1 -or $e.adoPoolId -gt [int]::MaxValue){throw 'Supply the observed positive Azure DevOps pool ID.'}
    if($e.location -isnot [string] -or $e.location -cnotmatch '^[a-z][a-z0-9]{2,39}$' -or (ConvertTo-FabricCanonical $e) -match 'REPLACE_'){throw 'Resolve reviewed managed-pool source inputs before Fabric execution.'}
    $p=Get-FabricManagedPoolParameters $Manifest
    Assert-MdpParameters @{subscriptionId=$Manifest.subscriptionId;agentPool=''} $p
    if($env:BOUND_FABRIC_AGENT_POOL -and $env:BOUND_FABRIC_AGENT_POOL -cne $e.adoPoolName){throw 'Pipeline pool selection differs from the reviewed Fabric execution profile.'}
    if($e.Contains('privateWorkspace')){
        $w=$e.privateWorkspace
        $fields=@('workspaceId','apiBaseUrl','privateEndpointResourceId','virtualNetworkResourceId','privateDnsZoneResourceId')
        Assert-FabricKeys $w $fields
        foreach($key in $fields){if(!$w.Contains($key)){throw "Missing Fabric private-workspace field: $key"}}
        foreach($key in $fields){if($w[$key] -isnot [string]){throw 'Private workspace identifiers must be scalar strings.'}}
        Assert-FabricGuid $w.workspaceId
        $n=([guid]$w.workspaceId).ToString('N')
        if($w.apiBaseUrl -cne "https://$n.z$($n.Substring(0,2)).w.api.fabric.microsoft.com"){throw 'Private workspace API endpoint must match its reviewed workspace UUID.'}
        if($w.virtualNetworkResourceId -ine ($e.agentSubnetId -replace '/subnets/[^/]+$','') -or $w.privateEndpointResourceId -cnotmatch ($scope+'Microsoft.Network/privateEndpoints/[^/]+$') -or $w.privateDnsZoneResourceId -cnotmatch ($scope+'Microsoft.Network/privateDnsZones/privatelink\.fabric\.microsoft\.com$')){throw 'Fabric private workspace requires the managed-agent VNet and a reviewed endpoint and Fabric DNS zone in its subscription.'}
        if($Manifest.operation.workspaceId -ine $w.workspaceId -or $Manifest.operation.kind -cnotin @('CreateLakehouse','ProvisionIdentity','AddWorkspaceAdminGroup','CreateManagedPrivateEndpoint')){throw 'This private profile only supports explicit workspace-context Fabric APIs; global connections and native deployment pipelines are unsupported.'}
    }
}
function Invoke-FabricExecutionAz([string[]]$Arguments) {
    $raw=& az @Arguments --output json --only-show-errors 2>&1
    if($LASTEXITCODE -ne 0){throw 'Managed-pool Azure prerequisite read failed. Check the reviewed WIF identity and resource read permissions.'}
    return ($raw -join "`n" | ConvertFrom-Json -AsHashtable)
}
function Get-FabricExecutionArm([string]$Id,[string]$ApiVersion) {
    Invoke-FabricExecutionAz @('resource','show','--ids',$Id,'--api-version',$ApiVersion)
}
function Get-FabricExecutionArmCollection([string]$Id,[string]$ApiVersion) {
    $r=Invoke-FabricExecutionAz @('rest','--method','get','--url',"https://management.azure.com${Id}?api-version=$ApiVersion")
    if(!$r.Contains('value') -or $r.value -isnot [array] -or $r.nextLink){throw 'Fabric private-network prerequisite collection is partial or malformed.'}
    return @($r.value)
}
function Invoke-FabricExecutionRead([string]$Uri,[string]$Token,[string]$Service) {
    try{$r=Invoke-WebRequest -Uri $Uri -Method GET -Headers @{Authorization="Bearer $Token"} -MaximumRedirection 0 -TimeoutSec 60 -SkipHttpErrorCheck}catch{throw "$Service prerequisite read failed; response bodies and credentials are withheld."}
    if([int]$r.StatusCode -ne 200){throw "$Service prerequisite read returned HTTP $([int]$r.StatusCode). Check read authorization and network policy."}
    try{$data=$r.Content | ConvertFrom-Json -AsHashtable -NoEnumerate}catch{throw "$Service prerequisite read returned invalid JSON; response content is withheld."}
    if($data -isnot [Collections.IDictionary]){throw "$Service prerequisite read returned an invalid object."}
    return $data
}
function Test-FabricExecutionAgent($E) {
    if(!$env:SYSTEM_ACCESSTOKEN -or $env:AGENT_ID -cnotmatch '^[1-9][0-9]*$' -or $env:BOUND_FABRIC_AGENT_POOL -cne $E.adoPoolName){throw 'Fabric managed-pool execution requires the protected pipeline job token, agent ID and source-bound pool alias.'}
    foreach($key in @('SYSTEM_JOBID','SYSTEM_PLANID','SYSTEM_TEAMPROJECTID')){Assert-FabricGuid ([Environment]::GetEnvironmentVariable($key))}
    if($env:SYSTEM_COLLECTIONURI.TrimEnd('/') -ine $E.adoOrganizationUrl -or $env:SYSTEM_TEAMPROJECT -cne $E.adoProjectName -or $env:AGENT_OS -cne 'Windows_NT'){throw 'Fabric managed-pool organization, project or Windows runner binding changed.'}
    $base=$E.adoOrganizationUrl+'/_apis/distributedtask/pools/'+$E.adoPoolId
    $pool=Invoke-FabricExecutionRead ($base+'?api-version=7.1') $env:SYSTEM_ACCESSTOKEN 'Azure DevOps pool'
    if($pool.id -ne $E.adoPoolId -or $pool.name -cne $E.adoPoolName -or $pool.poolType -cne 'automation'){throw 'Azure DevOps pool ID/name/type differs from the reviewed managed pool.'}
    $agent=Invoke-FabricExecutionRead ($base+'/agents/'+$env:AGENT_ID+'?includeAssignedRequest=true&api-version=7.0') $env:SYSTEM_ACCESSTOKEN 'Azure DevOps agent'
    $job=$agent.assignedRequest
    if($agent.id -ne [long]$env:AGENT_ID -or $agent.enabled -ne $true -or $agent.status -cne 'online' -or !$job -or $job.jobId -ine $env:SYSTEM_JOBID -or $job.planId -ine $env:SYSTEM_PLANID -or $job.scopeId -ine $env:SYSTEM_TEAMPROJECTID -or $job.poolId -ne $E.adoPoolId){throw 'Current Azure DevOps job is not assigned to the reviewed managed pool agent.'}
    # Job/agent identifiers deliberately do not enter the durable Preview binding: stateless stages use different agents.
    return @{poolId=$E.adoPoolId;poolName=$E.adoPoolName;organizationUrl=$E.adoOrganizationUrl;projectId=$env:SYSTEM_TEAMPROJECTID;projectName=$E.adoProjectName}
}
function Test-FabricExecutionPrivateIp([string]$Address) {
    $ip=$null
    if(![Net.IPAddress]::TryParse($Address,[ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){return $false}
    $b=$ip.GetAddressBytes()
    return $b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31 -and $b[1] -ne 17) -or ($b[0] -eq 192 -and $b[1] -eq 168)
}
function Resolve-FabricExecutionAddresses([string]$HostName) {
    try{return @([Net.Dns]::GetHostAddresses($HostName) | ForEach-Object {$_.ToString()} | Sort-Object -Unique)}catch{throw 'Private workspace API DNS resolution failed.'}
}
function Test-FabricExecutionTcp([string]$Address) {
    $client=[Net.Sockets.TcpClient]::new()
    try{$client.ConnectAsync($Address,443).WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | Out-Null}catch{throw 'Private workspace API endpoint TCP 443 is unreachable from this agent.'}finally{$client.Dispose()}
}
function Test-FabricExecutionPrivateWorkspace($Manifest,$Vnet) {
    $e=$Manifest.execution;$w=$e.privateWorkspace
    if($Vnet.properties.dhcpOptions -and @($Vnet.properties.dhcpOptions.dnsServers).Count){throw 'This Fabric managed-pool profile requires direct Azure DNS; custom DNS/forwarding needs separate qualification.'}
    $pe=Get-FabricExecutionArm $w.privateEndpointResourceId '2024-05-01'
    $connections=@($pe.properties.privateLinkServiceConnections)
    if($pe.id -ine $w.privateEndpointResourceId -or $pe.location -ine $e.location -or $pe.properties.provisioningState -cne 'Succeeded' -or $connections.Count -ne 1 -or @($pe.properties.manualPrivateLinkServiceConnections).Count){throw 'Fabric workspace endpoint identity, region, provisioning or connection is unverified.'}
    $c=$connections[0].properties
    $scope='^/subscriptions/'+[regex]::Escape($Manifest.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    if($c.privateLinkServiceId -cnotmatch ($scope+'Microsoft.Fabric/privateLinkServicesForFabric/[^/]+$') -or $c.privateLinkServiceConnectionState.status -cne 'Approved' -or @($c.groupIds).Count -ne 1 -or $c.groupIds[0] -cne 'workspace'){throw 'Fabric workspace private endpoint is not approved for exactly the workspace group.'}
    $service=Get-FabricExecutionArm $c.privateLinkServiceId '2024-06-01'
    if($service.id -ine $c.privateLinkServiceId -or $service.properties.workspaceId -ine $w.workspaceId -or $service.properties.tenantId -ine $Manifest.tenantId){throw 'Private Link service belongs to another Fabric workspace or tenant.'}
    $subnetId=[string]$pe.properties.subnet.id
    if(($subnetId -replace '/subnets/[^/]+$','') -ine $w.virtualNetworkResourceId -or $subnetId -ieq $e.agentSubnetId){throw 'Fabric endpoint must use a separate subnet in the managed-agent VNet.'}
    $subnet=Get-FabricExecutionArm $subnetId '2024-05-01'
    if($subnet.id -ine $subnetId -or @($subnet.properties.delegations).Count -or $subnet.properties.privateEndpointNetworkPolicies -cne 'Disabled'){throw 'Fabric endpoint subnet must be nondelegated with private endpoint policies disabled.'}
    $zone=Get-FabricExecutionArm $w.privateDnsZoneResourceId '2024-06-01'
    if($zone.id -ine $w.privateDnsZoneResourceId -or $zone.name -cne 'privatelink.fabric.microsoft.com'){throw 'Fabric private DNS zone identity changed.'}
    $groups=@(Get-FabricExecutionArmCollection ($w.privateEndpointResourceId+'/privateDnsZoneGroups') '2024-05-01')
    if($groups.Count -ne 1 -or $groups[0].properties.provisioningState -cne 'Succeeded' -or @($groups[0].properties.privateDnsZoneConfigs).Count -ne 1 -or $groups[0].properties.privateDnsZoneConfigs[0].properties.privateDnsZoneId -ine $w.privateDnsZoneResourceId){throw 'Fabric private endpoint DNS zone group differs from the reviewed binding.'}
    $links=@(Get-FabricExecutionArmCollection ($w.privateDnsZoneResourceId+'/virtualNetworkLinks') '2024-06-01')
    if(@($links|Where-Object {$_.properties.virtualNetwork.id -ieq $w.virtualNetworkResourceId -and $_.properties.provisioningState -ceq 'Succeeded' -and $_.properties.virtualNetworkLinkState -ceq 'Completed' -and $_.properties.registrationEnabled -eq $false}).Count -ne 1){throw 'Fabric private DNS zone needs one completed, nonregistering direct link to the managed-agent VNet.'}
    $nics=@($pe.properties.networkInterfaces)
    if(!$nics.Count -or $nics.Count -gt 10){throw 'Fabric private endpoint NIC evidence is incomplete.'}
    $addresses=@(@(foreach($nic in $nics){
        if($nic.id -cnotmatch ($scope+'Microsoft.Network/networkInterfaces/[^/]+$')){throw 'Untrusted Fabric private endpoint NIC resource.'}
        $n=Get-FabricExecutionArm $nic.id '2024-05-01'
        if($n.id -ine $nic.id -or $n.properties.privateEndpoint.id -ine $w.privateEndpointResourceId){throw 'Fabric endpoint NIC identity changed.'}
        foreach($config in $n.properties.ipConfigurations){if(!(Test-FabricExecutionPrivateIp $config.properties.privateIPAddress)){throw 'Fabric endpoint NIC has a nonprivate or unsupported IP address.'};$config.properties.privateIPAddress}
    }) | Sort-Object -Unique)
    $resolved=@(Resolve-FabricExecutionAddresses ([uri]$w.apiBaseUrl).Host)
    if(!$resolved.Count -or @($resolved|Where-Object {!(Test-FabricExecutionPrivateIp $_) -or $_ -cnotin $addresses}).Count){throw 'Fabric workspace API must resolve exclusively to its reviewed private endpoint NIC addresses.'}
    foreach($address in $resolved){Test-FabricExecutionTcp $address}
    # Fabric explicitly requires the global FQDN for communication-policy reads, even with workspace Private Link.
    $policy=Invoke-FabricExecutionRead ('https://api.fabric.microsoft.com/v1/workspaces/'+$w.workspaceId+'/networking/communicationPolicy') $script:FabricToken 'Fabric communication policy (global endpoint)'
    if($policy.inbound.publicAccessRules.defaultAction -cne 'Deny'){throw 'The reviewed Fabric private workspace must already deny public access; this workflow never changes its firewall policy.'}
    $workspace=Invoke-FabricApi GET ('workspaces/'+$w.workspaceId+'?preferWorkspaceSpecificEndpoints=true')
    Assert-FabricGuid $workspace.capacityId
    if($workspace.id -ine $w.workspaceId -or ([string]$workspace.apiEndpoint).TrimEnd('/') -cne $w.apiBaseUrl){throw 'Fabric workspace private API endpoint or assigned capacity is unverified.'}
    return @{workspaceId=$w.workspaceId;apiBaseUrl=$w.apiBaseUrl;capacityId=$workspace.capacityId;privateEndpointId=$pe.id;privateLinkServiceId=$service.id;endpointSubnetId=$subnetId;privateDnsZoneId=$zone.id;resolvedAddresses=@($resolved|Sort-Object);publicAccess='Deny'}
}
function Test-FabricExecutionRequirements($Manifest) {
    Assert-FabricExecutionProfile $Manifest
    if(!$Manifest.Contains('execution')){return @{kind='UnboundGlobalApi';privateWorkspace=$false}}
    $e=$Manifest.execution;$p=Get-FabricManagedPoolParameters $Manifest
    # Adapt existing deterministic MDP read-back checks without importing other workload runtimes.
    function Get-ValueHash($Value){Get-FabricHash (ConvertTo-FabricCanonical $Value)}
    function Invoke-ServiceJson([string[]]$Arguments){Invoke-FabricExecutionAz $Arguments}
    $poolId=$e.poolResourceId;$poolName=($poolId -split '/')[-1]
    $outputs=@{managedDevOpsPoolId=@{value=$poolId};managedDevOpsPoolName=@{value=$poolName};agentPoolName=@{value=$e.adoPoolName}}
    $null=Test-MdpInfrastructure @{parameters=@{parameters=$p}} $outputs
    $vnetId=$e.agentSubnetId -replace '/subnets/[^/]+$',''
    $vnet=Get-FabricExecutionArm $vnetId '2024-05-01'
    $subnet=Get-FabricExecutionArm $e.agentSubnetId '2024-05-01'
    Assert-MdpSubnet $p $subnet $vnet $poolId
    $project=Get-FabricExecutionArm $e.devCenterProjectResourceId '2025-02-01'
    if($project.id -ine $e.devCenterProjectResourceId -or $project.location -ine $e.location -or $project.properties.provisioningState -cne 'Succeeded' -or !$project.properties.devCenterId){throw 'Managed pool DevCenter project identity, region or provisioning changed.'}
    $grants=@(Invoke-FabricExecutionAz @('role','assignment','list','--assignee-object-id',$e.devOpsInfrastructurePrincipalObjectId,'--scope',$vnetId,'--include-inherited','--all'))
    foreach($role in @('acdd72a7-3385-48ef-bd42-f606fba81ae7','4d97b98b-1d4f-4787-a291-c67834d212e7')){
        if(!@($grants|Where-Object {$_.principalId -ieq $e.devOpsInfrastructurePrincipalObjectId -and $_.principalType -ceq 'ServicePrincipal' -and $_.roleDefinitionId -imatch ('/'+$role+'$') -and !$_['condition'] -and ($vnetId -ieq $_.scope -or $vnetId.StartsWith($_.scope.TrimEnd('/')+'/',[StringComparison]::OrdinalIgnoreCase))}).Count){throw 'Managed pool DevOpsInfrastructure principal requires reviewed unconditional Reader and Network Contributor VNet grants.'}
    }
    $agent=Test-FabricExecutionAgent $e
    $private=if($e.Contains('privateWorkspace')){Test-FabricExecutionPrivateWorkspace $Manifest $vnet}else{$null}
    return @{kind='ManagedDevOpsPool';poolResourceId=$poolId;agentSubnetId=$subnet.id;devCenterProjectResourceId=$project.id;ado=$agent;privateWorkspace=$private}
}
