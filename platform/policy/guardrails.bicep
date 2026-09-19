// Separate platform lifecycle. Never called by the workload deployment.
targetScope = 'subscription'

param policyPrefix string = 'workload-platform'
param approvedRegions array
@allowed(['Audit', 'Deny'])
param effect string = 'Audit'

resource placement 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: '${policyPrefix}-placement-tags'
  properties: {
    policyType: 'Custom'
    mode: 'Indexed'
    displayName: 'Workload approved regions and ownership tags'
    metadata: { category: 'Workload platform' }
    policyRule: {
      if: {
        anyOf: [
          { allOf: [{ field: 'location', notIn: approvedRegions }, { field: 'location', notEquals: 'global' }] }
          { field: 'tags[owner]', exists: 'false' }
          { field: 'tags[costCenter]', exists: 'false' }
          { field: 'tags[environment]', exists: 'false' }
          { field: 'tags[owner]', equals: '' }
          { field: 'tags[costCenter]', equals: '' }
        ]
      }
      then: { effect: effect }
    }
  }
}
resource privateStorage 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: '${policyPrefix}-private-storage'
  properties: {
    policyType: 'Custom'
    mode: 'All'
    displayName: 'Storage uses private access, identity authorization and TLS 1.2'
    metadata: { category: 'Workload platform' }
    policyRule: {
      if: {
        allOf: [
          { field: 'type', equals: 'Microsoft.Storage/storageAccounts' }
          { anyOf: [
            { field: 'Microsoft.Storage/storageAccounts/publicNetworkAccess', notEquals: 'Disabled' }
            { field: 'Microsoft.Storage/storageAccounts/allowBlobPublicAccess', notEquals: false }
            { field: 'Microsoft.Storage/storageAccounts/allowSharedKeyAccess', notEquals: false }
            { field: 'Microsoft.Storage/storageAccounts/minimumTlsVersion', notEquals: 'TLS1_2' }
            { field: 'Microsoft.Storage/storageAccounts/supportsHttpsTrafficOnly', notEquals: true }
          ] }
        ]
      }
      then: { effect: effect }
    }
  }
}
// Definitions only. Assignment scopes, exemptions and transition to Deny require platform review.
output policyDefinitionIds array = [placement.id, privateStorage.id]
