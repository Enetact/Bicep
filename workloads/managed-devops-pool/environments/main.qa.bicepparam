using '../main.bicep'
param workload = 'devopspool'
param environmentName = 'qa'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param existingLogAnalyticsWorkspaceId = ''
param deploymentPrincipalObjectId = ''
// Reference approved platform foundations. Never use the new pool as its bootstrap runner.
param devCenterProjectResourceId = ''
param agentSubnetId = ''
param adoOrganizationUrl = 'https://dev.azure.com/REPLACE_ORGANIZATION'
param adoPoolName = 'fabric-qa-agents'
param adoProjectNames = ['REPLACE_PROJECT_NAME']
param poolAdministratorGroupEmails = ['REPLACE_ADMIN_GROUP_EMAIL']
param devOpsInfrastructurePrincipalObjectId = ''
param maximumConcurrency = 2
param agentVmSize = 'Standard_D2ds_v5'
// Resolve a published Windows2022 Gen2 image version; rolling latest is forbidden.
param agentImageVersion = 'REPLACE_PINNED_IMAGE_VERSION'
