# Azure infrastructure (Bicep)

This directory contains the v1 Bicep templates for the Readable architecture:

- Storage account + blob containers (`incoming`, `processed`, `temp`, `reports`, `deadletter`)
- Event Grid system topic + subscriptions to Service Bus queue(s)
- Service Bus namespace + queue(s)
- Azure SQL server + database (SQL auth)
- App Service (Web) + Functions (Flex Consumption)
- Managed identity RBAC for Event Grid, Web App, and Functions

## Deploy

1. Create a resource group if you do not already have one:

```bash
az group create -n rg-readable-test -l westus2
```

2. Run the deployment (example with compute enabled):

[Add `what-if` flag to preview changes without applying them, ex `az deployment group create what-if ...`]

```bash
az deployment group create \
  -g rg-readable-test \
  -f infrastructure/azure/main.bicep \
  -p appName=readable env=test \
  -p corsAllowedOrigins='["*"]' \
  -p sqlAdminLogin='sqladmin' sqlAdminPassword='123'
```

## Parameters

- `appName` (optional, default `readable`): Base name used for resource naming.
- `env` (optional, default `dev`): Environment name (`dev`, `test`, `prod`).
- `corsAllowedOrigins` (optional): CORS origins for blob upload; omit or empty array to disable rules.
- `sqlAdminLogin` / `sqlAdminPassword` (required): SQL auth credentials.
- `sqlDatabaseName` (optional, default `{appName}`): SQL database name.
- SQL SKU defaults to `S0` for `prod`, `Basic` for `dev`/`test`.
- `serviceBusQueueBaseName` (optional): Overrides the base queue name (`files`).
