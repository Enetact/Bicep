using '../main.bicep'
param workload = 'dataplat'
param environmentName = 'prod'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param existingLogAnalyticsWorkspaceId = ''
param deploymentPrincipalObjectId = ''
param privateEndpointSubnetId = ''
param privateDnsZoneIds = {}
param databricksHostSubnetId = ''
param databricksContainerSubnetId = ''
param browserAuthenticationPrivateEndpointId = ''
param sparkVersion = 'REPLACE_QUALIFIED_RUNTIME'
param nodeType = 'Standard_D4ds_v5'
param maxWorkers = 2
param notebookPath = '/Shared/platform-studio/acceptance'

// Review and bind an existing published job; zero keeps its ADF launcher absent.
param databricksJobId = 0
param inputFileName = 'sample.csv'
