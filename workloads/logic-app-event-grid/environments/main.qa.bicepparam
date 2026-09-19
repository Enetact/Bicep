using '../main.bicep'
param workload = 'eventflow'
param environmentName = 'qa'
param location = 'eastus2'
param owner = 'REPLACE_OWNER'
param costCenter = 'REPLACE_COST_CENTER'
param integrationSubnetId = 'REPLACE_integrationSubnetId'
param privateEndpointSubnetId = 'REPLACE_privateEndpointSubnetId'
param existingLogAnalyticsWorkspaceId = 'REPLACE_existingLogAnalyticsWorkspaceId'
param deploymentPrincipalObjectId = 'REPLACE_deploymentPrincipalObjectId'
param privateDnsZoneIds = {
  blob: 'REPLACE_blob_ZONE'
  queue: 'REPLACE_queue_ZONE'
  table: 'REPLACE_table_ZONE'
  file: 'REPLACE_file_ZONE'
  sites: 'REPLACE_sites_ZONE'
  topic: 'REPLACE_topic_ZONE'
}
param trustedServiceException = { approved: false, reviewReference: '' }
param runtimeStorageCredentialException = { approved: false, reviewReference: '' }
param hostingSku = 'WS1'
param alertActionGroupIds = []
