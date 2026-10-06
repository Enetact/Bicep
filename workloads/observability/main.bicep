targetScope = 'resourceGroup'
import { ResourceNames } from './naming.bicep'
@description('Reviewed concrete name map for a new standard-v2 instance; null preserves legacy naming. Not yet accepted by self-service deployment requests.')
param resourceNames ResourceNames?
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
var stem = '${workload}-${environmentName}'
var tags = union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId })
module monitoring '../../modules/monitoring/observability/main.bicep' = {
 name: 'monitoring'
 params: { name: resourceNames.?displayName ?? 'mon-${stem}', workbookName: resourceNames.?workbook ?? guid(resourceGroup().id, 'mon-${stem}'), alertName: resourceNames.?alert ?? 'mon-${stem}-heartbeat', location: location, tags: tags, workspaceId: existingLogAnalyticsWorkspaceId, actionGroupIds: alertActionGroupIds }
}

output workbookId string = monitoring.outputs.workbookId
output alertId string = monitoring.outputs.alertId
output workspaceId string = existingLogAnalyticsWorkspaceId
