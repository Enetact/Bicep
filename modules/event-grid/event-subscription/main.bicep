targetScope = 'resourceGroup'
param topicName string
param storageAccountId string
param queueName string
param deadLetterContainer string
resource topic 'Microsoft.EventGrid/topics@2025-02-15' existing = { name: topicName }
resource subscription 'Microsoft.EventGrid/topics/eventSubscriptions@2025-02-15' = {
  parent: topic
  name: 'document-received'
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: ['Document.Received']
      subjectBeginsWith: '/documents/'
      isSubjectCaseSensitive: true
    }
    deliveryWithResourceIdentity: {
      identity: { type: 'SystemAssigned' }
      destination: {
        endpointType: 'StorageQueue'
        properties: { resourceId: storageAccountId, queueName: queueName, queueMessageTimeToLiveInSeconds: 604800 }
      }
    }
    deadLetterWithResourceIdentity: {
      identity: { type: 'SystemAssigned' }
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: { resourceId: storageAccountId, blobContainerName: deadLetterContainer }
      }
    }
    retryPolicy: { maxDeliveryAttempts: 30, eventTimeToLiveInMinutes: 1440 }
  }
}
output id string = subscription.id
