@description('SQL server name.')
param name string

@description('Azure region for SQL resources.')
param location string

@description('Tags to apply to SQL resources.')
param tags object

@description('SQL admin login for SQL authentication.')
param adminLogin string

@secure()
@description('SQL admin password for SQL authentication.')
param adminPassword string

@description('SQL database name.')
param databaseName string

@description('SQL database SKU name.')
param skuName string = 'S0'

@description('SQL database SKU tier.')
param skuTier string = 'Standard'

resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: name
  location: location
  tags: tags
  properties: {
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: '1.2'
  }
}

resource database 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  name: databaseName
  parent: sqlServer
  location: location
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

output serverName string = sqlServer.name
output databaseName string = database.name
