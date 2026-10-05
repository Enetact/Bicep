targetScope = 'subscription'
param workloadResourceGroupName string
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
param kubernetesVersion string
param nodeSubnetId string
param privateDnsZoneId string
param controlPlaneIdentityId string
param kubeletIdentityId string
param kubeletClientId string
param kubeletObjectId string
param registryResourceId string
param adminGroupObjectIds string[]
param podCidr string
param serviceCidr string
param dnsServiceIP string
@allowed(['Standard_D4ds_v5', 'Standard_D8ds_v5'])
param nodeVmSize string = 'Standard_D4ds_v5'
@minValue(3)
@maxValue(10)
param maxUserNodes int = 6
param applicationNamespace string = 'platform-apps'
param serviceAccountName string = 'platform-app'

resource group 'Microsoft.Resources/resourceGroups@2025-04-01' = { name: workloadResourceGroupName, location: location, tags: union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter }) }
module composition './main.bicep' = {
 name: 'workload'
 scope: group
 params: {
  workload: workload
  environmentName: environmentName
  location: location
  owner: owner
  costCenter: costCenter
  customTags: customTags
  existingLogAnalyticsWorkspaceId: existingLogAnalyticsWorkspaceId
  deploymentPrincipalObjectId: deploymentPrincipalObjectId
  releaseActivated: releaseActivated
  kubernetesVersion: kubernetesVersion
  nodeSubnetId: nodeSubnetId
  privateDnsZoneId: privateDnsZoneId
  controlPlaneIdentityId: controlPlaneIdentityId
  kubeletIdentityId: kubeletIdentityId
  kubeletClientId: kubeletClientId
  kubeletObjectId: kubeletObjectId
  registryResourceId: registryResourceId
  adminGroupObjectIds: adminGroupObjectIds
  podCidr: podCidr
  serviceCidr: serviceCidr
  dnsServiceIP: dnsServiceIP
  nodeVmSize: nodeVmSize
  maxUserNodes: maxUserNodes
  applicationNamespace: applicationNamespace
  serviceAccountName: serviceAccountName
 }
}
output clusterResourceId string = composition.outputs.clusterResourceId
output clusterName string = composition.outputs.clusterName
output privateFqdn string = composition.outputs.privateFqdn
output oidcIssuerUrl string = composition.outputs.oidcIssuerUrl
output applicationIdentityId string = composition.outputs.applicationIdentityId
output applicationClientId string = composition.outputs.applicationClientId
output registryResourceId string = composition.outputs.registryResourceId
output workspaceId string = composition.outputs.workspaceId
