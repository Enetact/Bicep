targetScope = 'resourceGroup'

metadata name = 'Private endpoint'
metadata description = 'Private endpoint and zone group referencing existing target, subnet and DNS zones.'

@description('Deterministic private endpoint resource name.')
param name string
@description('Azure region of the endpoint subnet.')
param location string
@description('Resource ownership and classification tags.')
param tags object = {}
@description('Approved private endpoint subnet resource ID.')
param subnetId string
@description('Existing or workload-created target. This module never owns the subnet or DNS zones.')
param targetResourceId string
@minLength(1)
@description('Private Link subresource group IDs supported by the target.')
param groupIds string[]
@minLength(1)
@description('Existing private DNS zone resource IDs associated with the endpoint.')
param privateDnsZoneIds string[]

resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [{
      name: '${name}-connection'
      properties: {
        privateLinkServiceId: targetResourceId
        groupIds: groupIds
      }
    }]
  }
}
resource dns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [for zoneId in privateDnsZoneIds: {
      // Preserve the existing single-zone configuration name during this refactor.
      name: length(privateDnsZoneIds) == 1 ? groupIds[0] : 'zone-${uniqueString(toLower(zoneId))}'
      properties: { privateDnsZoneId: zoneId }
    }]
  }
}

output privateEndpointId string = endpoint.id
output networkInterfaceIds array = map(endpoint.properties.networkInterfaces, nic => nic.id)
