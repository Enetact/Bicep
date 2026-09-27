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
@description('Platform-owned existing monitoring destination. This stack never adopts it.')
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
param privateEndpointSubnetId string
param privateDnsZoneIds object
param readerPrincipalObjectId string

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter }) }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  workload: workload
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  customTags: customTags
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  releaseActivated: releaseActivated
  privateEndpointSubnetId: privateEndpointSubnetId
  privateDnsZoneIds: privateDnsZoneIds
  readerPrincipalObjectId: readerPrincipalObjectId
 }
}
output vaultName string = composition.outputs.vaultName
output vaultResourceId string = composition.outputs.vaultResourceId
output workspaceId string = composition.outputs.workspaceId
