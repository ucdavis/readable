@description('Principal ID of the Event Grid system topic.')
param eventGridPrincipalId string

@description('Service Bus namespace name.')
param serviceBusNamespaceName string

@description('Storage account name.')
param storageAccountName string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2021-11-01' existing = {
  name: serviceBusNamespaceName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

var roleServiceBusSender = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var roleStorageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource eventGridToServiceBus 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, eventGridPrincipalId, roleServiceBusSender)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: roleServiceBusSender
    principalId: eventGridPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource eventGridToDeadletter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, eventGridPrincipalId, roleStorageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: eventGridPrincipalId
    principalType: 'ServicePrincipal'
  }
}
