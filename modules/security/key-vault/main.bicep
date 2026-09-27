@description('Globally unique vault name.')
param name string
param location string
param tags object
param workspaceId string
@description('Reviewed service principal allowed to read secrets; no secret values are deployed.')
param readerPrincipalId string
resource vault 'Microsoft.KeyVault/vaults@2025-05-01' = {
 name: name
 location: location
 tags: tags
 properties: {
  tenantId: tenant().tenantId
  sku: { family: 'A', name: 'standard' }
  enableRbacAuthorization: true
  enableSoftDelete: true
  enablePurgeProtection: true
  softDeleteRetentionInDays: 90
  publicNetworkAccess: 'Disabled'
  networkAcls: { bypass: 'None', defaultAction: 'Deny' }
  accessPolicies: []
 }
}
resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(vault.id, readerPrincipalId, '4633458b-17de-408a-b874-0445c86b69e6')
 scope: vault
 properties: { principalId: readerPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') }
}
resource audit 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
 name: 'vault-audit'
 scope: vault
 properties: { workspaceId: workspaceId, logs: [{ category: 'AuditEvent', enabled: true }] }
}
output id string = vault.id
output name string = vault.name
