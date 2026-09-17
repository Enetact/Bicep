param name string
param location string
param tags object
param retentionDays int
param dailyCapGb int
param principalId string
param alertActionGroupIds array
param operatorGroupObjectId string
param enableRuntimeAlerts bool = false

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
    workspaceCapping: { dailyQuotaGb: dailyCapGb }
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}
resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, principalId, '3913510d-42f4-4e42-8a64-420c390055eb')
  scope: insights
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}
resource failures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${name}-function-errors'
  location: location
  tags: tags
  properties: {
    displayName: '${name}: function errors'
    description: 'Review transfer-work-poison, quarantined ledger records, and recovery timers. No payloads in notifications.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspace.id]
    skipQueryValidation: true // FunctionAppLogs may not yet exist during bootstrap.
    criteria: { allOf: [{
      query: 'FunctionAppLogs | where Level in ("Error", "Critical")'
      timeAggregation: 'Count'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    actions: { actionGroups: alertActionGroupIds }
    autoMitigate: true
  }
}
output workspaceId string = workspace.id
resource logReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorGroupObjectId)) {
  name: guid(workspace.id, operatorGroupObjectId, '73c42c96-874c-492b-b04d-ab87d138a893')
  scope: workspace
  properties: {
    principalId: operatorGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')
  }
}
@secure()
output connectionString string = insights.properties.ConnectionString


resource recoveryFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${name}-recovery-review'
  location: location
  tags: tags
  properties: {
    displayName: '${name}: poison messages or transfers needing review'
    severity: 1
    enabled: enableRuntimeAlerts
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [workspace.id]
    skipQueryValidation: true
    criteria: { allOf: [{
      query: 'FunctionAppLogs | where Message has_any ("PoisonBacklog", "TransferNeedsReview", "TransferQuarantined", "SourceMissing")'
      timeAggregation: 'Count'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    actions: { actionGroups: alertActionGroupIds }
    autoMitigate: true
  }
}
resource recoveryHeartbeat 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${name}-recovery-heartbeat'
  location: location
  tags: tags
  properties: {
    displayName: '${name}: reconciliation heartbeat absent'
    description: 'Default five-minute timer must emit a heartbeat within 30 minutes. Reassess this window if scheduling changes.'
    severity: 1
    enabled: enableRuntimeAlerts
    evaluationFrequency: 'PT5M'
    windowSize: 'PT30M'
    scopes: [workspace.id]
    skipQueryValidation: true
    criteria: { allOf: [{
      query: 'FunctionAppLogs | where Message has "ReconcileHeartbeat" | summarize Heartbeats=count() | where Heartbeats == 0'
      timeAggregation: 'Count'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    actions: { actionGroups: alertActionGroupIds }
    autoMitigate: true
  }
}
