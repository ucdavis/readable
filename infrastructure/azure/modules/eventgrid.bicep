@description('Event Grid system topic name.')
param name string

@description('Azure region for Event Grid.')
param location string

@description('Tags to apply to Event Grid resources.')
param tags object

@description('Resource ID of the Storage Account to source events from.')
param storageAccountId string

@description('Resource IDs of the Service Bus queues.')
param serviceBusQueueIds array

@description('Deadletter container name in the storage account.')
param deadLetterContainerName string

@description('User-assigned managed identity resource ID for Event Grid delivery.')
param deliveryIdentityResourceId string

resource systemTopic 'Microsoft.EventGrid/systemTopics@2025-02-15' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    source: storageAccountId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

resource defaultSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-02-15' = {
  name: 'on-incoming-pdf'
  parent: systemTopic
  properties: {
    eventDeliverySchema: 'CloudEventSchemaV1_0'
    filter: {
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
      subjectBeginsWith: '/blobServices/default/containers/incoming/blobs/'
      subjectEndsWith: '.pdf'
      isSubjectCaseSensitive: false
    }
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
    deliveryWithResourceIdentity: {
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: deliveryIdentityResourceId
      }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: {
          resourceId: serviceBusQueueIds[0]
        }
      }
    }
    deadLetterWithResourceIdentity: {
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: deliveryIdentityResourceId
      }
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: storageAccountId
          blobContainerName: deadLetterContainerName
        }
      }
    }
  }
}

output topicName string = systemTopic.name
output topicId string = systemTopic.id
output principalId string = systemTopic.identity.principalId
