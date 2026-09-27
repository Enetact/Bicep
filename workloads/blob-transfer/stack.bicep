// Workload lifecycle wrapper. Shared references stay outside resource ownership.
targetScope = 'subscription'

metadata name = 'Blob transfer deployment stack'
metadata description = 'Subscription entry point owning one dedicated workload resource group and its resource-group composition.'

@description('Short lowercase alphanumeric workload code, 3-10 characters.')
@minLength(3)
@maxLength(10)
param workload string = 'blobcopy'

@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string

param location string
param owner string
@description('Reviewed source-owned custom tags. Required platform tags take precedence.')
param customTags object = {}
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

@description('First deploy false, upload a built package, then deploy true. Stack orchestration prevents Foundation after Release.')
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
@description('Optional organization-region-instance suffix; leave empty to preserve existing resource names.')
@maxLength(14)
param namingSuffix string = ''
@allowed(['new', 'existing'])
param networkMode string = 'new'
@description('Existing delegated integration subnet, separate private endpoint subnet, and blob/queue/table/dfs/web private DNS zone IDs. Platform-managed; never modified here.')
param existingNetwork object = {}
param vnetAddressPrefix string = ''
param integrationSubnetPrefix string = ''
param privateEndpointSubnetPrefix string = ''
@description('Existing pipeline service principal object ID; optional scoped smoke/data-plane roles. Does not grant deployment or role-assignment authority.')
param deploymentPrincipalObjectId string = ''
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
@description('Platform-owned Log Analytics workspace ID, optionally in another subscription. Empty creates an isolated workload workspace. Shared workspace settings and RBAC remain platform-owned.')
param existingLogAnalyticsWorkspaceId string = ''
@description('Enable the three log alert rules. Production requires alerts; disabling keeps rule resources but stops their evaluation.')
param enableLogAlerts bool = true

@description('Optional existing Entra security group object IDs; groups are governed outside this deployment.')
param uploaderGroupObjectId string = ''
param operatorGroupObjectId string = ''
@description('Existing CI service principal object ID. Grants package-container upload only, not deployment rights.')
param packagePublisherObjectId string = ''
@description('Existing Azure Monitor action group resource IDs. Configure approved notification receivers separately.')
param alertActionGroupIds array = []

param workloadResourceGroupName string

resource workloadGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: workloadResourceGroupName
  location: location
  tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, managedBy: 'Bicep' })
}

module composition './main.bicep' = {
  name: 'workload-composition'
  scope: workloadGroup
  params: {
    workload: workload
    environmentName: environmentName
    location: location
    owner: owner
    costCenter: costCenter
    customTags: customTags
    dataClassification: dataClassification
    destinationSubscriptionId: destinationSubscriptionId
    destinationResourceGroupName: destinationResourceGroupName
    destinationStorageAccountName: destinationStorageAccountName
    destinationContainerName: destinationContainerName
    destinationIsHnsEnabled: destinationIsHnsEnabled
    createDestinationPrivateEndpoints: createDestinationPrivateEndpoints
    deployFunctionApp: deployFunctionApp
    packageBlobName: packageBlobName
    uploadContainerName: uploadContainerName
    deploymentContainerName: deploymentContainerName
    transferQueueName: transferQueueName
    ledgerContainerName: ledgerContainerName
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
    recoveryOperatorGroupObjectId: recoveryOperatorGroupObjectId
    namingSuffix: namingSuffix
    networkMode: networkMode
    existingNetwork: existingNetwork
    vnetAddressPrefix: vnetAddressPrefix
    integrationSubnetPrefix: integrationSubnetPrefix
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
    deploymentPrincipalObjectId: deploymentPrincipalObjectId
    planSku: planSku
    instanceCount: instanceCount
    zoneRedundant: zoneRedundant
    storageSku: storageSku
    logRetentionDays: logRetentionDays
    logDailyCapGb: logDailyCapGb
    existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
    enableLogAlerts: enableLogAlerts
    uploaderGroupObjectId: uploaderGroupObjectId
    operatorGroupObjectId: operatorGroupObjectId
    packagePublisherObjectId: packagePublisherObjectId
    alertActionGroupIds: alertActionGroupIds
  }
}

output hostStorageAccountName string = composition.outputs.hostStorageAccountName
output uploadStorageAccountName string = composition.outputs.uploadStorageAccountName
output uploadContainer string = composition.outputs.uploadContainer
output packageContainer string = composition.outputs.packageContainer
output packageUrl string = composition.outputs.packageUrl
output functionAppName string = composition.outputs.functionAppName
output functionAppResourceId string = composition.outputs.functionAppResourceId
output managedIdentityPrincipalId string = composition.outputs.managedIdentityPrincipalId
output managedIdentityClientId string = composition.outputs.managedIdentityClientId
output virtualNetworkId string = composition.outputs.virtualNetworkId
output resourceNameStem string = composition.outputs.resourceNameStem
output workspaceId string = composition.outputs.workspaceId
output hostStorageAccountId string = composition.outputs.hostStorageAccountId
output uploadStorageAccountId string = composition.outputs.uploadStorageAccountId
output managedIdentityResourceId string = composition.outputs.managedIdentityResourceId
output applicationInsightsId string = composition.outputs.applicationInsightsId
output storagePrivateEndpointIds array = composition.outputs.storagePrivateEndpointIds
output destinationPrivateEndpointIds array = composition.outputs.destinationPrivateEndpointIds
output functionPrivateEndpointId string = composition.outputs.functionPrivateEndpointId
output ledgerStorageAccountName string = composition.outputs.ledgerStorageAccountName
output ledgerContainer string = composition.outputs.ledgerContainer
output transferQueue string = composition.outputs.transferQueue
