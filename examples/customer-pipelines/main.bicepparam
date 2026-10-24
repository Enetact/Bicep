using './main.bicep'

param storageAccountName = 'studiocompilesample01'
param location = readEnvironmentVariable('STUDIO_LOCATION', 'eastus2')
param environment = readEnvironmentVariable('STUDIO_ENVIRONMENT', 'dev')
param storageSku = 'Standard_LRS'
