targetScope = 'resourceGroup'
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
var stem = '${workload}-${environmentName}'
var suffix = uniqueString(resourceGroup().id)
var appName = 'logic-${stem}-${suffix}'
var runtimeName = take('strt${workload}${environmentName}${suffix}', 24)
var eventName = take('stev${workload}${environmentName}${suffix}', 24)
var topicName = 'evgt-${stem}'
var tags = union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, workloadType: 'logic-app-event-grid', trustedServiceReview: trustedServiceException.reviewReference, runtimeCredentialReview: runtimeStorageCredentialException.reviewReference })
module prerequisites './modules/prerequisites.bicep' = if (!empty(prerequisitePlan)) {
  name: 'workload-prerequisites'
  params: { plan: prerequisitePlan, location: location, tags: tags }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  dependsOn: [prerequisites]
  name: 'appi-${stem}'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: existingLogAnalyticsWorkspaceId }
}
module runtime './modules/storage.bicep' = { name: 'runtime-storage', dependsOn: [prerequisites], params: { name: runtimeName, location: location, tags: tags, runtime: true, workspaceId: existingLogAnalyticsWorkspaceId } }
module events './modules/storage.bicep' = { name: 'event-storage', dependsOn: [prerequisites], params: { name: eventName, location: location, tags: tags, runtime: false, workspaceId: existingLogAnalyticsWorkspaceId } }
module topic '../../modules/event-grid/topic/main.bicep' = { name: 'event-topic', params: { name: topicName, location: location, tags: tags } }
var storageEndpoints = [
  { name: 'runtime-blob', target: resourceId('Microsoft.Storage/storageAccounts', runtimeName), group: 'blob' }
  { name: 'runtime-queue', target: resourceId('Microsoft.Storage/storageAccounts', runtimeName), group: 'queue' }
  { name: 'runtime-table', target: resourceId('Microsoft.Storage/storageAccounts', runtimeName), group: 'table' }
  { name: 'runtime-file', target: resourceId('Microsoft.Storage/storageAccounts', runtimeName), group: 'file' }
  { name: 'event-blob', target: resourceId('Microsoft.Storage/storageAccounts', eventName), group: 'blob' }
  { name: 'event-queue', target: resourceId('Microsoft.Storage/storageAccounts', eventName), group: 'queue' }
]
module endpoints '../../modules/network/private-endpoint/main.bicep' = [for endpoint in storageEndpoints: {
  name: 'pe-${endpoint.name}'
  dependsOn: [prerequisites, runtime, events]
  params: { name: 'pe-${stem}-${endpoint.name}', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: endpoint.target, groupIds: [endpoint.group], privateDnsZoneIds: [privateDnsZoneIds[endpoint.group]] }
}]
module host '../../modules/logic-app/standard/main.bicep' = {
  name: 'workflow-host'
  params: { name: appName, location: location, tags: tags, runtimeStorageName: runtimeName, eventStorageName: eventName, topicId: topic.outputs.id, integrationSubnetId: integrationSubnetId, instrumentationConnectionString: insights.properties.ConnectionString, releaseActivated: releaseActivated, skuName: hostingSku }
  dependsOn: [endpoints]
}
module appEndpoint '../../modules/network/private-endpoint/main.bicep' = { name: 'pe-app', params: { name: 'pe-${stem}-app', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: host.outputs.id, groupIds: ['sites'], privateDnsZoneIds: [privateDnsZoneIds.sites] } }
module topicEndpoint '../../modules/network/private-endpoint/main.bicep' = { name: 'pe-topic', dependsOn: [prerequisites], params: { name: 'pe-${stem}-topic', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: topic.outputs.id, groupIds: ['topic'], privateDnsZoneIds: [privateDnsZoneIds.topic] } }
module access './modules/access.bicep' = { name: 'event-access', params: { storageName: eventName, logicPrincipalId: host.outputs.principalId, topicPrincipalId: topic.outputs.principalId, deploymentPrincipalId: deploymentPrincipalObjectId } }
resource topicRef 'Microsoft.EventGrid/topics@2025-02-15' existing = { name: topicName }
resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(topicRef.id, deploymentPrincipalObjectId, 'event-publisher')
  scope: topicRef
  properties: { principalId: deploymentPrincipalObjectId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'd5a91429-5739-47e2-a06b-3470a27159e7') }
  dependsOn: [topic]
}
module delivery '../../modules/event-grid/event-subscription/main.bicep' = { name: 'event-delivery', params: { topicName: topicName, storageAccountId: events.outputs.id, queueName: 'events', deadLetterContainer: 'deadletter' }, dependsOn: [access] }
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'topic-audit'
  scope: topicRef
  properties: { workspaceId: existingLogAnalyticsWorkspaceId, logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
  dependsOn: [topic]
}
resource backlog 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${stem}-workflow-failures'
  dependsOn: [prerequisites]
  location: location
  tags: tags
  properties: {
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [existingLogAnalyticsWorkspaceId]
    criteria: { allOf: [{ query: 'AppTraces | where AppRoleName == "${appName}" | where SeverityLevel >= 3', timeAggregation: 'Count', operator: 'GreaterThan', threshold: 0, failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 } }] }
    actions: { actionGroups: alertActionGroupIds }
  }
}
output logicAppName string = appName
output logicAppResourceId string = host.outputs.id
output runtimeStorageAccountName string = runtimeName
output eventStorageAccountName string = eventName
output topicId string = topic.outputs.id
output topicEndpoint string = topic.outputs.endpoint
output workspaceId string = existingLogAnalyticsWorkspaceId

resource deliveryAlerts 'Microsoft.Insights/metricAlerts@2018-03-01' = [for metric in ['DeliveryAttemptFailCount', 'DeadLetteredCount'] : {
  name: 'alert-${stem}-${metric}'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    severity: 2
    scopes: [topic.outputs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: { 'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria', allOf: [{ name: 'count', criterionType: 'StaticThresholdCriterion', metricNamespace: 'Microsoft.EventGrid/topics', metricName: metric, operator: 'GreaterThan', threshold: 0, timeAggregation: 'Total' }] }
    actions: [for id in alertActionGroupIds: { actionGroupId: id }]
  }
}]
resource queueBacklog 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-${stem}-queue-backlog'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    severity: 2
    scopes: ['${events.outputs.id}/queueServices/default']
    evaluationFrequency: 'PT5M'
    windowSize: 'PT1H'
    criteria: { 'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria', allOf: [{ name: 'count', criterionType: 'StaticThresholdCriterion', metricNamespace: 'Microsoft.Storage/storageAccounts/queueServices', metricName: 'QueueMessageCount', operator: 'GreaterThan', threshold: 100, timeAggregation: 'Average' }] }
    actions: [for id in alertActionGroupIds: { actionGroupId: id }]
  }
}
resource quarantineAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${stem}-quarantine'
  dependsOn: [prerequisites]
  location: location
  tags: tags
  properties: {
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [existingLogAnalyticsWorkspaceId]
    criteria: { allOf: [{ query: 'StorageBlobLogs | where AccountName == "${eventName}" | where Uri has "/quarantine/" and OperationName == "PutBlob" and StatusCode == "201"', timeAggregation: 'Count', operator: 'GreaterThan', threshold: 0, failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 } }] }
    actions: { actionGroups: alertActionGroupIds }
  }
}
