targetScope = 'resourceGroup'
param name string
param location string
param tags object
param runtimeStorageName string
param eventStorageName string
param topicId string
param integrationSubnetId string
param instrumentationConnectionString string
param releaseActivated bool
@allowed(['WS1', 'WS2', 'WS3'])
param skuName string = 'WS1'
resource runtime 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: runtimeStorageName }
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${name}'
  location: location
  tags: tags
  kind: 'elastic'
  sku: { name: skuName, tier: 'WorkflowStandard', capacity: 1 }
  properties: { reserved: false }
}
resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,workflowapp'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: integrationSubnetId
    vnetContentShareEnabled: true
    siteConfig: {
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      vnetRouteAllEnabled: true
      use32BitWorkerProcess: false
      appSettings: [
        { name: 'APP_KIND', value: 'workflowApp' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }
        { name: 'WEBSITE_NODE_DEFAULT_VERSION', value: '~22' }
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${runtime.name};AccountKey=${runtime.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}' }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: 'DefaultEndpointsProtocol=https;AccountName=${runtime.name};AccountKey=${runtime.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}' }
        { name: 'WEBSITE_CONTENTSHARE', value: 'workflows' }
        { name: 'WEBSITE_CONTENTOVERVNET', value: '1' }
        { name: 'WEBSITE_CONTENTSHARE_SKIPVALIDATION', value: '1' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: instrumentationConnectionString }
        { name: 'EVENT_STORAGE_NAME', value: eventStorageName }
        { name: 'EXPECTED_TOPIC', value: topicId }
        { name: 'Workflows.process-event.FlowState', value: releaseActivated ? 'Enabled' : 'Disabled' }
      ]
    }
  }
}
resource scm 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'scm'
  properties: { allow: false }
}
resource ftp 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: app
  name: 'ftp'
  properties: { allow: false }
}
output id string = app.id
output principalId string = app.identity.principalId
