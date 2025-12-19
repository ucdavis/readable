# Azure infrastructure (Bicep)

This directory contains the v1 Bicep templates for the Readable architecture:

- Storage account + blob containers (`incoming`, `processed`, `temp`, `reports`, `deadletter`)
- Event Grid system topic + subscriptions to Service Bus queue(s)
- Service Bus namespace + queue(s)
- Azure SQL server + database (SQL auth)
- Optional compute: App Service (API) + Functions (Durable)
- Managed identity RBAC for Event Grid, API, and Functions

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
  -p appName=readable env=test deployCompute=true \
  -p corsAllowedOrigins='["*"]' \
  -p sqlAdminLogin='sqladmin' sqlAdminPassword='123'
```

## Parameters

- `appName` (optional, default `readable`): Base name used for resource naming.
- `env` (optional, default `dev`): Environment name (`dev`, `test`, `prod`).
- `devAliases` (optional): When `env=dev`, creates per-dev queues + subscriptions (`files-{alias}`).
- `deployCompute` (optional, default `true`): Creates App Service + Functions when `true`.
- `corsAllowedOrigins` (optional): CORS origins for blob upload; omit or empty array to disable rules.
- `sqlAdminLogin` / `sqlAdminPassword` (required): SQL auth credentials.
- `sqlDatabaseName` (optional, default `db-{appName}-{env}`): SQL database name.
- `serviceBusQueueBaseName` (optional): Overrides the base queue name (`files`).
- `functionQueueName` (optional): Queue name for the function app (defaults to the first queue; set explicitly when multiple dev aliases are used).

## Dev queue routing

If you set `env=dev` and provide `devAliases`, Event Grid routes:

- `incoming/dev/{alias}/*.pdf` -> `files-{alias}`

For a single shared dev queue, leave `devAliases` empty and upload to `incoming/...`. If you deploy compute with multiple dev aliases, set `functionQueueName` so the function app knows which queue to process.
