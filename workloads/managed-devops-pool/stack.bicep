targetScope = 'subscription'
import { ResourceNames } from './naming.bicep'

param resourceNames ResourceNames?
param workloadResourceGroupName string
@minLength(3)
@maxLength(10)
param workload string
@allowed(['dev', 'qa', 'uat', 'prod'])
param environmentName string
param location string
param owner string
param costCenter string
param customTags object = {}
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
param devOpsInfrastructurePrincipalObjectId string
@minValue(1)
@maxValue(10)
param maximumConcurrency int = 2
@allowed(['Standard_D2ds_v5', 'Standard_D4ds_v5'])
param agentVmSize string = 'Standard_D2ds_v5'
param agentImageVersion string

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: workloadResourceGroupName
  location: location
  tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter })
}

module composition './main.bicep' = {
  name: 'workload'
  scope: group
  params: {
    resourceNames: resourceNames
    workload: workload
    environmentName: environmentName
    location: location
    owner: owner
    costCenter: costCenter
    customTags: customTags
    existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
    deploymentPrincipalObjectId: deploymentPrincipalObjectId
    releaseActivated: releaseActivated
    devCenterProjectResourceId: devCenterProjectResourceId
    agentSubnetId: agentSubnetId
    adoOrganizationUrl: adoOrganizationUrl
    adoPoolName: adoPoolName
    adoProjectNames: adoProjectNames
    poolAdministratorGroupEmails: poolAdministratorGroupEmails
    devOpsInfrastructurePrincipalObjectId: devOpsInfrastructurePrincipalObjectId
    maximumConcurrency: maximumConcurrency
    agentVmSize: agentVmSize
    agentImageVersion: agentImageVersion
  }
}

output managedDevOpsPoolId string = composition.outputs.managedDevOpsPoolId
output managedDevOpsPoolName string = composition.outputs.managedDevOpsPoolName
output agentPoolName string = composition.outputs.agentPoolName
output agentSubnetId string = composition.outputs.agentSubnetId
output devCenterProjectResourceId string = composition.outputs.devCenterProjectResourceId
output workspaceId string = composition.outputs.workspaceId
