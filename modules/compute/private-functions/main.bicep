param name string
param location string
param tags object
param identityId string
param identityClientId string
param integrationSubnetId string
param storageName string
param packageBlobName string
param workspaceId string
@allowed(['http', 'worker'])
param mode string
param busName string = ''
param apiClientId string = ''
param allowedClientApplications string[] = []
resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
 name: '${name}-plan'
 location: location
 tags: tags
 kind: 'linux'
 sku: { name: 'B1', tier: 'Basic', capacity: 1 }
 properties: { reserved: true }
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
  siteConfig: {
   linuxFxVersion: 'DOTNET-ISOLATED|10.0'
   alwaysOn: true
   minTlsVersion: '1.2'
   scmMinTlsVersion: '1.2'
   ftpsState: 'Disabled'
   remoteDebuggingEnabled: false
   vnetRouteAllEnabled: true
   appSettings: [
    { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
    { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
    { name: 'AzureWebJobsStorage__accountName', value: storageName }
    { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
    { name: 'AzureWebJobsStorage__clientId', value: identityClientId }
    { name: 'AZURE_CLIENT_ID', value: identityClientId }
    { name: 'WEBSITE_RUN_FROM_PACKAGE', value: 'https://${storageName}.blob.${environment().suffixes.storage}/packages/${packageBlobName}' }
    { name: 'WEBSITE_RUN_FROM_PACKAGE_BLOB_MI_RESOURCE_ID', value: identityId }
    { name: 'AzureWebJobs.Health.Disabled', value: string(mode != 'http') }
    { name: 'AzureWebJobs.ProcessWork.Disabled', value: string(mode != 'worker') }
    { name: 'Bus__fullyQualifiedNamespace', value: '${busName}.servicebus.windows.net' }
    { name: 'Bus__credential', value: 'managedidentity' }
    { name: 'Bus__clientId', value: identityClientId }
    { name: 'Receipts__endpoint', value: 'https://${storageName}.blob.${environment().suffixes.storage}' }
   ]
  }
 }
}
resource auth 'Microsoft.Web/sites/config@2024-11-01' = if (mode == 'http') {
 parent: app
 name: 'authsettingsV2'
 properties: {
  platform: { enabled: true }
  globalValidation: { requireAuthentication: true, unauthenticatedClientAction: 'Return401' }
  identityProviders: { azureActiveDirectory: { enabled: true, registration: { clientId: apiClientId, openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0' }, validation: { allowedAudiences: ['api://${apiClientId}', apiClientId], jwtClaimChecks: { allowedClientApplications: allowedClientApplications } } } }
  httpSettings: { requireHttps: true }
 }
}
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = { parent: app, name: 'ftp', properties: { allow: false } }
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = { parent: app, name: 'scm', properties: { allow: false } }
resource logs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
 name: 'runtime'
 scope: app
 properties: { workspaceId: workspaceId, logs: [{ category: 'FunctionAppLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
}
output id string = app.id
output name string = app.name
