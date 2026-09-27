using '../main.bicep'
param workload = 'httpapi'
param environmentName = 'prod'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param existingLogAnalyticsWorkspaceId = ''
param deploymentPrincipalObjectId = ''
param privateEndpointSubnetId = ''
param privateDnsZoneIds = {}
param integrationSubnetId = ''
param apiClientId = ''
param allowedClientApplications = []
