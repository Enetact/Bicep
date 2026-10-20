targetScope = 'resourceGroup'

metadata name = 'Fabric workspace inbound private link'
metadata description = 'Workspace-scoped Fabric Private Link service and endpoint using an approved existing subnet and DNS zone. Workspace creation and communication policy remain Fabric API operations.'

param name string
param endpointName string
param location string
param tags object = {}
@minLength(36)
@maxLength(36)
@description('Existing workspace UUID assigned to a Fabric F capacity, in the current subscription tenant.')
param fabricWorkspaceId string
@description('Approved endpoint subnet. Reserve at least ten addresses per workspace endpoint and verify reachability.')
param privateEndpointSubnetId string
@description('Existing platform-owned privatelink.fabric.microsoft.com private DNS zone linked to the caller network.')
param privateDnsZoneId string

// Official documented workspace-level ARM resource, not Microsoft.Network/privateLinkServices.
// https://learn.microsoft.com/fabric/security/security-workspace-level-private-links-set-up
// The provider does not support tags on this resource.
resource service 'Microsoft.Fabric/privateLinkServicesForFabric@2024-06-01' = {
  name: name
  location: 'global'
  properties: {
    tenantId: subscription().tenantId
    workspaceId: fabricWorkspaceId
  }
}

module endpoint '../../network/private-endpoint/main.bicep' = {
  name: 'workspace-private-endpoint'
  params: {
    name: endpointName
    location: location
    tags: tags
    subnetId: privateEndpointSubnetId
    targetResourceId: service.id
    groupIds: ['workspace']
    privateDnsZoneIds: [privateDnsZoneId]
  }
}

// Creating an endpoint does not disable public access. Set and verify the Fabric
// communication policy only after this endpoint, DNS, private runner and clients work.
output privateLinkServiceId string = service.id
output privateEndpointId string = endpoint.outputs.privateEndpointId
output fabricWorkspaceId string = fabricWorkspaceId
