targetScope = 'resourceGroup'
import { ResourceNames } from './naming.bicep'

@description('Reviewed concrete standard-v2 Azure resource names; null preserves deterministic workload naming.')
param resourceNames ResourceNames?
@minLength(3)
@maxLength(10)
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
param customTags object = {}
@description('Existing platform monitoring reference retained in delivery evidence; pool capacity/agent observability requires separate qualification.')
param existingLogAnalyticsWorkspaceId string
param deploymentPrincipalObjectId string
param releaseActivated bool = false
param devCenterProjectResourceId string
param agentSubnetId string
param adoOrganizationUrl string
@minLength(3)
@maxLength(64)
param adoPoolName string
@minLength(1)
param adoProjectNames string[]
@minLength(1)
param poolAdministratorGroupEmails string[]
@description('Reviewed DevOpsInfrastructure service principal object ID whose platform network pregrants are verified. No grants are created by this workload.')
param devOpsInfrastructurePrincipalObjectId string
@minValue(1)
@maxValue(10)
param maximumConcurrency int = 2
@allowed(['Standard_D2ds_v5', 'Standard_D4ds_v5'])
param agentVmSize string = 'Standard_D2ds_v5'
param agentImageVersion string

var tags = union(customTags, {
  workload: workload
  environment: environmentName
  owner: owner
  costCenter: costCenter
  releaseActivated: string(releaseActivated)
  deploymentPrincipal: deploymentPrincipalObjectId
  devOpsInfrastructurePrincipal: devOpsInfrastructurePrincipalObjectId
})

module pool '../../modules/devops/managed-devops-pool/main.bicep' = {
  name: 'managed-devops-pool'
  params: {
    name: resourceNames.?pool ?? 'mdp-${workload}-${environmentName}-${take(uniqueString(subscription().id, resourceGroup().id), 6)}'
    location: location
    tags: tags
    devCenterProjectResourceId: devCenterProjectResourceId
    agentSubnetId: agentSubnetId
    adoOrganizationUrl: adoOrganizationUrl
    adoPoolName: adoPoolName
    adoProjectNames: adoProjectNames
    poolAdministratorGroupEmails: poolAdministratorGroupEmails
    maximumConcurrency: maximumConcurrency
    agentVmSize: agentVmSize
    agentImageVersion: agentImageVersion
  }
}

output managedDevOpsPoolId string = pool.outputs.managedDevOpsPoolId
output managedDevOpsPoolName string = pool.outputs.managedDevOpsPoolName
output agentPoolName string = pool.outputs.agentPoolName
output agentSubnetId string = pool.outputs.agentSubnetId
output devCenterProjectResourceId string = pool.outputs.devCenterProjectResourceId
output workspaceId string = existingLogAnalyticsWorkspaceId
