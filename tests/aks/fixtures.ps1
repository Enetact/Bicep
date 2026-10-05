function Set-AksTestParameters($P,[string]$Scope,[string]$Vnet) {
    $values=@{kubernetesVersion='1.35.3';nodeSubnetId="$Vnet/subnets/aks";privateDnsZoneId="$Scope/Microsoft.Network/privateDnsZones/privatelink.eastus2.azmk8s.io";controlPlaneIdentityId="$Scope/Microsoft.ManagedIdentity/userAssignedIdentities/control";kubeletIdentityId="$Scope/Microsoft.ManagedIdentity/userAssignedIdentities/kubelet";registryResourceId="$Scope/Microsoft.ContainerRegistry/registries/fixtureacr";kubeletClientId='22222222-2222-2222-2222-222222222222';kubeletObjectId='33333333-3333-3333-3333-333333333333';adminGroupObjectIds=@('44444444-4444-4444-4444-444444444444');podCidr='10.200.0.0/16';serviceCidr='10.201.0.0/24';dnsServiceIP='10.201.0.10';nodeVmSize='Standard_D4ds_v5';maxUserNodes=6;applicationNamespace='platform-apps';serviceAccountName='platform-app'}
    foreach($key in $values.Keys){$P[$key]=@{value=$values[$key]}}
}
function Set-AksTestInventory($Inventory,$P,[string]$Vnet) {
    foreach($key in @('controlPlaneIdentityId','kubeletIdentityId','registryResourceId')){$Inventory.resources+=@{id=$P[$key].value;type=($P[$key].value -split '/providers/')[1] -replace '/[^/]+$',''}}
    $Inventory.networks=@(@{id=$Vnet;location='eastus2';subnetQuery=@{status='Succeeded'};subnets=@(@{id=$P.nodeSubnetId.value;delegations=@();addressPrefixes=@('10.10.0.0/24')})})
    $Inventory.privateDnsZones=@(@{id=$P.privateDnsZoneId.value})
}
