@description('Container Apps environment name.')
param name string

@description('Azure region for the environment.')
param location string

@description('Tags to apply to the environment.')
param tags object

@description('Log Analytics workspace customer ID.')
param logAnalyticsCustomerId string

@secure()
@description('Log Analytics workspace shared key.')
param logAnalyticsSharedKey string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
  }
}

output id string = environment.id
output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
