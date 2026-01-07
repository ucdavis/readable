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

@description('Whether to allow Azure services/resources to access this SQL server (creates firewall rule 0.0.0.0).')
param allowAzureServices bool = true

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
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

// Enables the Azure SQL setting "Allow Azure services and resources to access this server".
// This is represented as a special firewall rule where start/end IP are 0.0.0.0.
resource allowAzureServicesFirewallRule 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (allowAzureServices) {
  name: 'AllowAzureServices'
  parent: sqlServer
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
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
