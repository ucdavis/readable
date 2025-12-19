@description('Azure region for compute resources.')
param location string

@description('Tags to apply to compute resources.')
param tags object

@description('App Service plan name for the web app.')
param webPlanName string

@description('Web App name for the web app.')
param webAppName string

@description('Function App plan name.')
param functionPlanName string

@description('Function App name.')
param functionAppName string

@description('Data storage account name.')
param dataStorageAccountName string

@secure()
@description('Data storage connection string.')
param dataStorageConnectionString string

@description('Blob container name for incoming files.')
param incomingContainerName string

@description('Blob container name for processed files.')
param processedContainerName string

@description('Blob container name for temp files.')
param tempContainerName string

@description('Blob container name for reports.')
param reportsContainerName string

@secure()
@description('Service Bus connection string (fallback).')
param serviceBusConnectionString string

@description('Service Bus fully qualified namespace.')
param serviceBusFullyQualifiedNamespace string

@description('Service Bus queue name for the function app.')
param serviceBusQueueName string = ''

@secure()
@description('Function host storage connection string.')
param functionStorageConnectionString string

@secure()
@description('SQL connection string.')
param sqlConnectionString string

@description('Environment name for app settings.')
param environmentName string

@description('Chunk size for pipeline processing.')
param pipelineChunkSizePages int = 100

resource webPlan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: webPlanName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    size: 'B1'
    capacity: 1
  }
  tags: tags
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2025-03-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    serverFarmId: webPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environmentName
        }
        {
          name: 'DB_CONNECTION'
          value: sqlConnectionString
        }
        {
          name: 'Storage__AccountName'
          value: dataStorageAccountName
        }
        {
          name: 'Storage__ConnectionString'
          value: dataStorageConnectionString
        }
        {
          name: 'Storage__IncomingContainer'
          value: incomingContainerName
        }
        {
          name: 'Storage__ProcessedContainer'
          value: processedContainerName
        }
        {
          name: 'Storage__ReportsContainer'
          value: reportsContainerName
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
    }
  }
}

resource functionPlan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: functionPlanName
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  tags: tags
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2025-03-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: concat([
        {
          name: 'AzureWebJobsStorage'
          value: functionStorageConnectionString
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'DB_CONNECTION'
          value: sqlConnectionString
        }
        {
          name: 'ServiceBus__FullyQualifiedNamespace'
          value: serviceBusFullyQualifiedNamespace
        }
        {
          name: 'ServiceBus__ConnectionString'
          value: serviceBusConnectionString
        }
        {
          name: 'Storage__AccountName'
          value: dataStorageAccountName
        }
        {
          name: 'Storage__ConnectionString'
          value: dataStorageConnectionString
        }
        {
          name: 'Storage__IncomingContainer'
          value: incomingContainerName
        }
        {
          name: 'Storage__ProcessedContainer'
          value: processedContainerName
        }
        {
          name: 'Storage__TempContainer'
          value: tempContainerName
        }
        {
          name: 'Storage__ReportsContainer'
          value: reportsContainerName
        }
        {
          name: 'Pipeline__ChunkSizePages'
          value: string(pipelineChunkSizePages)
        }
      ], serviceBusQueueName != '' ? [
        {
          name: 'ServiceBus__QueueName'
          value: serviceBusQueueName
        }
      ] : [], [
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ])
    }
  }
}

output webAppName string = webApp.name
output functionAppName string = functionApp.name
output webPrincipalId string = webApp.identity.principalId
output functionPrincipalId string = functionApp.identity.principalId
