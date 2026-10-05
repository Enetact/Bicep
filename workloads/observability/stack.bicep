targetScope = 'subscription'
import { ResourceNames } from './naming.bicep'
@description('Concrete new-instance name map; null preserves the legacy deployment path.')
param resourceNames ResourceNames?
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
param alertActionGroupIds string[]

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter }) }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  workload: workload
  resourceNames: resourceNames
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  customTags: customTags
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  releaseActivated: releaseActivated
  alertActionGroupIds: alertActionGroupIds
 }
}
output workbookId string = composition.outputs.workbookId
output alertId string = composition.outputs.alertId
output workspaceId string = composition.outputs.workspaceId
