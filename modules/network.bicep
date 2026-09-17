param name string
param location string
param tags object
param addressPrefix string
param integrationSubnetPrefix string
param privateEndpointSubnetPrefix string

resource integrationNsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${name}-integration'
  location: location
  tags: tags
  properties: { securityRules: [] }
}
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${name}'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [addressPrefix] }
    subnets: [
      {
        name: 'snet-functions'
        properties: {
          addressPrefix: integrationSubnetPrefix
          networkSecurityGroup: { id: integrationNsg.id }
          delegations: [{ name: 'app-service', properties: { serviceName: 'Microsoft.Web/serverFarms' } }]
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

var zoneNames = [
  'privatelink.blob.${environment().suffixes.storage}'
  'privatelink.queue.${environment().suffixes.storage}'
  'privatelink.table.${environment().suffixes.storage}'
  'privatelink.dfs.${environment().suffixes.storage}'
  'privatelink.azurewebsites.net' // Azure public cloud target
]
resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for zoneName in zoneNames: {
  name: zoneName
  location: 'global'
  tags: tags
}]
resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (zoneName, index) in zoneNames: {
  parent: zones[index]
  name: 'link-${name}'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: vnet.id } }
}]

output vnetId string = vnet.id
output integrationSubnetId string = '${vnet.id}/subnets/snet-functions'
output privateEndpointSubnetId string = '${vnet.id}/subnets/snet-private-endpoints'
output blobZoneId string = zones[0].id
output queueZoneId string = zones[1].id
output tableZoneId string = zones[2].id
output dfsZoneId string = zones[3].id
output webZoneId string = zones[4].id
