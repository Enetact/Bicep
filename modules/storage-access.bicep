param hostAccountName string
param uploadAccountName string
param uploadContainerName string
param deploymentContainerName string
param principalId string
param ledgerContainerName string
param transferQueueName string
param recoveryOperatorGroupObjectId string
param uploaderGroupObjectId string
param packagePublisherObjectId string

resource host 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: hostAccountName }

resource sourceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  name: '${uploadAccountName}/default/${uploadContainerName}'
}
resource packages 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  name: '${hostAccountName}/default/${deploymentContainerName}'
}

// Documented identity-based host roles for the polling BlobTrigger dispatcher.
var hostRoles = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '17d1049b-9a84-46fb-8f53-869881c3d3ab' // Storage Account Contributor
]
resource hostAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in hostRoles: {
  name: guid(host.id, principalId, role)
  scope: host
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
  }
}]
resource upload 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: uploadAccountName }
resource sourceReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(upload.id, principalId, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: upload
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}
resource ledgerContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  name: '${uploadAccountName}/default/${ledgerContainerName}'
}
// Runtime account-level Blob Data Owner already covers this ledger container.
resource runtimeQueues 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' existing = [for queueName in [transferQueueName, '${transferQueueName}-poison']: {
  name: '${uploadAccountName}/default/${queueName}'
}]
resource triggerQueueAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(upload.id, principalId, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  scope: upload
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}
resource recoveryLedger 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(recoveryOperatorGroupObjectId)) {
  name: guid(ledgerContainer.id, recoveryOperatorGroupObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: ledgerContainer
  properties: {
    principalId: recoveryOperatorGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
resource recoveryQueueSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(recoveryOperatorGroupObjectId)) {
  name: guid(runtimeQueues[0].id, recoveryOperatorGroupObjectId, 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a')
  scope: runtimeQueues[0]
  properties: {
    principalId: recoveryOperatorGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a')
  }
}
resource uploaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(uploaderGroupObjectId)) {
  name: guid(sourceContainer.id, uploaderGroupObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: sourceContainer
  properties: {
    principalId: uploaderGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(packagePublisherObjectId)) {
  name: guid(packages.id, packagePublisherObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: packages
  properties: {
    principalId: packagePublisherObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
