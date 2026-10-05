targetScope = 'resourceGroup'
import { ResourceNames } from './naming.bicep'
@description('Concrete standard-v2 name candidates; null preserves legacy names. Effective settings govern conditional resources.')
param resourceNames ResourceNames?
@minLength(3)
@maxLength(10)
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
param customTags object = {}
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
param privateEndpointSubnetId string
param privateDnsZoneIds object
param databricksHostSubnetId string
param databricksContainerSubnetId string
@description('Existing approved regional browser-auth endpoint. Referenced/validated by pipeline; never owned by this stack.')
param browserAuthenticationPrivateEndpointId string
param sparkVersion string
@allowed(['Standard_D4ds_v5', 'Standard_D8ds_v5'])
param nodeType string = 'Standard_D4ds_v5'
@minValue(1)
@maxValue(4)
param maxWorkers int = 2
param notebookPath string
param inputFileName string = 'sample.csv'
@minValue(0)
param databricksJobId int = 0
var stem = '${workload}-${environmentName}'
var tags = union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId })
var suffix = take(uniqueString(subscription().id, resourceGroup().id), 6)
var storageName = resourceNames.?storage ?? 'st${workload}${environmentName}${suffix}'
var databricksName = resourceNames.?databricks ?? 'dbw-${stem}-${suffix}'
var factoryName = resourceNames.?factory ?? 'adf-${stem}-${suffix}'
var managedGroupName = resourceNames.?managedGroup ?? '${resourceGroup().name}-databricks-managed'
var rootStorageName = resourceNames.?rootStorage ?? 'dbfs${workload}${environmentName}${suffix}'
var connectorName = resourceNames.?accessConnector ?? 'dbw-${stem}-${suffix}-access'
module lake '../../modules/storage/data-lake/main.bicep' = {
  name: 'lake'
  params: { name: storageName, location: location, tags: tags, skuName: environmentName == 'prod' ? 'Standard_ZRS' : 'Standard_LRS', workspaceId: existingLogAnalyticsWorkspaceId }
}
module databricks '../../modules/analytics/databricks-workspace/main.bicep' = {
  name: 'databricks'
  params: {
    name: databricksName
    accessConnectorName: connectorName
    rootAccessConnectorName: resourceNames.?rootAccessConnector ?? 'dbw-${stem}-${suffix}-root-access'
    location: location
    tags: tags
    managedResourceGroupName: managedGroupName
    rootStorageAccountName: rootStorageName
    virtualNetworkId: substring(databricksHostSubnetId, 0, lastIndexOf(databricksHostSubnetId, '/subnets/'))
    hostSubnetName: last(split(databricksHostSubnetId, '/'))
    containerSubnetName: last(split(databricksContainerSubnetId, '/'))
    workspaceId: existingLogAnalyticsWorkspaceId
  }
}
module factory '../../modules/analytics/data-factory/main.bicep' = {
  name: 'factory'
  params: {
    name: factoryName
    location: location
    tags: tags
    workspaceId: existingLogAnalyticsWorkspaceId
    databricksResourceId: databricks.outputs.id
    databricksUrl: databricks.outputs.url
    lakeResourceId: lake.outputs.id
    lakeName: lake.outputs.name
    sparkVersion: sparkVersion
    nodeType: nodeType
    maxWorkers: maxWorkers
    notebookPath: notebookPath
    inputFileName: inputFileName
    databricksJobId: databricksJobId
  }
}
resource account 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = { parent: account, name: 'default' }
resource landing 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = { parent: blobs, name: 'landing' }
resource curated 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = { parent: blobs, name: 'curated' }
var blobContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource factoryLanding 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(landing.id, factoryName, blobContributor)
  scope: landing
  properties: { principalId: factory.outputs.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}
resource connectorLanding 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(landing.id, connectorName, blobContributor)
  scope: landing
  properties: { principalId: databricks.outputs.accessConnectorPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
  dependsOn: [lake]
}
resource connectorCurated 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(curated.id, connectorName, blobContributor)
  scope: curated
  properties: { principalId: databricks.outputs.accessConnectorPrincipalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
  dependsOn: [lake]
}
// Databricks workspace membership and notebook/compute/UC grants are data-plane onboarding.
// Do not grant ADF Azure Contributor just to implicitly make it a workspace administrator.
var destinations = [
  { key: 'root-blob', id: resourceId(managedGroupName, 'Microsoft.Storage/storageAccounts', rootStorageName), group: 'blob' }
  { key: 'root-dfs', id: resourceId(managedGroupName, 'Microsoft.Storage/storageAccounts', rootStorageName), group: 'dfs' }
  { key: 'blob', id: resourceId('Microsoft.Storage/storageAccounts', storageName), group: 'blob' }
  { key: 'dfs', id: resourceId('Microsoft.Storage/storageAccounts', storageName), group: 'dfs' }
  { key: 'databricks', id: resourceId('Microsoft.Databricks/workspaces', databricksName), group: 'databricks_ui_api' }
  { key: 'datafactory', id: resourceId('Microsoft.DataFactory/factories', factoryName), group: 'dataFactory' }
  { key: 'adfportal', id: resourceId('Microsoft.DataFactory/factories', factoryName), group: 'portal' }
]
module endpoints '../../modules/network/private-endpoint/main.bicep' = [for item in destinations: {
  name: 'pe-${item.key}'
  dependsOn: [lake, databricks, factory]
  params: { name: resourceNames == null ? 'pe-${stem}-${item.key}' : resourceNames![replace(item.key, 'root-', 'root') == item.key ? '${item.key}Endpoint' : (item.key == 'root-blob' ? 'rootBlobEndpoint' : 'rootDfsEndpoint')], location: location, tags: tags, subnetId: privateEndpointSubnetId, targetResourceId: item.id, groupIds: [item.group], privateDnsZoneIds: [privateDnsZoneIds[startsWith(item.key, 'root-') ? item.group : item.key]] }
}]
output storageAccountName string = lake.outputs.name
output storageResourceId string = lake.outputs.id
output databricksResourceId string = databricks.outputs.id
output databricksUrl string = databricks.outputs.url
output accessConnectorId string = databricks.outputs.accessConnectorId
output accessConnectorPrincipalId string = databricks.outputs.accessConnectorPrincipalId
output factoryResourceId string = factory.outputs.id
output factoryName string = factory.outputs.name
output factoryPrincipalId string = factory.outputs.principalId
output pipelineName string = factory.outputs.pipelineName
output browserAuthenticationPrivateEndpointId string = browserAuthenticationPrivateEndpointId
output workspaceId string = existingLogAnalyticsWorkspaceId
output stagingPipelineName string = factory.outputs.stagingPipelineName
output jobPipelineName string = factory.outputs.jobPipelineName
