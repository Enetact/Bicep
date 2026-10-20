targetScope = 'subscription'
import { ResourceNames } from './naming.bicep'

param resourceNames ResourceNames?
param workloadResourceGroupName string
@minLength(3)
@maxLength(10)
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
param customTags object = {}
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
@allowed(['F64', 'F128', 'F256', 'F512', 'F1024', 'F2048'])
param capacitySku string = 'F64'
@minLength(1)
param capacityAdministrators string[]
param enableWorkspacePrivateLink bool = false
param existingFabricWorkspaceId string = ''
param privateEndpointSubnetId string = ''
param privateDnsZoneIds object = {}

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: workloadResourceGroupName
  location: location
  tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter })
}

module composition './main.bicep' = {
  name: 'workload'
  scope: group
  params: {
    resourceNames: resourceNames
    workload: workload
    environmentName: environmentName
    location: location
    owner: owner
    costCenter: costCenter
    customTags: customTags
    existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
    deploymentPrincipalObjectId: deploymentPrincipalObjectId
    releaseActivated: releaseActivated
    capacitySku: capacitySku
    capacityAdministrators: capacityAdministrators
    enableWorkspacePrivateLink: enableWorkspacePrivateLink
    existingFabricWorkspaceId: existingFabricWorkspaceId
    privateEndpointSubnetId: privateEndpointSubnetId
    privateDnsZoneIds: privateDnsZoneIds
  }
}

output fabricCapacityResourceId string = composition.outputs.fabricCapacityResourceId
output fabricCapacityName string = composition.outputs.fabricCapacityName
output fabricWorkspaceId string = composition.outputs.fabricWorkspaceId
output workspacePrivateLinkServiceId string = composition.outputs.workspacePrivateLinkServiceId
output workspacePrivateEndpointId string = composition.outputs.workspacePrivateEndpointId
output workspaceId string = composition.outputs.workspaceId
