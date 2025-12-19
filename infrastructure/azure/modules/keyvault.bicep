@description('Key Vault name.')
param name string

@description('Azure region for the Key Vault.')
param location string

@description('Tags to apply to the Key Vault.')
param tags object

@secure()
@description('Accessibility API key (optional).')
param accessibilityApiKey string

@secure()
@description('LLM API key (optional).')
param llmApiKey string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    enableRbacAuthorization: true
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource accessibilitySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(accessibilityApiKey)) {
  name: 'accessibility-api-key'
  parent: keyVault
  properties: {
    value: accessibilityApiKey
  }
}

resource llmSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(llmApiKey)) {
  name: 'llm-api-key'
  parent: keyVault
  properties: {
    value: llmApiKey
  }
}

output vaultId string = keyVault.id
output vaultName string = keyVault.name
output vaultUri string = keyVault.properties.vaultUri
