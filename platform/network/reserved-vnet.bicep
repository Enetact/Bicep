param location string
param vnetName string
param addressPrefix string
param integrationPrefix string
param endpointPrefix string
param allocationId string

// Explicit prefixes are covered by the retained static CIDR. Do not request a second IPAM allocation.
resource vnet 'Microsoft.Network/virtualNetworks@2025-07-01' = {
  name: vnetName
  location: location
  tags: { addressAuthority: 'AVNM', allocationId: allocationId }
  properties: {
    addressSpace: { addressPrefixes: [addressPrefix] }
    subnets: [
      {
        name: 'integration'
        properties: {
          addressPrefix: integrationPrefix
          delegations: [{ name: 'web', properties: { serviceName: 'Microsoft.Web/serverFarms' } }]
        }
      }
      {
        name: 'private-endpoints'
        properties: { addressPrefix: endpointPrefix, privateEndpointNetworkPolicies: 'Disabled' }
      }
    ]
  }
}
output vnetId string = vnet.id
output integrationSubnetId string = '${vnet.id}/subnets/integration'
output privateEndpointSubnetId string = '${vnet.id}/subnets/private-endpoints'
