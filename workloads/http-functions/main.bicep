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
param integrationSubnetId string
param packageBlobName string = 'releases/preview.zip'
param apiClientId string
param allowedClientApplications string[]
var stem = '${workload}-${environmentName}'
var tags = { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId }
var storageName = 'st${workload}${environmentName}${take(uniqueString(subscription().id, resourceGroup().id), 6)}'
module storage '../../modules/storage/storage-account/main.bicep' = {
 name: 'storage'
 params: { name: storageName, location: location, tags: tags, skuName: environmentName == 'prod' ? 'Standard_ZRS' : 'Standard_LRS', containers: ['packages', 'receipts'], queueNames: [], workspaceId: existingLogAnalyticsWorkspaceId }
}
resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageName }
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = { name: 'id-${stem}', location: location, tags: tags }
resource runtimeRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in ['b7e6dc6d-f1e8-4753-8033-0f276bb0955b', '974c5e8b-45b9-4653-ba55-5f855dd0fb88', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3']: {
 name: guid(account.id, identity.id, role)
 scope: account
 properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role) }
 dependsOn: [storage]
}]
resource dataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
 name: guid(account.id, deploymentPrincipalObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
 scope: account
 properties: { principalId: deploymentPrincipalObjectId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') }
 dependsOn: [storage]
}
module storageEndpoints '../../modules/network/private-endpoint/main.bicep' = [for service in ['blob', 'queue', 'table']: {
 name: 'pe-${service}'
 params: { name: 'pe-${stem}-${service}', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: storage.outputs.id, groupIds: [service], privateDnsZoneIds: [privateDnsZoneIds[service]] }
}]
module app '../../modules/compute/private-functions/main.bicep' = if (releaseActivated) {
 name: 'application'
 params: { name: 'func-${stem}-${take(uniqueString(resourceGroup().id), 6)}', location: location, tags: tags, identityId: identity.id, identityClientId: identity.properties.clientId, integrationSubnetId: integrationSubnetId, storageName: storage.outputs.name, packageBlobName: packageBlobName, workspaceId: existingLogAnalyticsWorkspaceId, mode: 'http', apiClientId: apiClientId, allowedClientApplications: allowedClientApplications }
 dependsOn: [runtimeRoles]
}
module appEndpoint '../../modules/network/private-endpoint/main.bicep' = if (releaseActivated) {
 name: 'pe-app'
 params: { name: 'pe-${stem}-app', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: app!.outputs.id, groupIds: ['sites'], privateDnsZoneIds: [privateDnsZoneIds.sites] }
}

output storageAccountName string = storage.outputs.name
output storageResourceId string = storage.outputs.id
output functionAppName string = releaseActivated ? app!.outputs.name : ''
output functionAppId string = releaseActivated ? app!.outputs.id : ''
output workspaceId string = existingLogAnalyticsWorkspaceId
