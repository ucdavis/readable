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

@description('Function host storage account name.')
param functionStorageAccountName string

@description('Deployment container name for function app packages.')
param functionDeploymentContainerName string

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

@description('Application Insights connection string (optional).')
param appInsightsConnectionString string = ''

@description('Application Insights instrumentation key (optional).')
param appInsightsInstrumentationKey string = ''

@allowed([
  512
  2048
  4096
])
@description('Memory size (MB) for Flex Consumption function instances.')
param functionInstanceMemoryMB int = 2048

@minValue(1)
@maxValue(1000)
@description('Maximum number of Flex Consumption instances.')
param functionMaximumInstanceCount int = 10

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
      appSettings: concat([
        {
          name: 'AzureWebJobsStorage'
          value: functionStorageConnectionString
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
          name: 'ServiceBus'
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
      ], appInsightsConnectionString != '' ? [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsightsInstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_AGENT_EXTENSION_VERSION'
          value: '~3'
        }
      ] : [], serviceBusQueueName != '' ? [
        {
          name: 'ServiceBus__QueueName'
          value: serviceBusQueueName
        }
      ] : [])
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'BlobContainer'
          value: 'https://${functionStorageAccountName}.blob.${environment().suffixes.storage}/${functionDeploymentContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'AzureWebJobsStorage'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
      scaleAndConcurrency: {
        instanceMemoryMB: functionInstanceMemoryMB
        maximumInstanceCount: functionMaximumInstanceCount
      }
    }
  }
}

output webAppName string = webApp.name
output functionAppName string = functionApp.name
output webPrincipalId string = webApp.identity.principalId
output functionPrincipalId string = functionApp.identity.principalId
