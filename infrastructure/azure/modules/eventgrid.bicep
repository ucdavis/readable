@description('Event Grid system topic name.')
param name string

@description('Azure region for Event Grid.')
param location string

@description('Tags to apply to Event Grid resources.')
param tags object

@description('Resource ID of the Storage Account to source events from.')
param storageAccountId string

@description('User-assigned managed identity resource ID for Event Grid delivery.')
param deliveryIdentityResourceId string

resource systemTopic 'Microsoft.EventGrid/systemTopics@2025-02-15' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${deliveryIdentityResourceId}': {}
    }
  }
  tags: tags
  properties: {
    source: storageAccountId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

output topicName string = systemTopic.name
output topicId string = systemTopic.id
output principalId string = systemTopic.identity.principalId
