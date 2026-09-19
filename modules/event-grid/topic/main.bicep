targetScope = 'resourceGroup'
param name string
param location string
param tags object
resource topic 'Microsoft.EventGrid/topics@2025-02-15' = {
  name: name
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    inputSchema: 'EventGridSchema'
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
    minimumTlsVersionAllowed: '1.2'
  }
}
output id string = topic.id
output principalId string = topic.identity.principalId
output endpoint string = topic.properties.endpoint
