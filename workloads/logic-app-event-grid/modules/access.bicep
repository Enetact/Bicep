targetScope = 'resourceGroup'
param storageName string
param logicPrincipalId string
param topicPrincipalId string
param deploymentPrincipalId string
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageName }
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' existing = { name: '${storageName}/default/events' }
var queueGrants = [
  { principal: logicPrincipalId, role: '8a0f0c08-91a1-4084-bc3d-661d67233fed' }
  { principal: topicPrincipalId, role: 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a' }
]
resource queueRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for grant in queueGrants: {
  name: guid(queue.id, grant.principal, grant.role)
  scope: queue
  properties: { principalId: grant.principal, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', grant.role) }
}]
var blobGrants = [
  { container: 'receipts', principal: logicPrincipalId, role: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
  { container: 'quarantine', principal: logicPrincipalId, role: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
  { container: 'deadletter', principal: topicPrincipalId, role: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
  { container: 'receipts', principal: deploymentPrincipalId, role: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' }
]
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = [for grant in blobGrants: { name: '${storage.name}/default/${grant.container}' }]
resource blobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (grant,i) in blobGrants: {
  name: guid(containers[i].id, grant.principal, grant.role)
  scope: containers[i]
  properties: { principalId: grant.principal, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', grant.role) }
}]
