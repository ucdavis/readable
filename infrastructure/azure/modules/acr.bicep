@description('Azure Container Registry name.')
param name string

@description('Azure region for the registry.')
param location string

@description('Tags to apply to the registry.')
param tags object

@description('Container Registry SKU.')
param sku string = 'Basic'

@description('Optional principal ID to grant AcrPull on this registry.')
param acrPullPrincipalId string = ''

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  tags: tags
  properties: {
    adminUserEnabled: false
  }
}

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrPullPrincipalId)) {
  name: guid(registry.id, acrPullPrincipalId, 'AcrPull')
  scope: registry
  properties: {
    principalId: acrPullPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

output name string = registry.name
output id string = registry.id
output loginServer string = registry.properties.loginServer
