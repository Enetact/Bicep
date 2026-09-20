param plan object
param location string
param tags object
module network '../../../modules/network/workload-vnet/main.bicep' = if (plan.createNetwork) {
  name: 'prerequisite-network'
  params: { name: plan.vnetName, location: location, tags: tags, addressPrefix: plan.vnetAddressPrefix, integrationSubnetPrefix: plan.integrationSubnetPrefix, privateEndpointSubnetPrefix: plan.privateEndpointSubnetPrefix }
}
module zones '../../../modules/network/private-dns-zone/main.bicep' = [for zoneName in plan.createDnsZoneNames: {
  name: 'prerequisite-dns-${uniqueString(zoneName)}'
  params: { name: zoneName, tags: tags, virtualNetworkId: plan.vnetId, linkName: plan.linkName }
  dependsOn: [network]
}]
module workspace '../../../modules/monitoring/log-analytics/main.bicep' = if (plan.createWorkspace) {
  name: 'prerequisite-monitoring'
  params: { name: plan.workspaceName, location: location, tags: tags }
}
