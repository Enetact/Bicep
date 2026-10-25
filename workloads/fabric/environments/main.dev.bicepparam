using '../main.bicep'
param workload = 'fabric'
param environmentName = 'dev'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param existingLogAnalyticsWorkspaceId = ''
param deploymentPrincipalObjectId = ''
// F64 incurs ongoing cost. Qualify region, quota, licensing and budgets before enablement.
param capacitySku = 'F64'
param capacityAdministrators = ['REPLACE_CAPACITY_ADMIN_UPN']
// Re-run the protected ARM flow with the observed workspace UUID after Fabric bootstrap.
param enableWorkspacePrivateLink = false
param existingFabricWorkspaceId = ''
param privateEndpointSubnetId = ''
param privateDnsZoneIds = {}
