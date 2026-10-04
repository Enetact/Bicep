targetScope = 'resourceGroup'
param name string
param location string
param tags object
param workspaceId string
param databricksResourceId string
param databricksUrl string
param lakeResourceId string
param lakeName string
@description('Platform-qualified Databricks runtime key; validate availability before operating the sample.')
param sparkVersion string
param nodeType string
@minValue(1)
@maxValue(4)
param maxWorkers int
@description('Reviewed workspace notebook path. Notebook import and Unity Catalog grants are separate data-plane onboarding.')
param notebookPath string
@description('Reviewed single CSV filename in landing/inbox. No arbitrary runtime URL or path.')
param inputFileName string = 'sample.csv'
@minValue(0)
@description('Existing reviewed Databricks Job ID. Zero omits the job-launch pipeline until workspace onboarding is complete.')
param databricksJobId int = 0
resource factory 'Microsoft.DataFactory/factories@2018-06-01' = {
  name: name
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: { publicNetworkAccess: 'Disabled' }
}
resource network 'Microsoft.DataFactory/factories/managedVirtualNetworks@2018-06-01' = {
  parent: factory
  name: 'default'
  properties: {}
}
resource runtime 'Microsoft.DataFactory/factories/integrationRuntimes@2018-06-01' = {
  parent: factory
  name: 'private-azure-ir'
  properties: {
    type: 'Managed'
    managedVirtualNetwork: { type: 'ManagedVirtualNetworkReference', referenceName: network.name }
    typeProperties: { computeProperties: { location: location } }
  }
}
var destinations = [
  { name: 'databricks', id: databricksResourceId, group: 'databricks_ui_api' }
  { name: 'lake-dfs', id: lakeResourceId, group: 'dfs' }
  { name: 'lake-blob', id: lakeResourceId, group: 'blob' }
]
resource managedEndpoints 'Microsoft.DataFactory/factories/managedVirtualNetworks/managedPrivateEndpoints@2018-06-01' = [for dest in destinations: {
  parent: network
  name: dest.name
  properties: { privateLinkResourceId: dest.id, groupId: dest.group }
}]
resource databricksLink 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = {
  parent: factory
  name: 'databricks-managed-identity'
  properties: {
    type: 'AzureDatabricks'
    connectVia: { type: 'IntegrationRuntimeReference', referenceName: runtime.name }
    typeProperties: {
      domain: 'https://${databricksUrl}'
      authentication: 'MSI'
      workspaceResourceId: databricksResourceId
      newClusterVersion: sparkVersion
      newClusterNodeType: nodeType
      newClusterNumOfWorker: maxWorkers == 1 ? '1' : '1:${maxWorkers}'
      dataSecurityMode: 'USER_ISOLATION'
    }
  }
}
resource lakeLink 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = {
  parent: factory
  name: 'lake-managed-identity'
  properties: {
    type: 'AzureBlobFS'
    connectVia: { type: 'IntegrationRuntimeReference', referenceName: runtime.name }
    typeProperties: { url: 'https://${lakeName}.dfs.${environment().suffixes.storage}' }
  }
}
resource pipeline 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: factory
  name: 'run-reviewed-notebook'
  properties: {
    description: 'Manual only. Complete notebook, Unity Catalog, identity and endpoint approvals before invocation. No schedule or automatic run is created.'
    concurrency: 1
    activities: [{
      name: 'RunReviewedNotebook'
      type: 'DatabricksNotebook'
      linkedServiceName: { type: 'LinkedServiceReference', referenceName: databricksLink.name }
      policy: { timeout: '0.01:00:00', retry: 0, secureInput: true, secureOutput: true }
      typeProperties: { notebookPath: notebookPath }
    }]
  }
}
resource sourceDataset 'Microsoft.DataFactory/factories/datasets@2018-06-01' = {
  parent: factory
  name: 'reviewed-landing-file'
  properties: {
    type: 'Binary'
    linkedServiceName: { type: 'LinkedServiceReference', referenceName: lakeLink.name }
    typeProperties: { location: { type: 'AzureBlobFSLocation', fileSystem: 'landing', folderPath: 'inbox', fileName: inputFileName } }
  }
}
resource stagedDataset 'Microsoft.DataFactory/factories/datasets@2018-06-01' = {
  parent: factory
  name: 'isolated-staged-file'
  properties: {
    type: 'Binary'
    parameters: { runId: { type: 'String' } }
    linkedServiceName: { type: 'LinkedServiceReference', referenceName: lakeLink.name }
    typeProperties: { location: { type: 'AzureBlobFSLocation', fileSystem: 'landing', folderPath: { type: 'Expression', value: '@concat(\'staged/\', dataset().runId)' }, fileName: 'batch.csv' } }
  }
}
var copyActivity = {
  name: 'StageReviewedFile'
  type: 'Copy'
  policy: { timeout: '0.00:30:00', retry: 0, secureInput: true, secureOutput: true }
  inputs: [{ type: 'DatasetReference', referenceName: sourceDataset.name }]
  outputs: [{ type: 'DatasetReference', referenceName: stagedDataset.name, parameters: { runId: { type: 'Expression', value: '@pipeline().RunId' } } }]
  typeProperties: {
    source: { type: 'BinarySource', storeSettings: { type: 'AzureBlobFSReadSettings', recursive: false, deleteFilesAfterCompletion: false }, formatSettings: { type: 'BinaryReadSettings' } }
    sink: { type: 'BinarySink', storeSettings: { type: 'AzureBlobFSWriteSettings', copyBehavior: 'PreserveHierarchy' } }
    enableStaging: false
    parallelCopies: 1
    validateDataConsistency: true
  }
}
resource stagePipeline 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: factory
  name: 'stage-reviewed-file'
  properties: { description: 'Manual: copy one approved landing/inbox file into an isolated staged/RunId path. No source deletion or Databricks compute.', concurrency: 1, activities: [copyActivity] }
}
resource jobLink 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = if (databricksJobId > 0) {
  parent: factory
  name: 'databricks-jobs-managed-identity'
  properties: {
    type: 'AzureDatabricks'
    connectVia: { type: 'IntegrationRuntimeReference', referenceName: runtime.name }
    typeProperties: { domain: 'https://${databricksUrl}', authentication: 'MSI', workspaceResourceId: databricksResourceId }
  }
}
resource jobPipeline 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = if (databricksJobId > 0) {
  parent: factory
  name: 'stage-and-run-reviewed-job'
  properties: {
    description: 'Manual: stage the reviewed file then invoke an existing reviewed Databricks Job. Job owns validation, Delta write and verification. No schedules or automatic retries.'
    concurrency: 1
    activities: [copyActivity, {
      name: 'RunReviewedJob'
      type: 'DatabricksJob'
      dependsOn: [{ activity: 'StageReviewedFile', dependencyConditions: ['Succeeded'] }]
      linkedServiceName: { type: 'LinkedServiceReference', referenceName: jobLink!.name }
      policy: { timeout: '0.01:15:00', retry: 0, secureInput: true, secureOutput: true }
      typeProperties: {
        jobId: string(databricksJobId)
        jobParameters: {
          batch_id: { type: 'Expression', value: '@pipeline().RunId' }
          source_relative_path: { type: 'Expression', value: '@concat(\'staged/\', pipeline().RunId, \'/batch.csv\')' }
        }
      }
    }]
  }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'factory-audit'
  scope: factory
  properties: { workspaceId: workspaceId, logAnalyticsDestinationType: 'Dedicated', logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
}
output id string = factory.id
output name string = factory.name
output principalId string = factory.identity.principalId
output pipelineName string = pipeline.name
output stagingPipelineName string = stagePipeline.name
output jobPipelineName string = databricksJobId > 0 ? jobPipeline!.name : ''
