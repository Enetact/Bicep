// Cross-resource-group/subscription module. Does not create or reconfigure the destination.
param accountName string
param containerName string
param principalId string
param deploymentPrincipalObjectId string = ''

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: accountName }
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  name: '${accountName}/default/${containerName}'
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(container.id, principalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: container
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
output accountId string = account.id
resource pipelineReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deploymentPrincipalObjectId)) {
  name: guid(container.id, deploymentPrincipalObjectId, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  scope: container
  properties: {
    principalId: deploymentPrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  }
}
output blobEndpoint string = account.properties.primaryEndpoints.blob
