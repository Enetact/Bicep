targetScope = 'resourceGroup'

@description('Existing connectivity-owned Network Manager name.')
param networkManagerName string
param poolName string
param allocationName string
param ownerDescription string
@allowed([256])
param addressCount int = 256

resource manager 'Microsoft.Network/networkManagers@2025-07-01' existing = {
  name: networkManagerName
}
resource pool 'Microsoft.Network/networkManagers/ipamPools@2025-07-01' existing = {
  parent: manager
  name: poolName
}
// Durable reservation remains owned by the connectivity platform for the network lifetime.
resource allocation 'Microsoft.Network/networkManagers/ipamPools/staticCidrs@2025-07-01' = {
  parent: pool
  name: allocationName
  properties: {
    description: ownerDescription
    numberOfIPAddressesToAllocate: string(addressCount)
  }
}
output allocationId string = allocation.id
output addressPrefixes array = allocation.properties.addressPrefixes
