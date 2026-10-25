@minLength(3)
@maxLength(24)
param storageAccountName string
param location string

@allowed(['Standard_LRS', 'Standard_ZRS'])
param skuName string
param tags object

resource account 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    defaultToOAuthAuthentication: true
  }
}

output storageAccountId string = account.id
