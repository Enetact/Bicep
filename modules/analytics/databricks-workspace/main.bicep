targetScope = 'resourceGroup'
param name string
param accessConnectorName string = '${name}-access'
param rootAccessConnectorName string = '${name}-root-access'
param location string
param tags object
param managedResourceGroupName string
param rootStorageAccountName string
param virtualNetworkId string
param hostSubnetName string
param containerSubnetName string
param workspaceId string

resource connector 'Microsoft.Databricks/accessConnectors@2023-05-01' = {
  name: accessConnectorName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {}
}
resource rootConnector 'Microsoft.Databricks/accessConnectors@2023-05-01' = {
  name: rootAccessConnectorName
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {}
}
resource databricks 'Microsoft.Databricks/workspaces@2026-01-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'premium' }
  properties: {
    computeMode: 'Hybrid'
    defaultStorageFirewall: 'Enabled'
    accessConnector: { id: rootConnector.id, identityType: 'SystemAssigned' }
    managedResourceGroupId: subscriptionResourceId('Microsoft.Resources/resourceGroups', managedResourceGroupName)
    publicNetworkAccess: 'Disabled'
    requiredNsgRules: 'NoAzureDatabricksRules'
    parameters: {
      storageAccountName: { value: rootStorageAccountName }
      customVirtualNetworkId: { value: virtualNetworkId }
      customPublicSubnetName: { value: hostSubnetName }
      customPrivateSubnetName: { value: containerSubnetName }
      enableNoPublicIp: { value: true }
      requireInfrastructureEncryption: { value: true }
    }
  }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'workspace-audit'
  scope: databricks
  properties: {
    workspaceId: workspaceId
    logAnalyticsDestinationType: 'Dedicated'
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}
output id string = databricks.id
output name string = databricks.name
output url string = databricks.properties.workspaceUrl
output accessConnectorId string = connector.id
output accessConnectorPrincipalId string = connector.identity.principalId
