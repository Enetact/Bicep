using '../main.bicep'
param workload = 'eventflow'
param environmentName = 'dev'
param location = 'eastus2'
// Generated dev labels authorized by the repository operator; see the review record below.
param owner = 'enetact-dev'
param costCenter = 'dev-poc'
param integrationSubnetId = 'REPLACE_integrationSubnetId'
param privateEndpointSubnetId = 'REPLACE_privateEndpointSubnetId'
param existingLogAnalyticsWorkspaceId = 'REPLACE_existingLogAnalyticsWorkspaceId'
// Enterprise application/service principal object ID, not the application/client ID.
param deploymentPrincipalObjectId = '2b7a2791-e7d6-4181-9db3-1bee486236d0'
param privateDnsZoneIds = {
  blob: 'REPLACE_blob_ZONE'
  queue: 'REPLACE_queue_ZONE'
  table: 'REPLACE_table_ZONE'
  file: 'REPLACE_file_ZONE'
  sites: 'REPLACE_sites_ZONE'
  topic: 'REPLACE_topic_ZONE'
}
param trustedServiceException = {
  approved: true
  reviewReference: 'https://github.com/Enetact/Bicep/blob/main/docs/reviews/eventflow-dev-exceptions.md#trusted-service-delivery'
}
param runtimeStorageCredentialException = {
  approved: true
  reviewReference: 'https://github.com/Enetact/Bicep/blob/main/docs/reviews/eventflow-dev-exceptions.md#runtime-storage-credentials'
}
param hostingSku = 'WS1'
param alertActionGroupIds = []
