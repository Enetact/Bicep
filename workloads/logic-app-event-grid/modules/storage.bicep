targetScope = 'resourceGroup'
param name string
param location string
param tags object
param runtime bool
param workspaceId string
resource account 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: runtime
    publicNetworkAccess: runtime ? 'Disabled' : 'Enabled'
    networkAcls: { defaultAction: 'Deny', bypass: runtime ? 'None' : 'AzureServices' }
    allowCrossTenantReplication: false
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: { deleteRetentionPolicy: { enabled: true, days: 14 }, isVersioningEnabled: true }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = [for container in (runtime ? ['packages'] : ['receipts', 'quarantine', 'deadletter']): {
  parent: blobs
  name: container
  properties: { publicAccess: 'None' }
}]
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: {}
}
resource queue 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' = if (!runtime) {
  parent: queues
  name: 'events'
  properties: {}
}
resource files 'Microsoft.Storage/storageAccounts/fileServices@2025-01-01' = if (runtime) {
  parent: account
  name: 'default'
  properties: {}
}
resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-01-01' = if (runtime) {
  name: '${name}/default/workflows'
  properties: { shareQuota: 100 }
  dependsOn: [files]
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'queue-audit'
  scope: queues
  properties: {
    workspaceId: workspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}
output id string = account.id

resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'blob-audit'
  scope: blobs
  properties: { workspaceId: workspaceId, logAnalyticsDestinationType: 'Dedicated', logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'Transaction', enabled: true }] }
}
