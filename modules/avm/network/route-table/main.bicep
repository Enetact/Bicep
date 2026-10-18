targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM route table'
metadata description = 'New owned route table with explicit local, gateway, drop or appliance routes; no subnet writer.'

@sealed()
type routeProperties = {
  @minLength(1)
  @maxLength(100)
  addressPrefix: string
  nextHopType: 'VnetLocal' | 'VirtualNetworkGateway' | 'None'
}

@sealed()
type route = {
  @minLength(1)
  @maxLength(80)
  name: string
  properties: routeProperties
}

@sealed()
type applianceProperties = {
  @minLength(1)
  @maxLength(100)
  addressPrefix: string
  @minLength(7)
  @maxLength(45)
  nextHopIpAddress: string
}

@sealed()
type applianceRoute = {
  @minLength(1)
  @maxLength(80)
  name: string
  properties: applianceProperties
}

@description('Explicit name for a new workload-owned route table; existing shared tables are not adopted.')
@minLength(1)
@maxLength(80)
param name string
@description('Approved Azure region.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags.')
param tags object
@description('Explicit local, gateway or drop routes. Empty adds no routes. Names and address prefixes require network review.')
@maxLength(100)
param routes route[] = []
@description('Explicit appliance routes, each with a required reviewed next-hop IP. No appliance is created or discovered by this module.')
@maxLength(100)
param applianceRoutes applianceRoute[] = []
@description('Whether the reviewed routing profile disables BGP propagation. Default preserves propagation.')
param disableBgpRoutePropagation bool = false

var formattedApplianceRoutes = [for item in applianceRoutes: {
  name: item.name
  properties: {
    addressPrefix: item.properties.addressPrefix
    nextHopType: 'VirtualAppliance'
    nextHopIpAddress: item.properties.nextHopIpAddress
  }
}]

module avm '../../../../vendor/azure-verified-modules/network-route-table/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    routes: concat(routes, formattedApplianceRoutes)
    disableBgpRoutePropagation: disableBgpRoutePropagation
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
