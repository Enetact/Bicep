targetScope = 'resourceGroup'

metadata name = 'Microsoft Fabric capacity'
metadata description = 'Azure-billed F64-or-larger Fabric capacity. Fabric workspaces and items use the Fabric control plane.'

@minLength(3)
@maxLength(63)
@description('Reviewed globally unique capacity name: lowercase letter followed by lowercase letters and digits.')
param name string
param location string
param tags object = {}
@description('F64 is the premium baseline in this offering. Review regional availability, quotas and ongoing cost before Apply.')
@allowed(['F64', 'F128', 'F256', 'F512', 'F1024', 'F2048'])
param skuName string = 'F64'
@minLength(1)
@description('Explicit capacity administrator user identities accepted by Microsoft Fabric. These are separate from workspace roles and Azure RBAC.')
param administrators string[]

// Stable GA API; do not add workspace properties or unsupported publicNetworkAccess/identity settings.
// https://learn.microsoft.com/azure/templates/microsoft.fabric/2023-11-01/capacities
resource capacity 'Microsoft.Fabric/capacities@2023-11-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: 'Fabric'
  }
  properties: {
    administration: {
      members: administrators
    }
  }
}

output id string = capacity.id
output name string = capacity.name
output skuName string = capacity.sku.name
