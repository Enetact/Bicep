targetScope = 'subscription'
param workloadResourceGroupName string
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
@description('Reviewed source-owned custom tags. Required platform tags take precedence.')
param customTags object = {}
param costCenter string
param prerequisitePlan object = {}
param integrationSubnetId string
param privateEndpointSubnetId string
param privateDnsZoneIds object
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param trustedServiceException object
param runtimeStorageCredentialException object
param releaseActivated bool = false
@allowed(['WS1', 'WS2', 'WS3'])
param hostingSku string = 'WS1'
param alertActionGroupIds array = []
resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter }) }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  prerequisitePlan: prerequisitePlan
  workload: workload
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  customTags: customTags
  integrationSubnetId: integrationSubnetId
  privateEndpointSubnetId: privateEndpointSubnetId
  privateDnsZoneIds: privateDnsZoneIds
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  trustedServiceException: trustedServiceException
  runtimeStorageCredentialException: runtimeStorageCredentialException
  releaseActivated: releaseActivated
  hostingSku: hostingSku
  alertActionGroupIds: alertActionGroupIds
 }
}
output logicAppName string = composition.outputs.logicAppName
output logicAppResourceId string = composition.outputs.logicAppResourceId
output runtimeStorageAccountName string = composition.outputs.runtimeStorageAccountName
output eventStorageAccountName string = composition.outputs.eventStorageAccountName
output topicId string = composition.outputs.topicId
output topicEndpoint string = composition.outputs.topicEndpoint
output workspaceId string = composition.outputs.workspaceId
