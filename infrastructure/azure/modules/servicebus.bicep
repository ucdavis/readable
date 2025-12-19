@description('Service Bus namespace name.')
param name string

@description('Azure region for the Service Bus namespace.')
param location string

@description('Tags to apply to Service Bus resources.')
param tags object

@description('Service Bus queue names.')
param queueNames array

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  tags: tags
}

resource queues 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for queueName in queueNames: {
  name: queueName
  parent: serviceBusNamespace
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enablePartitioning: true
  }
}]

resource appAuthRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = {
  name: 'app'
  parent: serviceBusNamespace
  properties: {
    rights: [
      'Listen'
      'Send'
    ]
  }
}

var appAuthRuleKeys = listKeys(appAuthRule.id, '2021-11-01')

output namespaceName string = serviceBusNamespace.name
output namespaceId string = serviceBusNamespace.id
output fullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
output queueNames array = queueNames
output queueIds array = [for queueName in queueNames: resourceId('Microsoft.ServiceBus/namespaces/queues', serviceBusNamespace.name, queueName)]
output authRuleId string = appAuthRule.id
@secure()
output connectionString string = appAuthRuleKeys.primaryConnectionString
