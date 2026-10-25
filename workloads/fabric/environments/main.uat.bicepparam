using '../main.bicep'
param workload = 'fabric'
param environmentName = 'uat'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param existingLogAnalyticsWorkspaceId = ''
param deploymentPrincipalObjectId = ''
param capacitySku = 'F64'
param capacityAdministrators = ['REPLACE_CAPACITY_ADMIN_UPN']
param enableWorkspacePrivateLink = false
param existingFabricWorkspaceId = ''
param privateEndpointSubnetId = ''
param privateDnsZoneIds = {}
