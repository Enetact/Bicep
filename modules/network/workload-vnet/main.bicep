param name string
param location string
param tags object
param addressPrefix string
param integrationSubnetPrefix string
param privateEndpointSubnetPrefix string
resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-${name}-integration'
  location: location
  tags: tags
  properties: { securityRules: [] }
}
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [addressPrefix] }
    subnets: [
      { name: 'snet-integration', properties: { addressPrefix: integrationSubnetPrefix, networkSecurityGroup: { id: nsg.id }, delegations: [{ name: 'app-service', properties: { serviceName: 'Microsoft.Web/serverFarms' } }], privateEndpointNetworkPolicies: 'Enabled' } }
      { name: 'snet-private-endpoints', properties: { addressPrefix: privateEndpointSubnetPrefix, privateEndpointNetworkPolicies: 'Disabled' } }
    ]
  }
}
output id string = vnet.id
