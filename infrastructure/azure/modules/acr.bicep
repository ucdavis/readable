@description('Azure Container Registry name.')
param name string

@description('Azure region for the registry.')
param location string

@description('Tags to apply to the registry.')
param tags object

@description('Container Registry SKU.')
param sku string = 'Basic'

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  tags: tags
  properties: {
    adminUserEnabled: true
  }
}

var credentials = registry.listCredentials()

output name string = registry.name
output id string = registry.id
output loginServer string = registry.properties.loginServer
output username string = credentials.username
@secure()
output password string = credentials.passwords[0].value

