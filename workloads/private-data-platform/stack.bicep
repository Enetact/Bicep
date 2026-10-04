targetScope = 'subscription'
param workloadResourceGroupName string
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

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter }) }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  workload: workload
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  customTags: customTags
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  releaseActivated: releaseActivated
  privateEndpointSubnetId: privateEndpointSubnetId
  privateDnsZoneIds: privateDnsZoneIds
  databricksHostSubnetId: databricksHostSubnetId
  databricksContainerSubnetId: databricksContainerSubnetId
  browserAuthenticationPrivateEndpointId: browserAuthenticationPrivateEndpointId
  sparkVersion: sparkVersion
  nodeType: nodeType
  maxWorkers: maxWorkers
  notebookPath: notebookPath
    inputFileName: inputFileName
    databricksJobId: databricksJobId
 }
}
output storageAccountName string = composition.outputs.storageAccountName
output storageResourceId string = composition.outputs.storageResourceId
output databricksResourceId string = composition.outputs.databricksResourceId
output databricksUrl string = composition.outputs.databricksUrl
output accessConnectorId string = composition.outputs.accessConnectorId
output accessConnectorPrincipalId string = composition.outputs.accessConnectorPrincipalId
output factoryResourceId string = composition.outputs.factoryResourceId
output factoryName string = composition.outputs.factoryName
output factoryPrincipalId string = composition.outputs.factoryPrincipalId
output pipelineName string = composition.outputs.pipelineName
output browserAuthenticationPrivateEndpointId string = composition.outputs.browserAuthenticationPrivateEndpointId
output workspaceId string = composition.outputs.workspaceId
output stagingPipelineName string = composition.outputs.stagingPipelineName
output jobPipelineName string = composition.outputs.jobPipelineName
