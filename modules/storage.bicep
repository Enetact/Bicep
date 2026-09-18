param name string
param location string
param tags object
@allowed(['Standard_LRS', 'Standard_ZRS'])
param skuName string
param containers array
param queueNames array = []
param workspaceId string

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: skuName }
  properties: {
    accessTier: 'Hot'
    isHnsEnabled: false // Standard Blob APIs and versioned immutable uploads.
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    allowCrossTenantReplication: false
    networkAcls: { bypass: 'None', defaultAction: 'Deny' }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: { enabled: true, keyType: 'Account' }
        file: { enabled: true, keyType: 'Account' }
      }
    }
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
  }
}
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: {}
}
resource workQueues 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' = [for queueName in queueNames: {
  parent: queues
  name: queueName
  properties: {}
}]
resource tables 'Microsoft.Storage/storageAccounts/tableServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: {}
}
resource blobContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = [for container in containers: {
  parent: blobs
  name: container
  properties: { publicAccess: 'None' }
}]

resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'storage-audit'
  scope: blobs
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}
resource queueDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'storage-audit'
  scope: queues
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}

output id string = account.id
output name string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
output queueEndpoint string = account.properties.primaryEndpoints.queue
output tableEndpoint string = account.properties.primaryEndpoints.table
