using '../main.bicep'

param environmentName = 'uat'
param workload = 'blobcopy'
// Longest source prefix wins. No uploader metadata required. Empty prefix covers all names.
param sourceScopePrefixes = { '': 'default' }
param recoveryIncludeSourceVersions = true
param transferQueueName = 'transfer-work'
param ledgerContainerName = 'transfer-ledger'
param recoveryMaxAttempts = 10
param recoveryRetryMinutes = 15
param recoveryScanPageSize = 100
param recoveryScanPagesPerRun = 5
param recoveryVerifyAfterHours = 24
param recoverySchedule = '0 */5 * * * *'
param poisonMonitorSchedule = '0 */5 * * * *'
param location = 'eastus2'
param owner = 'REPLACE_WITH_TEAM'
param costCenter = 'REPLACE_WITH_COST_CENTER'
param destinationSubscriptionId = '00000000-0000-0000-0000-000000000000'
param destinationResourceGroupName = 'REPLACE_WITH_DESTINATION_RG'
param destinationStorageAccountName = 'datalakestorage'
param destinationContainerName = 'blobcopy-uat'
param destinationIsHnsEnabled = true
param vnetAddressPrefix = '10.42.0.0/16'
param integrationSubnetPrefix = '10.42.0.0/26'
param privateEndpointSubnetPrefix = '10.42.1.0/26'
param planSku = 'P1v3'
param instanceCount = 2
param storageSku = 'Standard_ZRS'
param logRetentionDays = 90
param logDailyCapGb = 3
