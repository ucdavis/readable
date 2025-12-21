targetScope = 'resourceGroup'

@description('Base name used for resources (keep short).')
param appName string = 'readable'

@allowed([
  'dev'
  'test'
  'prod'
])
@description('Deployment environment name.')
param env string = 'dev'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Allowed CORS origins for blob uploads (SPA URLs). Example: ["https://localhost:5175"].')
param corsAllowedOrigins array = []

@description('SQL admin login for SQL authentication.')
param sqlAdminLogin string

@secure()
@description('SQL admin password for SQL authentication.')
param sqlAdminPassword string

@description('SQL database name.')
param sqlDatabaseName string = appName

@description('Service Bus queue base name. Leave empty for `files`.')
param serviceBusQueueBaseName string = ''

@description('Additional resource tags to apply.')
param tags object = {}

@description('Whether to deploy the Event Grid subscription.')
param deployEventGridSubscription bool = true

@minValue(30)
@maxValue(730)
@description('Application Insights retention in days.')
param appInsightsRetentionInDays int = 30

var appSlug = toLower(replace(replace(replace(appName, '-', ''), '_', ''), ' ', ''))
var appNameSafe = toLower(replace(replace(appName, ' ', ''), '_', ''))
var envSlug = toLower(replace(replace(env, '-', ''), ' ', ''))
var nameToken = substring(uniqueString(resourceGroup().id, appName, env), 0, 6)

var dataStorageName = take('st${appSlug}${envSlug}d${nameToken}', 24)
var functionStorageName = take('st${appSlug}${envSlug}f${nameToken}', 24)
var serviceBusNamespaceName = toLower('sb-${appNameSafe}-${env}-${nameToken}')
var eventGridTopicName = toLower('eg-${appNameSafe}-${env}-${nameToken}')
var eventGridDeliveryIdentityName = toLower('uai-eg-${appNameSafe}-${env}-${nameToken}')
var sqlServerName = toLower('sql-${appNameSafe}-${env}-${nameToken}')
var webPlanName = toLower('asp-${appNameSafe}-${env}-${nameToken}')
var webAppName = toLower('web-${appNameSafe}-${env}-${nameToken}')
var functionPlanName = toLower('func-${appNameSafe}-${env}-${nameToken}')
var functionAppName = toLower('fn-${appNameSafe}-${env}-${nameToken}')
var appInsightsName = toLower('appi-${appNameSafe}-${env}-${nameToken}')
var logAnalyticsWorkspaceName = toLower('log-${appNameSafe}-${env}-${nameToken}')
var sqlSkuName = env == 'prod' ? 'S0' : 'Basic'
var sqlSkuTier = env == 'prod' ? 'Standard' : 'Basic'

var baseQueueName = serviceBusQueueBaseName == '' ? 'files' : serviceBusQueueBaseName
var fileQueueNames = [
  baseQueueName
]

var incomingContainerName = 'incoming'
var processedContainerName = 'processed'
var tempContainerName = 'temp'
var reportsContainerName = 'reports'
var deadLetterContainerName = 'deadletter'
var functionDeploymentContainerName = 'deployment'

var resourceTags = union(tags, {
  environment: env
  application: appName
})

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  tags: resourceTags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: resourceTags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: appInsightsRetentionInDays
  }
}

resource eventGridDeliveryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: eventGridDeliveryIdentityName
  location: location
  tags: resourceTags
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: dataStorageName
    location: location
    tags: resourceTags
    corsAllowedOrigins: corsAllowedOrigins
    containerNames: [
      incomingContainerName
      processedContainerName
      tempContainerName
      reportsContainerName
      deadLetterContainerName
    ]
    tempContainerName: tempContainerName
  }
}

module functionStorage 'modules/function-storage.bicep' = {
  name: 'functionStorage'
  params: {
    name: functionStorageName
    location: location
    tags: resourceTags
    deploymentContainerName: functionDeploymentContainerName
  }
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    name: serviceBusNamespaceName
    location: location
    tags: resourceTags
    queueNames: fileQueueNames
  }
}

module eventGrid 'modules/eventgrid.bicep' = {
  name: 'eventgrid'
  params: {
    name: eventGridTopicName
    location: location
    tags: resourceTags
    storageAccountId: storage.outputs.accountId
    deliveryIdentityResourceId: eventGridDeliveryIdentity.id
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    name: sqlServerName
    location: location
    tags: resourceTags
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
    databaseName: sqlDatabaseName
    skuName: sqlSkuName
    skuTier: sqlSkuTier
  }
}

var sqlServerFqdn = '${sqlServerName}.${environment().suffixes.sqlServerHostname}'
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

module compute 'modules/compute.bicep' = {
  name: 'compute'
  params: {
    location: location
    tags: resourceTags
    webPlanName: webPlanName
    webAppName: webAppName
    functionPlanName: functionPlanName
    functionAppName: functionAppName
    functionStorageAccountName: functionStorageName
    functionDeploymentContainerName: functionDeploymentContainerName
    dataStorageAccountName: storage.outputs.accountName
    dataStorageConnectionString: storage.outputs.connectionString
    incomingContainerName: incomingContainerName
    processedContainerName: processedContainerName
    tempContainerName: tempContainerName
    reportsContainerName: reportsContainerName
    serviceBusConnectionString: serviceBus.outputs.connectionString
    serviceBusFullyQualifiedNamespace: serviceBus.outputs.fullyQualifiedNamespace
    serviceBusQueueName: baseQueueName
    functionStorageConnectionString: functionStorage.outputs.connectionString
    sqlConnectionString: sqlConnectionString
    environmentName: env
    appInsightsConnectionString: reference(appInsights.id, '2020-02-02').ConnectionString
    appInsightsInstrumentationKey: reference(appInsights.id, '2020-02-02').InstrumentationKey
  }
}

module eventGridRbac 'modules/rbac-eventgrid.bicep' = {
  name: 'rbac-eventgrid'
  params: {
    eventGridPrincipalId: eventGridDeliveryIdentity.properties.principalId
    serviceBusNamespaceName: serviceBus.outputs.namespaceName
    storageAccountName: storage.outputs.accountName
  }
}

module eventGridSubscription 'modules/eventgrid-subscription.bicep' = if (deployEventGridSubscription) {
  name: 'eventgrid-subscription'
  params: {
    systemTopicName: eventGrid.outputs.topicName
    storageAccountId: storage.outputs.accountId
    serviceBusQueueIds: serviceBus.outputs.queueIds
    deadLetterContainerName: deadLetterContainerName
    deliveryIdentityResourceId: eventGridDeliveryIdentity.id
  }
  dependsOn: [
    eventGridRbac
  ]
}

module computeRbac 'modules/rbac-compute.bicep' = {
  name: 'rbac-compute'
  params: {
    webPrincipalId: compute!.outputs.webPrincipalId
    functionPrincipalId: compute!.outputs.functionPrincipalId
    storageAccountName: storage.outputs.accountName
    serviceBusNamespaceName: serviceBusNamespaceName
  }
}

output storageAccountName string = storage.outputs.accountName
output storageBlobEndpoint string = storage.outputs.blobEndpoint
output functionStorageAccountName string = functionStorageName
output serviceBusNamespaceName string = serviceBusNamespaceName
output serviceBusQueueNames array = fileQueueNames
output eventGridSystemTopicName string = eventGrid.outputs.topicName
output sqlServerName string = sql.outputs.serverName
output sqlDatabaseName string = sqlDatabaseName
output webAppName string = webAppName
output functionAppName string = functionAppName
