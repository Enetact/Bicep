// Separate platform lifecycle, with pre-existing subnet and central DNS zone IDs.
targetScope = 'resourceGroup'
@minLength(5)
@maxLength(50)
param registryName string
param location string
param tags object
param privateEndpointSubnetId string
param privateDnsZoneIds string[]

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Premium' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Disabled'
    networkRuleBypassOptions: 'None'
    roleAssignmentMode: 'LegacyRegistryPermissions'
    networkRuleSet: { defaultAction: 'Deny' }
    policies: { azureADAuthenticationAsArmPolicy: { status: 'enabled' } }
  }
}
module endpoint '../../modules/network/private-endpoint/main.bicep' = {
  name: 'registry-private-access'
  params: {
    name: 'pe-${registryName}'
    location: location
    tags: tags
    subnetId: privateEndpointSubnetId
    targetResourceId: registry.id
    groupIds: ['registry']
    privateDnsZoneIds: privateDnsZoneIds
  }
}
output registryId string = registry.id
output loginServer string = registry.properties.loginServer
output privateEndpointId string = endpoint.outputs.privateEndpointId
