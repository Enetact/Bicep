targetScope = 'resourceGroup'

@description('Short lowercase alphanumeric workload code, 3-10 characters.')
@minLength(3)
@maxLength(10)
param workload string = 'blobcopy'

@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string

param location string = resourceGroup().location
param owner string
param costCenter string
param dataClassification string = 'Confidential'

@description('Existing destination account and container; same Entra tenant, optionally another subscription.')
param destinationSubscriptionId string = subscription().subscriptionId
param destinationResourceGroupName string
@minLength(3)
@maxLength(24)
param destinationStorageAccountName string
param destinationContainerName string
param destinationIsHnsEnabled bool = true
param createDestinationPrivateEndpoints bool = true

@description('First deploy false, upload a built package, then deploy true. Incremental deployments only.')
param deployFunctionApp bool = false
@description('Immutable release blob name, e.g. releases/<commit>.zip. No SAS or query string.')
param packageBlobName string = 'releases/initial.zip'

param uploadContainerName string = 'incoming'
param deploymentContainerName string = 'packages'
param transferQueueName string = 'transfer-work'
param ledgerContainerName string = 'transfer-ledger'
@description('Server-controlled source-name prefix to opaque deduplication scope map; longest match wins. Empty prefix covers the whole container.')
param sourceScopePrefixes object = { '': 'default' }
param recoveryIncludeSourceVersions bool = true
@minValue(1)
@maxValue(100)
param recoveryMaxAttempts int = 10
@minValue(1)
@maxValue(1440)
param recoveryRetryMinutes int = 15
@minValue(1)
@maxValue(5000)
param recoveryScanPageSize int = 100
@minValue(1)
@maxValue(100)
param recoveryScanPagesPerRun int = 5
@minValue(1)
@maxValue(8760)
param recoveryVerifyAfterHours int = 24
param recoverySchedule string = '0 */5 * * * *'
param poisonMonitorSchedule string = '0 */5 * * * *'
@minValue(1)
@maxValue(1073741824)
param maxCopyBytes int = 1073741824
@description('Existing trusted recovery operators; grants ledger mutation plus work-queue send. Does not grant destination writes.')
param recoveryOperatorGroupObjectId string = ''
param vnetAddressPrefix string
param integrationSubnetPrefix string
param privateEndpointSubnetPrefix string
@allowed(['B1', 'S1', 'P1v3'])
param planSku string = 'P1v3'
@minValue(1)
param instanceCount int = 2
param zoneRedundant bool = false
@allowed(['Standard_LRS', 'Standard_ZRS'])
param storageSku string = 'Standard_ZRS'
@minValue(30)
@maxValue(730)
param logRetentionDays int = 90
@minValue(1)
param logDailyCapGb int = 5

@description('Optional existing Entra security group object IDs; groups are governed outside this deployment.')
param uploaderGroupObjectId string = ''
param operatorGroupObjectId string = ''
@description('Existing CI service principal object ID. Grants package-container upload only, not deployment rights.')
param packagePublisherObjectId string = ''
@description('Existing Azure Monitor action group resource IDs. Configure approved notification receivers separately.')
param alertActionGroupIds array = []

var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id, workload, environmentName)
var stem = '${workload}-${environmentName}'
var tags = {
  workload: workload
  environment: environmentName
  owner: owner
  costCenter: costCenter
  dataClassification: dataClassification
  managedBy: 'Bicep'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${stem}-${suffix}'
  location: location
  tags: tags
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring-${environmentName}'
  params: {
    name: stem
    location: location
    tags: tags
    retentionDays: logRetentionDays
    dailyCapGb: logDailyCapGb
    principalId: identity.properties.principalId
    alertActionGroupIds: alertActionGroupIds
    operatorGroupObjectId: operatorGroupObjectId
    enableRuntimeAlerts: deployFunctionApp
  }
}

module network './modules/network.bicep' = {
  name: 'network-${environmentName}'
  params: {
    name: stem
    location: location
    tags: tags
    addressPrefix: vnetAddressPrefix
    integrationSubnetPrefix: integrationSubnetPrefix
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
  }
}

module hostStorage './modules/storage.bicep' = {
  name: 'host-storage-${environmentName}'
  params: {
    name: 'sth${environmentName}${suffix}'
    location: location
    tags: tags
    skuName: storageSku
    containers: [deploymentContainerName]
    workspaceId: monitoring.outputs.workspaceId
  }
}

module uploadStorage './modules/storage.bicep' = {
  name: 'upload-storage-${environmentName}'
  params: {
    name: 'stu${environmentName}${suffix}'
    location: location
    tags: tags
    skuName: storageSku
    containers: [uploadContainerName, ledgerContainerName]
    queueNames: [transferQueueName, '${transferQueueName}-poison', 'webjobs-blobtrigger-poison']
    workspaceId: monitoring.outputs.workspaceId
  }
}

module storageAccess './modules/storage-access.bicep' = {
  name: 'storage-access-${environmentName}'
  params: {
    hostAccountName: hostStorage.outputs.name
    ledgerContainerName: ledgerContainerName
    transferQueueName: transferQueueName
    recoveryOperatorGroupObjectId: recoveryOperatorGroupObjectId
    uploadAccountName: uploadStorage.outputs.name
    uploadContainerName: uploadContainerName
    deploymentContainerName: deploymentContainerName
    principalId: identity.properties.principalId
    uploaderGroupObjectId: uploaderGroupObjectId
    packagePublisherObjectId: packagePublisherObjectId
  }
}

module destination './modules/destination-access.bicep' = {
  name: 'destination-access-${environmentName}-${suffix}'
  scope: resourceGroup(destinationSubscriptionId, destinationResourceGroupName)
  params: {
    accountName: destinationStorageAccountName
    containerName: destinationContainerName
    principalId: identity.properties.principalId
  }
}

var storageEndpoints = [
  { name: 'host-blob', account: 'host', groupId: 'blob' }
  { name: 'host-queue', account: 'host', groupId: 'queue' }
  { name: 'host-table', account: 'host', groupId: 'table' }
  { name: 'upload-blob', account: 'upload', groupId: 'blob' }
  { name: 'upload-queue', account: 'upload', groupId: 'queue' }
]

module storagePrivateEndpoints './modules/private-endpoint.bicep' = [for item in storageEndpoints: {
  name: 'pe-${item.name}-${environmentName}'
  params: {
    name: 'pe-${stem}-${item.name}'
    location: location
    tags: tags
    subnetId: network.outputs.privateEndpointSubnetId
    privateLinkResourceId: item.account == 'host' ? hostStorage.outputs.id : uploadStorage.outputs.id
    groupId: item.groupId
    privateDnsZoneId: item.groupId == 'blob' ? network.outputs.blobZoneId : (item.groupId == 'queue' ? network.outputs.queueZoneId : network.outputs.tableZoneId)
  }
}]

module destinationBlobEndpoint './modules/private-endpoint.bicep' = if (createDestinationPrivateEndpoints) {
  name: 'pe-destination-blob-${environmentName}'
  params: {
    name: 'pe-${stem}-destination-blob'
    location: location
    tags: tags
    subnetId: network.outputs.privateEndpointSubnetId
    privateLinkResourceId: destination.outputs.accountId
    groupId: 'blob'
    privateDnsZoneId: network.outputs.blobZoneId
  }
}

module destinationDfsEndpoint './modules/private-endpoint.bicep' = if (createDestinationPrivateEndpoints && destinationIsHnsEnabled) {
  name: 'pe-destination-dfs-${environmentName}'
  params: {
    name: 'pe-${stem}-destination-dfs'
    location: location
    tags: tags
    subnetId: network.outputs.privateEndpointSubnetId
    privateLinkResourceId: destination.outputs.accountId
    groupId: 'dfs'
    privateDnsZoneId: network.outputs.dfsZoneId
  }
}

module app './modules/function-app.bicep' = if (deployFunctionApp) {
  name: 'function-app-${environmentName}'
  params: {
    name: 'func-${stem}-${suffix}'
    planName: 'asp-${stem}'
    location: location
    tags: tags
    planSku: planSku
    instanceCount: instanceCount
    zoneRedundant: zoneRedundant
    identityId: identity.id
    identityClientId: identity.properties.clientId
    integrationSubnetId: network.outputs.integrationSubnetId
    hostBlobEndpoint: hostStorage.outputs.blobEndpoint
    hostQueueEndpoint: hostStorage.outputs.queueEndpoint
    hostTableEndpoint: hostStorage.outputs.tableEndpoint
    uploadBlobEndpoint: uploadStorage.outputs.blobEndpoint
    uploadQueueEndpoint: uploadStorage.outputs.queueEndpoint
    ledgerContainerName: ledgerContainerName
    transferQueueName: transferQueueName
    sourceScopePrefixes: sourceScopePrefixes
    recoveryIncludeSourceVersions: recoveryIncludeSourceVersions
    recoveryMaxAttempts: recoveryMaxAttempts
    recoveryRetryMinutes: recoveryRetryMinutes
    recoveryScanPageSize: recoveryScanPageSize
    recoveryScanPagesPerRun: recoveryScanPagesPerRun
    recoveryVerifyAfterHours: recoveryVerifyAfterHours
    recoverySchedule: recoverySchedule
    poisonMonitorSchedule: poisonMonitorSchedule
    maxCopyBytes: maxCopyBytes
    uploadContainerName: uploadContainerName
    destinationBlobEndpoint: destination.outputs.blobEndpoint
    destinationContainerName: destinationContainerName
    packageUrl: '${hostStorage.outputs.blobEndpoint}${deploymentContainerName}/${packageBlobName}'
    applicationInsightsConnectionString: monitoring.outputs.connectionString
    workspaceId: monitoring.outputs.workspaceId
    operatorGroupObjectId: operatorGroupObjectId
  }
  dependsOn: [storageAccess, storagePrivateEndpoints, destinationBlobEndpoint, destinationDfsEndpoint]
}

module appPrivateEndpoint './modules/private-endpoint.bicep' = if (deployFunctionApp) {
  name: 'pe-function-${environmentName}'
  params: {
    name: 'pe-${stem}-function'
    location: location
    tags: tags
    subnetId: network.outputs.privateEndpointSubnetId
    privateLinkResourceId: app!.outputs.id
    groupId: 'sites'
    privateDnsZoneId: network.outputs.webZoneId
  }
}

output hostStorageAccountName string = hostStorage.outputs.name
output uploadStorageAccountName string = uploadStorage.outputs.name
output uploadContainer string = uploadContainerName
output packageContainer string = deploymentContainerName
output packageUrl string = '${hostStorage.outputs.blobEndpoint}${deploymentContainerName}/${packageBlobName}'
output functionAppName string = 'func-${stem}-${suffix}'
output functionAppResourceId string = resourceId('Microsoft.Web/sites', 'func-${stem}-${suffix}')
output managedIdentityPrincipalId string = identity.properties.principalId
output managedIdentityClientId string = identity.properties.clientId
output virtualNetworkId string = network.outputs.vnetId
output workspaceId string = monitoring.outputs.workspaceId


// Compatibility output: the ledger shares the solution storage account.
output ledgerStorageAccountName string = uploadStorage.outputs.name
output ledgerContainer string = ledgerContainerName
output transferQueue string = transferQueueName
