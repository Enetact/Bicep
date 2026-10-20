function Set-MdpTestParameters($P,[string]$Scope,[string]$Vnet) {
    $P.devCenterProjectResourceId.value="$Scope/Microsoft.DevCenter/projects/platform-project"
    $P.agentSubnetId.value="$Vnet/subnets/build-agents"
    $P.adoOrganizationUrl.value='https://dev.azure.com/contoso'
    $P.adoPoolName.value='private-build-agents'
    $P.adoProjectNames.value=@('Platform')
    $P.poolAdministratorGroupEmails.value=@('build-admins@contoso.com')
    $P.maximumConcurrency.value=2;$P.agentVmSize.value='Standard_D2ds_v5';$P.agentImageVersion.value='20261018.1.0'
    $P.devOpsInfrastructurePrincipalObjectId.value='33333333-3333-3333-3333-333333333333'
}
function Set-MdpTestInventory($Inventory,$P,[string]$Vnet) {
    $Inventory.resources+=@{id=$P.devCenterProjectResourceId.value;type='Microsoft.DevCenter/projects'}
    $Inventory.networks=@(@{id=$Vnet;location=$P.location.value;addressPrefixes=@('10.0.0.0/16');subnetQuery=@{status='Succeeded'};subnets=@(@{id=$P.agentSubnetId.value;addressPrefixes=@('10.0.8.0/27');delegations=@('Microsoft.DevOpsInfrastructure/pools');privateEndpointCount=0;ipConfigurationCount=0;serviceAssociationLinks=@();resourceNavigationLinks=@()})})
}
function New-MdpTestVnet($P) {
    @{id=($P.agentSubnetId.value -replace '/subnets/[^/]+$','');location=$P.location.value;properties=@{addressSpace=@{addressPrefixes=@('10.0.0.0/16')};dhcpOptions=@{dnsServers=@()}}}
}
function New-MdpTestSubnet($P) {
    @{id=$P.agentSubnetId.value;properties=@{addressPrefix='10.0.8.0/27';delegations=@(@{properties=@{serviceName='Microsoft.DevOpsInfrastructure/pools'}});ipConfigurations=@();privateEndpoints=@();serviceAssociationLinks=@();resourceNavigationLinks=@()}}
}
function New-MdpTestPool($P,[string]$Id) {
    @{id=$Id;name=($Id -split '/')[-1];location=$P.location.value;identity=@{type='None'};properties=@{provisioningState='Succeeded';devCenterProjectResourceId=$P.devCenterProjectResourceId.value;maximumConcurrency=$P.maximumConcurrency.value;agentProfile=@{kind='Stateless'};fabricProfile=@{kind='Vmss';sku=@{name=$P.agentVmSize.value};networkProfile=@{subnetId=$P.agentSubnetId.value};images=@(@{wellKnownImageName=('windows-2022-g2/'+$P.agentImageVersion.value);aliases=@('platform-windows')});osProfile=@{logonType='Service'};storageProfile=@{osDiskStorageAccountType='StandardSSD';dataDisks=@()}};organizationProfile=@{kind='AzureDevOps';organizations=@(@{url=$P.adoOrganizationUrl.value;alias=$P.adoPoolName.value;projects=$P.adoProjectNames.value;parallelism=$P.maximumConcurrency.value;openAccess=$false});permissionProfile=@{kind='SpecificAccounts';groups=$P.poolAdministratorGroupEmails.value;users=@()}}}}
}
