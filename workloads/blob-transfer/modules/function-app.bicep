param name string
param planName string
param location string
param tags object
param planSku string
param instanceCount int
param zoneRedundant bool
param identityId string
param identityClientId string
param integrationSubnetId string
param hostBlobEndpoint string
param hostQueueEndpoint string
param hostTableEndpoint string
param uploadBlobEndpoint string
param uploadQueueEndpoint string
param uploadContainerName string
param ledgerContainerName string
param transferQueueName string
param sourceScopePrefixes object
param recoveryIncludeSourceVersions bool
param recoveryMaxAttempts int
param recoveryRetryMinutes int
param recoveryScanPageSize int
param recoveryScanPagesPerRun int
param recoveryVerifyAfterHours int
param recoverySchedule string
param poisonMonitorSchedule string
param maxCopyBytes int
param destinationBlobEndpoint string
param destinationContainerName string
param packageUrl string
@secure()
param applicationInsightsConnectionString string
param workspaceId string
param operatorGroupObjectId string

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
    tier: planSku == 'B1' ? 'Basic' : (planSku == 'S1' ? 'Standard' : 'PremiumV3')
    capacity: instanceCount
  }
  properties: { reserved: true, zoneRedundant: zoneRedundant }
}

resource app 'Microsoft.Web/sites@2024-11-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: integrationSubnetId
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      remoteDebuggingEnabled: false
      vnetRouteAllEnabled: true
      ipSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: hostBlobEndpoint }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: hostQueueEndpoint }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: hostTableEndpoint }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identityClientId }
        { name: 'UploadStorage__blobServiceUri', value: uploadBlobEndpoint }
        { name: 'UploadStorage__queueServiceUri', value: uploadQueueEndpoint }
        { name: 'UploadStorage__credential', value: 'managedidentity' }
        { name: 'UploadStorage__clientId', value: identityClientId }
        { name: 'TransferQueueStorage__queueServiceUri', value: uploadQueueEndpoint }
        { name: 'TransferQueueStorage__credential', value: 'managedidentity' }
        { name: 'TransferQueueStorage__clientId', value: identityClientId }
        { name: 'UploadContainer', value: uploadContainerName }
        { name: 'Destination__blobServiceUri', value: destinationBlobEndpoint }
        { name: 'Destination__container', value: destinationContainerName }
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: packageUrl }
        { name: 'WEBSITE_RUN_FROM_PACKAGE_BLOB_MI_RESOURCE_ID', value: identityId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'ClientId=${identityClientId};Authorization=AAD' }
        { name: 'Copy__maxBytes', value: string(maxCopyBytes) }
        { name: 'Copy__scopePrefixes', value: string(sourceScopePrefixes) }
        { name: 'Recovery__includeSourceVersions', value: string(recoveryIncludeSourceVersions) }
        { name: 'Ledger__blobServiceUri', value: uploadBlobEndpoint }
        { name: 'Ledger__container', value: ledgerContainerName }
        { name: 'TransferQueue', value: transferQueueName }
        { name: 'Recovery__maxAttempts', value: string(recoveryMaxAttempts) }
        { name: 'Recovery__retryMinutes', value: string(recoveryRetryMinutes) }
        { name: 'Recovery__scanPageSize', value: string(recoveryScanPageSize) }
        { name: 'Recovery__scanPagesPerRun', value: string(recoveryScanPagesPerRun) }
        { name: 'Recovery__verifyAfterHours', value: string(recoveryVerifyAfterHours) }
        { name: 'RecoverySchedule', value: recoverySchedule }
        { name: 'PoisonMonitorSchedule', value: poisonMonitorSchedule }
      ]
    }
  }
}
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'function-logs'
  scope: app
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ category: 'FunctionAppLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}
resource operators 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorGroupObjectId)) {
  name: guid(app.id, operatorGroupObjectId, 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  scope: app
  properties: {
    principalId: operatorGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  }
}
output id string = app.id
