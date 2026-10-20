targetScope = 'resourceGroup'
import { ResourceNames } from './naming.bicep'

@description('Reviewed concrete standard-v2 names; null uses the legacy-compatible deterministic naming path.')
param resourceNames ResourceNames?
@minLength(3)
@maxLength(10)
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
@description('Source-owned custom tags; required platform ownership tags take precedence.')
param customTags object = {}
@description('Existing platform monitoring destination retained in delivery evidence. Fabric capacity metrics/audit onboarding is a separate control-plane operation.')
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
@allowed(['F64', 'F128', 'F256', 'F512', 'F1024', 'F2048'])
param capacitySku string = 'F64'
@minLength(1)
param capacityAdministrators string[]
@description('Enable only after the workspace exists and its F-capacity assignment is verified. This does not set Fabric deny-public communication policy.')
param enableWorkspacePrivateLink bool = false
@description('Existing Fabric workspace UUID for the optional second ARM networking phase; never an Azure monitoring workspace ID.')
param existingFabricWorkspaceId string = ''
param privateEndpointSubnetId string = ''
@description('When enabled, fabric must identify an existing privatelink.fabric.microsoft.com zone. No shared zones or links are adopted.')
param privateDnsZoneIds object = {}

var stem = '${workload}-${environmentName}'
var suffix = take(uniqueString(subscription().id, resourceGroup().id), 6)
var tags = union(customTags, {
  workload: workload
  environment: environmentName
  owner: owner
  costCenter: costCenter
  releaseActivated: string(releaseActivated)
  deploymentPrincipal: deploymentPrincipalObjectId
})

module capacity '../../modules/fabric/capacity/main.bicep' = {
  name: 'fabric-capacity'
  params: {
    name: resourceNames.?capacity ?? 'fab${workload}${environmentName}${suffix}'
    location: location
    tags: tags
    skuName: capacitySku
    administrators: capacityAdministrators
  }
}

module workspacePrivateLink '../../modules/fabric/workspace-private-link/main.bicep' = if (enableWorkspacePrivateLink) {
  name: 'fabric-workspace-inbound'
  params: {
    name: resourceNames.?workspacePrivateLink ?? 'fpl-${stem}-${suffix}'
    endpointName: resourceNames.?workspaceEndpoint ?? 'pe-${stem}-fabric-${suffix}'
    location: location
    tags: tags
    fabricWorkspaceId: existingFabricWorkspaceId
    privateEndpointSubnetId: privateEndpointSubnetId
    privateDnsZoneId: privateDnsZoneIds.fabric
  }
}

// Azure ARM IDs are not Fabric REST capacity UUIDs. Resolve and verify the latter
// using the Fabric capacities API before creating/assigning Fabric workspaces.
output fabricCapacityResourceId string = capacity.outputs.id
output fabricCapacityName string = capacity.outputs.name
output fabricWorkspaceId string = existingFabricWorkspaceId
output workspacePrivateLinkServiceId string = enableWorkspacePrivateLink ? workspacePrivateLink!.outputs.privateLinkServiceId : ''
output workspacePrivateEndpointId string = enableWorkspacePrivateLink ? workspacePrivateLink!.outputs.privateEndpointId : ''
output workspaceId string = existingLogAnalyticsWorkspaceId
