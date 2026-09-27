param name string
param location string
param tags object
param workspaceId string
param workerPrincipalId string
param senderPrincipalId string
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
 name: name
 location: location
 tags: tags
 sku: { name: 'Premium', tier: 'Premium', capacity: 1 }
 properties: { disableLocalAuth: true, publicNetworkAccess: 'Disabled', minimumTlsVersion: '1.2', premiumMessagingPartitions: 1 }
}
resource rules 'Microsoft.ServiceBus/namespaces/networkRuleSets@2024-01-01' = {
 parent: bus
 name: 'default'
 properties: { defaultAction: 'Deny', publicNetworkAccess: 'Disabled', trustedServiceAccessEnabled: false, ipRules: [], virtualNetworkRules: [] }
}
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
 parent: bus
 name: 'work'
 properties: { maxDeliveryCount: 10, lockDuration: 'PT1M', defaultMessageTimeToLive: 'P14D', deadLetteringOnMessageExpiration: true, requiresDuplicateDetection: true, duplicateDetectionHistoryTimeWindow: 'PT10M', enablePartitioning: false }
}
resource worker 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(bus.id, workerPrincipalId, '090c5cfd-751d-490a-894a-3ce6f1109419')
 scope: bus
 properties: { principalId: workerPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419') }
}
resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(queue.id, senderPrincipalId, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
 scope: queue
 properties: { principalId: senderPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39') }
}
resource audit 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
 name: 'bus-audit'
 scope: bus
 properties: { workspaceId: workspaceId, logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
}
output id string = bus.id
output name string = bus.name
