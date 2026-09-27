targetScope = 'subscription'
param workloadResourceGroupName string
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
@description('Platform-owned existing monitoring destination. This stack never adopts it.')
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
param privateEndpointSubnetId string
param privateDnsZoneIds object
param consumerPrincipalObjectId string

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter } }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  workload: workload
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  releaseActivated: releaseActivated
  privateEndpointSubnetId: privateEndpointSubnetId
  privateDnsZoneIds: privateDnsZoneIds
  consumerPrincipalObjectId: consumerPrincipalObjectId
 }
}
output storageAccountName string = composition.outputs.storageAccountName
output storageResourceId string = composition.outputs.storageResourceId
output workspaceId string = composition.outputs.workspaceId
