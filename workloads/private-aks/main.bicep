targetScope = 'resourceGroup'
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

var tags = union(customTags, { workload: workload, environment: environmentName, owner: owner, costCenter: costCenter, releaseActivated: string(releaseActivated), deploymentPrincipal: deploymentPrincipalObjectId })
module aks '../../modules/compute/private-aks/main.bicep' = {
 name: 'aks'
 params: {
  name: 'aks-${workload}-${environmentName}'
  location: location
  tags: tags
  workspaceId: existingLogAnalyticsWorkspaceId
  kubernetesVersion: kubernetesVersion
  nodeSubnetId: nodeSubnetId
  privateDnsZoneId: privateDnsZoneId
  controlPlaneIdentityId: controlPlaneIdentityId
  kubeletIdentityId: kubeletIdentityId
  kubeletClientId: kubeletClientId
  kubeletObjectId: kubeletObjectId
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
output clusterResourceId string = aks.outputs.clusterResourceId
output clusterName string = aks.outputs.clusterName
output privateFqdn string = aks.outputs.privateFqdn
output oidcIssuerUrl string = aks.outputs.oidcIssuerUrl
output applicationIdentityId string = aks.outputs.applicationIdentityId
output applicationClientId string = aks.outputs.applicationClientId
output registryResourceId string = registryResourceId
output workspaceId string = existingLogAnalyticsWorkspaceId
