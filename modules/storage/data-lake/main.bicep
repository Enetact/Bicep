targetScope = 'resourceGroup'
param name string
param location string
param tags object
@allowed(['Standard_LRS', 'Standard_ZRS'])
param skuName string
param workspaceId string
resource account 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: skuName }
  properties: {
    isHnsEnabled: true
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    allowCrossTenantReplication: false
    networkAcls: { bypass: 'None', defaultAction: 'Deny' }
    encryption: { keySource: 'Microsoft.Storage', requireInfrastructureEncryption: true, services: { blob: { enabled: true, keyType: 'Account' } } }
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: account
  name: 'default'
  properties: {
    // Blob versioning is incompatible with hierarchical namespace.
    deleteRetentionPolicy: { enabled: true, days: 14 }
    containerDeleteRetentionPolicy: { enabled: true, days: 14 }
  }
}
resource filesystems 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = [for name in ['landing', 'curated']: {
  parent: blobs
  name: name
  properties: { publicAccess: 'None' }
}]
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'lake-audit'
  scope: blobs
  properties: { workspaceId: workspaceId, logAnalyticsDestinationType: 'Dedicated', logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'Transaction', enabled: true }] }
}
output id string = account.id
output name string = account.name
