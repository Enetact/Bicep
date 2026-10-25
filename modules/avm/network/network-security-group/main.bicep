targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM network security group'
metadata description = 'New owned NSG with bounded explicit rules; no subnet adoption or role grants.'

@sealed()
type ruleProperties = {
  @minValue(100)
  @maxValue(4096)
  priority: int
  access: 'Allow' | 'Deny'
  direction: 'Inbound' | 'Outbound'
  protocol: 'Tcp' | 'Udp' | 'Icmp' | 'Esp' | 'Ah' | '*'
  @minLength(1)
  @maxLength(100)
  sourceAddressPrefix: string
  @minLength(1)
  @maxLength(11)
  sourcePortRange: string
  @minLength(1)
  @maxLength(100)
  destinationAddressPrefix: string
  @minLength(1)
  @maxLength(11)
  destinationPortRange: string
  @maxLength(140)
  description: string?
}

@sealed()
type securityRule = {
  @minLength(1)
  @maxLength(80)
  name: string
  properties: ruleProperties
}

@description('Explicit name for a new workload-owned NSG. Never pass an existing shared NSG name to adopt it.')
@minLength(1)
@maxLength(80)
param name string
@description('Approved Azure region.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags.')
param tags object
@description('Reviewed custom rules; empty retains Azure default NSG rules. Names and priorities must be unique; review effective traffic and address/port syntax.')
@maxLength(100)
param securityRules securityRule[] = []

module avm '../../../../vendor/azure-verified-modules/network-network-security-group/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    securityRules: securityRules
    flushConnection: false
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
