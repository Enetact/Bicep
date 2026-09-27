param name string
param location string
param tags object
param workspaceId string
@minLength(1)
param actionGroupIds string[]
resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
 name: guid(resourceGroup().id, name)
 location: location
 tags: tags
 kind: 'shared'
 properties: {
  displayName: name
  category: 'workbook'
  sourceId: workspaceId
  serializedData: string({ version: 'Notebook/1.0', items: [{ type: 3, content: { version: 'KqlItem/1.0', query: 'Heartbeat | summarize LastSeen=max(TimeGenerated) by Computer', size: 0, queryType: 0, resourceType: 'microsoft.operationalinsights/workspaces' } }], isLocked: false })
 }
}
resource alert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
 name: '${name}-heartbeat'
 location: location
 tags: tags
 properties: {
  displayName: 'Missing heartbeat for previously reporting machines'
  description: 'Only detects machines observed in the prior day; never-seen machines require separate inventory.'
  severity: 2
  enabled: true
  evaluationFrequency: 'PT5M'
  windowSize: 'P1D'
  scopes: [workspaceId]
  criteria: { allOf: [{ query: 'Heartbeat | summarize LastSeen=max(TimeGenerated) by Computer | where LastSeen < ago(15m)', timeAggregation: 'Count', operator: 'GreaterThan', threshold: 0, failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 } }] }
  actions: { actionGroups: actionGroupIds }
  autoMitigate: true
 }
}
output workbookId string = workbook.id
output alertId string = alert.id
