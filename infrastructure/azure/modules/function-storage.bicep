@description('Storage account name for function host.')
param name string

@description('Azure region for the storage account.')
param location string

@description('Tags to apply to the storage account.')
param tags object

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  tags: tags
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${listKeys(storageAccount.id, '2023-01-01').keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

output accountName string = storageAccount.name
output accountId string = storageAccount.id
@secure()
output connectionString string = storageConnectionString
