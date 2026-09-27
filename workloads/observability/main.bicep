targetScope = 'resourceGroup'
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
param alertActionGroupIds string[]
var stem = '${workload}-${environmentName}'
var tags = { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId }
module monitoring '../../modules/monitoring/observability/main.bicep' = {
 name: 'monitoring'
 params: { name: 'mon-${stem}', location: location, tags: tags, workspaceId: existingLogAnalyticsWorkspaceId, actionGroupIds: alertActionGroupIds }
}

output workbookId string = monitoring.outputs.workbookId
output alertId string = monitoring.outputs.alertId
output workspaceId string = existingLogAnalyticsWorkspaceId
