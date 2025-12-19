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

@description('Dev aliases used only when env == dev (creates per-dev queues + event subscriptions).')
param devAliases array = []

@description('Deploy compute resources (API App Service + Functions). In dev you may set false if running locally.')
param deployCompute bool = true

@description('Allowed CORS origins for blob uploads (SPA URLs). Example: ["https://localhost:5175"].')
param corsAllowedOrigins array = []

@description('SQL admin login for SQL authentication.')
param sqlAdminLogin string

@secure()
@description('SQL admin password for SQL authentication.')
param sqlAdminPassword string

@description('SQL database name.')
param sqlDatabaseName string = 'sqldb-${appName}-${env}'

@description('Service Bus queue base name. Leave empty for `files`.')
param serviceBusQueueBaseName string = ''

@description('Queue name for the function app to process. Leave empty to use the first queue.')
param functionQueueName string = ''

@description('Additional resource tags to apply.')
param tags object = {}

var appSlug = toLower(replace(replace(replace(appName, '-', ''), '_', ''), ' ', ''))
var appNameSafe = toLower(replace(replace(appName, ' ', ''), '_', ''))
var envSlug = toLower(replace(replace(env, '-', ''), ' ', ''))
var nameToken = substring(uniqueString(resourceGroup().id, appName, env), 0, 6)

var dataStorageName = take('st${appSlug}${envSlug}d${nameToken}', 24)
var functionStorageName = take('st${appSlug}${envSlug}f${nameToken}', 24)
var serviceBusNamespaceName = toLower('sb-${appNameSafe}-${env}-${nameToken}')
var eventGridTopicName = toLower('eg-${appNameSafe}-${env}-${nameToken}')
var sqlServerName = toLower('sql-${appNameSafe}-${env}-${nameToken}')
var apiPlanName = toLower('asp-${appNameSafe}-${env}-${nameToken}')
var apiAppName = toLower('api-${appNameSafe}-${env}-${nameToken}')
var functionPlanName = toLower('func-${appNameSafe}-${env}-${nameToken}')
var functionAppName = toLower('fn-${appNameSafe}-${env}-${nameToken}')

var baseQueueName = serviceBusQueueBaseName == '' ? 'files' : serviceBusQueueBaseName
var devQueueNames = [for alias in devAliases: '${baseQueueName}-${toLower(alias)}']
var fileQueueNames = (env == 'dev' && length(devAliases) > 0)
  ? devQueueNames
  : [
      baseQueueName
    ]

var incomingContainerName = 'incoming'
var processedContainerName = 'processed'
var tempContainerName = 'temp'
var reportsContainerName = 'reports'
var deadLetterContainerName = 'deadletter'

var resolvedFunctionQueueName = functionQueueName != ''
  ? functionQueueName
  : (env == 'dev' && length(devAliases) > 1 ? '' : fileQueueNames[0])

var resourceTags = union(tags, {
  environment: env
  application: appName
})

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

module functionStorage 'modules/function-storage.bicep' = if (deployCompute) {
  name: 'functionStorage'
  params: {
    name: functionStorageName
    location: location
    tags: resourceTags
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
    env: env
    devAliases: devAliases
    storageAccountId: storage.outputs.accountId
    serviceBusQueueIds: serviceBus.outputs.queueIds
    deadLetterContainerName: deadLetterContainerName
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
  }
}

var dataStorageId = resourceId('Microsoft.Storage/storageAccounts', dataStorageName)
var dataStorageKeys = listKeys(dataStorageId, '2023-01-01')
var dataStorageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.outputs.accountName};AccountKey=${dataStorageKeys.keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

var serviceBusAuthRuleId = resourceId('Microsoft.ServiceBus/namespaces/authorizationRules', serviceBusNamespaceName, 'app')
var serviceBusKeys = listKeys(serviceBusAuthRuleId, '2021-11-01')
var serviceBusConnectionString = serviceBusKeys.primaryConnectionString

var functionStorageConnectionString = deployCompute
  ? 'DefaultEndpointsProtocol=https;AccountName=${functionStorageName};AccountKey=${listKeys(resourceId('Microsoft.Storage/storageAccounts', functionStorageName), '2023-01-01').keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  : ''

var sqlServerFqdn = '${sqlServerName}.${environment().suffixes.sqlServerHostname}'
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

module compute 'modules/compute.bicep' = if (deployCompute) {
  name: 'compute'
  params: {
    location: location
    tags: resourceTags
    apiPlanName: apiPlanName
    apiAppName: apiAppName
    functionPlanName: functionPlanName
    functionAppName: functionAppName
    dataStorageAccountName: storage.outputs.accountName
    dataStorageConnectionString: dataStorageConnectionString
    incomingContainerName: incomingContainerName
    processedContainerName: processedContainerName
    tempContainerName: tempContainerName
    reportsContainerName: reportsContainerName
    serviceBusConnectionString: serviceBusConnectionString
    serviceBusFullyQualifiedNamespace: serviceBus.outputs.fullyQualifiedNamespace
    serviceBusQueueName: resolvedFunctionQueueName
    functionStorageConnectionString: functionStorageConnectionString
    sqlConnectionString: sqlConnectionString
    environmentName: env
  }
}

module eventGridRbac 'modules/rbac-eventgrid.bicep' = {
  name: 'rbac-eventgrid'
  params: {
    eventGridPrincipalId: eventGrid.outputs.principalId
    serviceBusNamespaceName: serviceBusNamespaceName
    storageAccountName: storage.outputs.accountName
  }
}

module computeRbac 'modules/rbac-compute.bicep' = if (deployCompute) {
  name: 'rbac-compute'
  params: {
    apiPrincipalId: compute!.outputs.apiPrincipalId
    functionPrincipalId: compute!.outputs.functionPrincipalId
    storageAccountName: storage.outputs.accountName
    serviceBusNamespaceName: serviceBusNamespaceName
  }
}

output storageAccountName string = storage.outputs.accountName
output storageBlobEndpoint string = storage.outputs.blobEndpoint
output functionStorageAccountName string = deployCompute ? functionStorageName : ''
output serviceBusNamespaceName string = serviceBusNamespaceName
output serviceBusQueueNames array = fileQueueNames
output eventGridSystemTopicName string = eventGrid.outputs.topicName
output sqlServerName string = sql.outputs.serverName
output sqlDatabaseName string = sqlDatabaseName
output apiAppName string = deployCompute ? apiAppName : ''
output functionAppName string = deployCompute ? functionAppName : ''
