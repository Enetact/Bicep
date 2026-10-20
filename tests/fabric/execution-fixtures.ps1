. "$PSScriptRoot/../managed-devops-pool/fixtures.ps1"
function New-FabricExecutionFixtureManifest {
    $subscription='10000000-0000-0000-0000-000000000002'
    $scope="/subscriptions/$subscription/resourceGroups/platform/providers"
    $vnet="$scope/Microsoft.Network/virtualNetworks/platform"
    $workspace='30000000-0000-0000-0000-000000000001'
    return @{
        schemaVersion=1;enabled=$true;tenantId='10000000-0000-0000-0000-000000000001';subscriptionId=$subscription
        deploymentPrincipalObjectId='10000000-0000-0000-0000-000000000003';environmentName='dev'
        operation=@{kind='CreateLakehouse';workspaceId=$workspace;displayName='Private claims lake'}
        execution=@{
            kind='ManagedDevOpsPool';poolResourceId="$scope/Microsoft.DevOpsInfrastructure/pools/mdp-devopspool-dev-abc123"
            adoOrganizationUrl='https://dev.azure.com/contoso';adoProjectName='Platform';adoPoolName='private-build-agents';adoPoolId=123
            location='eastus2';agentSubnetId="$vnet/subnets/build-agents";devCenterProjectResourceId="$scope/Microsoft.DevCenter/projects/platform-project"
            maximumConcurrency=2;agentVmSize='Standard_D2ds_v5';agentImageVersion='20261018.1.0';poolAdministratorGroupEmails=@('build-admins@contoso.com')
            devOpsInfrastructurePrincipalObjectId='33333333-3333-3333-3333-333333333333'
            privateWorkspace=@{workspaceId=$workspace;apiBaseUrl='https://30000000000000000000000000000001.z30.w.api.fabric.microsoft.com';privateEndpointResourceId="$scope/Microsoft.Network/privateEndpoints/fabric";virtualNetworkResourceId=$vnet;privateDnsZoneResourceId="$scope/Microsoft.Network/privateDnsZones/privatelink.fabric.microsoft.com"}
        }
    }
}
function Set-FabricExecutionFixtureEnvironment($Manifest) {
    $env:BOUND_FABRIC_AGENT_POOL=$Manifest.execution.adoPoolName
    $env:SYSTEM_ACCESSTOKEN='synthetic-job-token-never-sent'
    $env:AGENT_ID='7';$env:AGENT_OS='Windows_NT'
    $env:SYSTEM_JOBID='80000000-0000-0000-0000-000000000001';$env:SYSTEM_PLANID='80000000-0000-0000-0000-000000000002';$env:SYSTEM_TEAMPROJECTID='80000000-0000-0000-0000-000000000003'
    $env:SYSTEM_COLLECTIONURI=$Manifest.execution.adoOrganizationUrl+'/'
    $env:SYSTEM_TEAMPROJECT=$Manifest.execution.adoProjectName
}
function New-FabricExecutionFixture($Manifest) {
    $e=$Manifest.execution;$w=$e.privateWorkspace;$p=Get-FabricManagedPoolParameters $Manifest
    $scope="/subscriptions/$($Manifest.subscriptionId)/resourceGroups/platform/providers"
    $endpointSubnetId=$w.virtualNetworkResourceId+'/subnets/private-endpoints'
    $serviceId="$scope/Microsoft.Fabric/privateLinkServicesForFabric/workspace"
    $nicId="$scope/Microsoft.Network/networkInterfaces/fabric-endpoint"
    $arm=@{}
    $arm[$e.poolResourceId]=New-MdpTestPool $p $e.poolResourceId
    $arm[$w.virtualNetworkResourceId]=New-MdpTestVnet $p
    $arm[$e.agentSubnetId]=New-MdpTestSubnet $p
    $arm[$e.agentSubnetId].properties.serviceAssociationLinks=@(@{properties=@{link=$e.poolResourceId;linkedResourceType='Microsoft.DevOpsInfrastructure/pools'}})
    $arm[$e.agentSubnetId].properties.ipConfigurations=@(@{id='synthetic-running-pool-agent'})
    $arm[$e.devCenterProjectResourceId]=@{id=$e.devCenterProjectResourceId;location=$e.location;properties=@{provisioningState='Succeeded';devCenterId="$scope/Microsoft.DevCenter/devcenters/platform"}}
    $arm[$w.privateEndpointResourceId]=@{id=$w.privateEndpointResourceId;location=$e.location;properties=@{provisioningState='Succeeded';subnet=@{id=$endpointSubnetId};manualPrivateLinkServiceConnections=@();privateLinkServiceConnections=@(@{properties=@{privateLinkServiceId=$serviceId;groupIds=@('workspace');privateLinkServiceConnectionState=@{status='Approved'}}});networkInterfaces=@(@{id=$nicId})}}
    $arm[$serviceId]=@{id=$serviceId;location='global';properties=@{workspaceId=$w.workspaceId;tenantId=$Manifest.tenantId}}
    $arm[$endpointSubnetId]=@{id=$endpointSubnetId;properties=@{delegations=@();privateEndpointNetworkPolicies='Disabled';addressPrefix='10.0.4.0/24'}}
    $arm[$w.privateDnsZoneResourceId]=@{id=$w.privateDnsZoneResourceId;name='privatelink.fabric.microsoft.com'}
    $arm[$nicId]=@{id=$nicId;properties=@{privateEndpoint=@{id=$w.privateEndpointResourceId};ipConfigurations=@(@{properties=@{privateIPAddress='10.0.4.5'}},@{properties=@{privateIPAddress='10.0.4.6'}})}}
    $collections=@{}
    $collections[$w.privateEndpointResourceId+'/privateDnsZoneGroups']=@(@{properties=@{provisioningState='Succeeded';privateDnsZoneConfigs=@(@{properties=@{privateDnsZoneId=$w.privateDnsZoneResourceId}})}})
    $collections[$w.privateDnsZoneResourceId+'/virtualNetworkLinks']=@(@{properties=@{virtualNetwork=@{id=$w.virtualNetworkResourceId};provisioningState='Succeeded';virtualNetworkLinkState='Completed';registrationEnabled=$false}})
    $grants=@('acdd72a7-3385-48ef-bd42-f606fba81ae7','4d97b98b-1d4f-4787-a291-c67834d212e7' | ForEach-Object {@{principalId=$e.devOpsInfrastructurePrincipalObjectId;principalType='ServicePrincipal';roleDefinitionId="/subscriptions/$($Manifest.subscriptionId)/providers/Microsoft.Authorization/roleDefinitions/$_";scope=$w.virtualNetworkResourceId;condition=''}})
    return @{
        arm=$arm;collections=$collections;grants=$grants;dns=@('10.0.4.5');tcpFails=$false
        adoPool=@{id=$e.adoPoolId;name=$e.adoPoolName;poolType='automation'}
        adoAgent=@{id=7;enabled=$true;status='online';assignedRequest=@{jobId='80000000-0000-0000-0000-000000000001';planId='80000000-0000-0000-0000-000000000002';scopeId='80000000-0000-0000-0000-000000000003';poolId=$e.adoPoolId}}
        policy=@{inbound=@{publicAccessRules=@{defaultAction='Deny'}}}
        workspace=@{id=$w.workspaceId;capacityId='20000000-0000-0000-0000-000000000001';apiEndpoint=$w.apiBaseUrl}
        calls=[Collections.Generic.List[object]]::new()
    }
}
