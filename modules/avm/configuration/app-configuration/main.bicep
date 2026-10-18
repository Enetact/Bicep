targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM private App Configuration'
metadata description = 'Entra-only private store with non-secret key-value children, optional replicas and pass-through ARM authorization.'

@sealed()
type keyValue = {
  @minLength(1)
  @maxLength(512)
  name: string
  @maxLength(8192)
  value: string
  contentType: 'text/plain' | 'application/json'
}

@sealed()
type replica = {
  @minLength(1)
  @maxLength(50)
  name: string
  @minLength(1)
  @maxLength(50)
  replicaLocation: string
}

@description('Explicit globally unique store name, 5-50 characters; qualify Azure naming rules and availability.')
@minLength(5)
@maxLength(50)
param name string
@description('Approved Azure region.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags.')
param tags object
@description('Reviewed billable service tier; Free and Developer are excluded from this private protected profile.')
@allowed(['Standard', 'Premium'])
param skuName string = 'Standard'
@description('Protected soft-delete retention in days; purge protection remains enabled.')
@minValue(1)
@maxValue(7)
param softDeleteRetentionInDays int = 7
@description('Non-secret configuration only, with explicit content types. Empty writes no configuration data. Values are visible in source/deployment history; never supply credentials. Requires reviewed data-plane RBAC and an ARM Private Link deployment path.')
@maxLength(100)
param keyValues keyValue[] = []
@description('Optional explicit replicas; empty creates none. Review tier/region availability, uniqueness and cost.')
@maxLength(10)
param replicaLocations replica[] = []

module avm '../../../../vendor/azure-verified-modules/configuration-app-configuration/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    sku: skuName
    disableLocalAuth: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    softDeleteRetentionInDays: softDeleteRetentionInDays
    dataPlaneProxy: {
      authenticationMode: 'Pass-through'
      privateLinkDelegation: 'Enabled'
    }
    keyValues: [for item in keyValues: {
      name: item.name
      value: item.value
      contentType: item.contentType
      tags: tags
    }]
    replicaLocations: replicaLocations
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
output endpoint string = avm.outputs.endpoint
