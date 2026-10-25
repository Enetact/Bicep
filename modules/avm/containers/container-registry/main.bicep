targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM private container registry'
metadata description = 'Premium registry with Entra authentication, explicit retention and optional geo-replicas; no admin credentials, tasks or webhooks.'

@sealed()
type replica = {
  @minLength(1)
  @maxLength(50)
  name: string
  @minLength(1)
  @maxLength(50)
  location: string
  zoneRedundancy: 'Disabled' | 'Enabled'
}

@description('Explicit globally unique registry name, 5-50 alphanumeric characters; validate availability before qualification.')
@minLength(5)
@maxLength(50)
param name string
@description('Approved Azure region with Premium registry support.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags.')
param tags object
@description('Zone redundancy in a supported region; availability and cost require review.')
@allowed(['Disabled', 'Enabled'])
param zoneRedundancy string = 'Disabled'
@description('Untagged-manifest retention in days. After this period untagged manifests can be deleted; review the application retention requirement.')
@minValue(7)
@maxValue(90)
param retentionInDays int = 30
@description('Optional reviewed geo-replicas, with explicit names, regions and zone profiles. Empty creates no replicas.')
@maxLength(10)
param replications replica[] = []

module avm '../../../../vendor/azure-verified-modules/containers-container-registry/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    acrSku: 'Premium'
    acrAdminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Disabled'
    networkRuleBypassOptions: 'None'
    networkRuleSetDefaultAction: 'Deny'
    networkRuleBypassAllowedForTasks: false
    networkRuleSetIpRules: []
    azureADAuthenticationAsArmPolicyStatus: 'disabled'
    exportPolicyStatus: 'disabled'
    retentionPolicyStatus: 'enabled'
    retentionPolicyDays: retentionInDays
    zoneRedundancy: zoneRedundancy
    replications: [for item in replications: {
      name: item.name
      location: item.location
      tags: tags
      regionEndpointEnabled: true
      zoneRedundancy: item.zoneRedundancy
    }]
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
output loginServer string = avm.outputs.loginServer
