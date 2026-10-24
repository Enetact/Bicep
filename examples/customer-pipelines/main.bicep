targetScope = 'resourceGroup'

@description('Compilation example only; no deployment is performed by the example pipelines.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@allowed(['eastus2', 'westus2'])
param location string

@allowed(['dev'])
param environment string

@allowed(['Standard_LRS', 'Standard_ZRS'])
param storageSku string = 'Standard_LRS'

module storage './modules/storage-account.bicep' = {
  name: 'storage-example-${environment}'
  params: {
    storageAccountName: storageAccountName
    location: location
    skuName: storageSku
    tags: {
      environment: environment
      purpose: 'offline-validation-example'
    }
  }
}

output storageAccountId string = storage.outputs.storageAccountId
