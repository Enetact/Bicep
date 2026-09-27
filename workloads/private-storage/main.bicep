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
param privateEndpointSubnetId string
param privateDnsZoneIds object
param consumerPrincipalObjectId string
var stem = '${workload}-${environmentName}'
var tags = { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId }
var storageName = 'st${workload}${environmentName}${take(uniqueString(subscription().id, resourceGroup().id), 6)}'
module storage '../../modules/storage/storage-account/main.bicep' = {
 name: 'storage'
 params: { name: storageName, location: location, tags: tags, skuName: environmentName == 'prod' ? 'Standard_ZRS' : 'Standard_LRS', containers: ['workspace'], queueNames: ['work'], workspaceId: existingLogAnalyticsWorkspaceId }
}
resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageName }
resource dataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(account.id, consumerPrincipalObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
 scope: account
 properties: { principalId: consumerPrincipalObjectId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') }
 dependsOn: [storage]
}
resource queueAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(account.id, consumerPrincipalObjectId, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
 scope: account
 properties: { principalId: consumerPrincipalObjectId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88') }
 dependsOn: [storage]
}
module storageEndpoints '../../modules/network/private-endpoint/main.bicep' = [for service in ['blob', 'queue']: {
 name: 'pe-${service}'
 params: { name: 'pe-${stem}-${service}', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: storage.outputs.id, groupIds: [service], privateDnsZoneIds: [privateDnsZoneIds[service]] }
}]

output storageAccountName string = storage.outputs.name
output storageResourceId string = storage.outputs.id
output workspaceId string = existingLogAnalyticsWorkspaceId
