targetScope = 'subscription'

param location string
param resourceGroupName string
param vnetName string
param addressPrefix string
param integrationPrefix string
param endpointPrefix string
param allocationId string
param owner string
param costCenter string

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: { owner: owner, costCenter: costCenter, addressAuthority: 'AVNM', allocationId: allocationId }
}
module network './reserved-vnet.bicep' = {
  name: 'reserved-vnet'
  scope: group
  params: {
    location: location
    vnetName: vnetName
    addressPrefix: addressPrefix
    integrationPrefix: integrationPrefix
    endpointPrefix: endpointPrefix
    allocationId: allocationId
  }
}
output vnetId string = network.outputs.vnetId
output integrationSubnetId string = network.outputs.integrationSubnetId
output privateEndpointSubnetId string = network.outputs.privateEndpointSubnetId
