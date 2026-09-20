param name string
param tags object
param virtualNetworkId string
param linkName string
resource zone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: name
  location: 'global'
  tags: tags
}
resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: zone
  name: linkName
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: virtualNetworkId } }
}
output id string = zone.id
