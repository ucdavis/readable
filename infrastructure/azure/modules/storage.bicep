@description('Storage account name.')
param name string

@description('Azure region for the storage account.')
param location string

@description('Tags to apply to the storage account.')
param tags object

@description('Allowed CORS origins for blob service; empty disables CORS rules.')
param corsAllowedOrigins array

@description('Blob container names to create.')
param containerNames array

@description('Container name used for temporary blobs (for lifecycle cleanup).')
param tempContainerName string

@minValue(0)
@description('Delete temp blobs after this many days. Set to 0 to disable.')
param tempDeleteAfterDays int = 7

@minValue(1)
@description('Soft delete retention days for blobs.')
param blobDeleteRetentionDays int = 7

@description('Enable blob versioning.')
param enableBlobVersioning bool = true

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

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  name: 'default'
  parent: storageAccount
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: blobDeleteRetentionDays
    }
    isVersioningEnabled: enableBlobVersioning
    cors: {
      corsRules: length(corsAllowedOrigins) > 0 ? [
        {
          allowedOrigins: corsAllowedOrigins
          allowedMethods: [
            'GET'
            'PUT'
            'POST'
            'HEAD'
            'OPTIONS'
          ]
          allowedHeaders: [
            '*'
          ]
          exposedHeaders: [
            '*'
          ]
          maxAgeInSeconds: 3600
        }
      ] : []
    }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = [for containerName in containerNames: {
  name: containerName
  parent: blobService
  properties: {
    publicAccess: 'None'
  }
}]

resource managementPolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2025-06-01' = if (tempDeleteAfterDays > 0) {
  name: 'default'
  parent: storageAccount
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-temp-after-${tempDeleteAfterDays}-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${tempContainerName}/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: tempDeleteAfterDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${listKeys(storageAccount.id, '2023-01-01').keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

output accountName string = storageAccount.name
output accountId string = storageAccount.id
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
@secure()
output connectionString string = storageConnectionString
