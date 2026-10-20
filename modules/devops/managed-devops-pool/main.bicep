targetScope = 'resourceGroup'

metadata name = 'Private Managed DevOps Pool'
metadata description = 'Stateless Microsoft-managed agents injected into one approved existing subnet, scoped to explicit Azure DevOps projects and administrator groups.'

@minLength(3)
@maxLength(44)
@description('Globally unique Azure pool resource name. The Azure DevOps organization alias has a separate namespace.')
param name string
param location string
param tags object = {}
@description('Existing same-region Dev Center project; referenced without adopting its lifecycle.')
param devCenterProjectResourceId string
@description('Existing exclusive subnet delegated to Microsoft.DevOpsInfrastructure/pools. Platform pregrants and egress/DNS qualification are external prerequisites.')
param agentSubnetId string
@description('Azure DevOps organization URL in the same Microsoft Entra tenant, e.g. https://dev.azure.com/<organization>. No credentials.')
param adoOrganizationUrl string
@description('Explicit organization pool alias used by consumer pipeline pool.name; independent from this Azure resource name.')
@minLength(3)
@maxLength(64)
param adoPoolName string
@minLength(1)
@description('Explicit Azure DevOps project names. Access for all projects/pipelines is not granted.')
param adoProjectNames string[]
@minLength(1)
@description('Reviewed administrator security-group email addresses accepted by Azure DevOps SpecificAccounts; not Entra object IDs.')
param poolAdministratorGroupEmails string[]
@minValue(1)
@maxValue(10)
param maximumConcurrency int = 2
@allowed(['Standard_D2ds_v5', 'Standard_D4ds_v5'])
param agentVmSize string = 'Standard_D2ds_v5'
@minLength(1)
@description('Observed approved immutable version of windows-2022-g2, e.g. YYYYMMDD.N.N. Resolve a published version before enabling the target; latest is not accepted by the workflow.')
param agentImageVersion string

// Latest stable API verified against Bicep MCP and Microsoft Learn. No preview-only features.
// https://learn.microsoft.com/azure/templates/microsoft.devopsinfrastructure/2026-06-02/pools
resource pool 'Microsoft.DevOpsInfrastructure/pools@2026-06-02' = {
  name: name
  location: location
  tags: tags
  // Job Azure access uses an explicitly authorized WIF service connection.
  // The pool/agent receives no ambient Azure managed-identity privileges.
  identity: { type: 'None' }
  properties: {
    devCenterProjectResourceId: devCenterProjectResourceId
    maximumConcurrency: maximumConcurrency
    agentProfile: {
      kind: 'Stateless'
    }
    fabricProfile: {
      kind: 'Vmss'
      sku: { name: agentVmSize }
      images: [
        {
          wellKnownImageName: 'windows-2022-g2/${agentImageVersion}'
          aliases: ['platform-windows']
        }
      ]
      networkProfile: { subnetId: agentSubnetId }
      osProfile: { logonType: 'Service' }
      storageProfile: {
        osDiskStorageAccountType: 'StandardSSD'
        dataDisks: []
      }
    }
    organizationProfile: {
      kind: 'AzureDevOps'
      organizations: [
        {
          url: adoOrganizationUrl
          alias: adoPoolName
          projects: adoProjectNames
          openAccess: false
          parallelism: maximumConcurrency
        }
      ]
      permissionProfile: {
        kind: 'SpecificAccounts'
        groups: poolAdministratorGroupEmails
        users: []
      }
    }
  }
}

output managedDevOpsPoolId string = pool.id
output managedDevOpsPoolName string = pool.name
output agentPoolName string = adoPoolName
output agentSubnetId string = agentSubnetId
output devCenterProjectResourceId string = devCenterProjectResourceId
