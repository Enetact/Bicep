targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM user-assigned identity'
metadata description = 'Pinned AVM identity with optional explicit federation; no role grants or credentials.'

@sealed()
type federation = {
  @minLength(3)
  @maxLength(120)
  name: string
  @minLength(9)
  @maxLength(2048)
  issuer: string
  @minLength(1)
  @maxLength(600)
  subject: string
  @minLength(1)
  @maxLength(1)
  audiences: ('api://AzureADTokenExchange')[]
}

@description('Explicit reviewed identity name; no naming policy is inferred.')
@minLength(3)
@maxLength(128)
param name string
@description('Approved Azure region.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags; no secrets.')
param tags object
@description('Explicit trusted issuers and subjects. Empty creates no federated identity credentials. Review every trust before deployment.')
@maxLength(20)
param federatedIdentityCredentials federation[] = []

module avm '../../../../vendor/azure-verified-modules/identity-user-assigned-identity/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    federatedIdentityCredentials: federatedIdentityCredentials
    isolationScope: 'Regional'
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
output principalId string = avm.outputs.principalId
output clientId string = avm.outputs.clientId
