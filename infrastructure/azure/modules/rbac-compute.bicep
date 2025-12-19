@description('Principal ID of the API app.')
param apiPrincipalId string

@description('Principal ID of the Function app.')
param functionPrincipalId string

@description('Storage account name.')
param storageAccountName string

@description('Service Bus namespace name.')
param serviceBusNamespaceName string

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2021-11-01' existing = {
  name: serviceBusNamespaceName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

var roleStorageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var roleStorageBlobDelegator = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a')
var roleServiceBusReceiver = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')

resource funcBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionPrincipalId, roleStorageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource funcServiceBusReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, functionPrincipalId, roleServiceBusReceiver)
  scope: serviceBusNamespace
  properties: {
    roleDefinitionId: roleServiceBusReceiver
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, apiPrincipalId, roleStorageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDataContributor
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiBlobDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, apiPrincipalId, roleStorageBlobDelegator)
  scope: storageAccount
  properties: {
    roleDefinitionId: roleStorageBlobDelegator
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}
