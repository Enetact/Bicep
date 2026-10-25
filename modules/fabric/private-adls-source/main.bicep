targetScope = 'resourceGroup'

metadata name = 'Optional private ADLS Gen2 Fabric source example'
metadata description = 'Separate source-owned example. Creates private-only ADLS and scoped read access; it does not create a Fabric connection or managed private endpoint.'

@minLength(3)
@maxLength(24)
param storageAccountName string
param location string
param tags object = {}
param existingLogAnalyticsWorkspaceId string
param privateEndpointSubnetId string
@description('Existing private DNS zones with blob and dfs keys; referenced only, never adopted.')
param privateDnsZoneIds object
@description('Observed Fabric workspace identity service-principal object ID, not the workspace UUID or deployment principal.')
param workspaceIdentityPrincipalObjectId string
@allowed(['Standard_LRS', 'Standard_ZRS'])
param skuName string = 'Standard_ZRS'

module lake '../../storage/data-lake/main.bicep' = {
  name: 'source-lake'
  params: {
    name: storageAccountName
    location: location
    tags: tags
    skuName: skuName
    workspaceId: existingLogAnalyticsWorkspaceId
  }
}

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = { parent: account, name: 'default' }
resource landing 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = { parent: blobs, name: 'landing' }
resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(landing.id, workspaceIdentityPrincipalObjectId, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  scope: landing
  properties: {
    principalId: workspaceIdentityPrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  }
  dependsOn: [lake]
}

module endpoints '../../network/private-endpoint/main.bicep' = [for service in ['blob', 'dfs']: {
  name: 'source-${service}-endpoint'
  params: {
    name: 'pe-${storageAccountName}-${service}'
    location: location
    tags: tags
    subnetId: privateEndpointSubnetId
    targetResourceId: lake.outputs.id
    groupIds: [service]
    privateDnsZoneIds: [privateDnsZoneIds[service]]
  }
}]

// These customer-network endpoints serve private agents/clients. Fabric Spark
// needs separately created and approved workspace managed private endpoints.
// Data Factory requires its supported VNet gateway/connection route instead.
output storageAccountName string = lake.outputs.name
output storageResourceId string = lake.outputs.id
output sourceUrl string = 'https://${storageAccountName}.dfs.${environment().suffixes.storage}/landing'
output consumerPrincipalId string = workspaceIdentityPrincipalObjectId
