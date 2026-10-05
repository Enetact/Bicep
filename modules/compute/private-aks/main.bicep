targetScope = 'resourceGroup'
param name string
param nodeResourceGroupName string = '${resourceGroup().name}-aks-managed'
param applicationIdentityName string = 'id-${name}-app'
param location string
param tags object
param kubernetesVersion string
param nodeSubnetId string
param privateDnsZoneId string
param controlPlaneIdentityId string
param kubeletIdentityId string
param kubeletClientId string
param kubeletObjectId string
param adminGroupObjectIds string[]
param workspaceId string
param podCidr string
param serviceCidr string
param dnsServiceIP string
@allowed(['Standard_D4ds_v5', 'Standard_D8ds_v5'])
param nodeVmSize string
@minValue(3)
@maxValue(10)
param maxUserNodes int
param applicationNamespace string
param serviceAccountName string

resource cluster 'Microsoft.ContainerService/managedClusters@2026-02-01' = {
 name: name
 location: location
 tags: tags
 sku: { name: 'Base', tier: 'Standard' }
 identity: { type: 'UserAssigned', userAssignedIdentities: { '${controlPlaneIdentityId}': {} } }
 properties: {
  kubernetesVersion: kubernetesVersion
  dnsPrefix: name
  nodeResourceGroup: nodeResourceGroupName
  enableRBAC: true
  disableLocalAccounts: true
  aadProfile: { managed: true, enableAzureRBAC: true, tenantID: tenant().tenantId, adminGroupObjectIDs: adminGroupObjectIds }
  apiServerAccessProfile: { enablePrivateCluster: true, enablePrivateClusterPublicFQDN: false, privateDNSZone: privateDnsZoneId }
  identityProfile: { kubeletidentity: { resourceId: kubeletIdentityId, clientId: kubeletClientId, objectId: kubeletObjectId } }
  oidcIssuerProfile: { enabled: true }
  securityProfile: { workloadIdentity: { enabled: true }, defender: { logAnalyticsWorkspaceResourceId: workspaceId, securityMonitoring: { enabled: true } } }
  autoUpgradeProfile: { upgradeChannel: 'patch', nodeOSUpgradeChannel: 'NodeImage' }
  networkProfile: {
   networkPlugin: 'azure'
   networkPluginMode: 'overlay'
   networkDataplane: 'cilium'
   networkPolicy: 'cilium'
   loadBalancerSku: 'standard'
   outboundType: 'userAssignedNATGateway'
   podCidr: podCidr
   serviceCidr: serviceCidr
   dnsServiceIP: dnsServiceIP
  }
  addonProfiles: {
   azurepolicy: { enabled: true }
   azureKeyvaultSecretsProvider: { enabled: true, config: { enableSecretRotation: 'true', rotationPollInterval: '2m' } }
  }
  agentPoolProfiles: [
   {
    name: 'system'
    mode: 'System'
    type: 'VirtualMachineScaleSets'
    vmSize: nodeVmSize
    count: 3
    availabilityZones: ['1', '2', '3']
    osType: 'Linux'
    osSKU: 'AzureLinux3'
    osDiskType: 'Managed'
    osDiskSizeGB: 128
    vnetSubnetID: nodeSubnetId
    maxPods: 50
    enableNodePublicIP: false
    nodeTaints: ['CriticalAddonsOnly=true:NoSchedule']
    upgradeSettings: { maxSurge: '1', drainTimeoutInMinutes: 30 }
   }
   {
    name: 'apps'
    mode: 'User'
    type: 'VirtualMachineScaleSets'
    vmSize: nodeVmSize
    count: 3
    enableAutoScaling: true
    minCount: 3
    maxCount: maxUserNodes
    availabilityZones: ['1', '2', '3']
    osType: 'Linux'
    osSKU: 'AzureLinux3'
    osDiskType: 'Managed'
    osDiskSizeGB: 128
    vnetSubnetID: nodeSubnetId
    maxPods: 50
    enableNodePublicIP: false
    upgradeSettings: { maxSurge: '1', drainTimeoutInMinutes: 30 }
   }
  ]
 }
}
resource applicationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
 name: applicationIdentityName
 location: location
 tags: tags
}
resource federation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
 parent: applicationIdentity
 name: 'kubernetes'
 properties: {
  issuer: cluster.properties.oidcIssuerProfile.issuerURL
  subject: 'system:serviceaccount:${applicationNamespace}:${serviceAccountName}'
  audiences: ['api://AzureADTokenExchange']
 }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
 name: 'platform'
 scope: cluster
 properties: { workspaceId: workspaceId, logs: [{ categoryGroup: 'allLogs', enabled: true }], metrics: [{ category: 'AllMetrics', enabled: true }] }
}
output clusterResourceId string = cluster.id
output clusterName string = cluster.name
output privateFqdn string = cluster.properties.privateFQDN
output oidcIssuerUrl string = cluster.properties.oidcIssuerProfile.issuerURL
output applicationIdentityId string = applicationIdentity.id
output applicationClientId string = applicationIdentity.properties.clientId
