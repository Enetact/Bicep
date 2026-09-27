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
param readerPrincipalObjectId string
var stem = '${workload}-${environmentName}'
var tags = { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId }
module vault '../../modules/security/key-vault/main.bicep' = {
 name: 'vault'
 params: { name: 'kv-${stem}-${take(uniqueString(resourceGroup().id), 4)}', location: location, tags: tags, workspaceId: existingLogAnalyticsWorkspaceId, readerPrincipalId: readerPrincipalObjectId }
}
module endpoint '../../modules/network/private-endpoint/main.bicep' = {
 name: 'pe-vault'
 params: { name: 'pe-${stem}-vault', location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: vault.outputs.id, groupIds: ['vault'], privateDnsZoneIds: [privateDnsZoneIds.vault] }
}

output vaultName string = vault.outputs.name
output vaultResourceId string = vault.outputs.id
output workspaceId string = existingLogAnalyticsWorkspaceId
