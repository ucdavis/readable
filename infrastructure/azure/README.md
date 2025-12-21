# Azure infrastructure

Use the dev deploy script to provision the Azure resources for development.

## Deploy (dev)

1. Log in to Azure:

```bash
az login
```

2. Export the SQL admin password and run the script:

```bash
export SQL_ADMIN_PASSWORD='your-strong-password'
./infrastructure/azure/scripts/deploy_dev.sh
```

## Notes

- The script expects Azure CLI to be installed and will fail fast if it is missing.
- Additional environment scripts (test/prod) will be added later.

## DEV-ONLY - Deploy function code (ingest)

The infra deployment provisions the Function App, but does not publish code to it.

1. Find the Function App name:

```bash
az functionapp list -g rg-readable-dev --query "[].name" -o tsv
```

2. Publish the function from this repo:

```bash
cd workers/function.ingest
dotnet publish -c Release -o ./bin/publish
cd ./bin/publish
zip -r ../function_ingest.zip .
az functionapp deployment source config-zip -g rg-readable-dev -n <FUNCTION_APP_NAME> --src ../function_ingest.zip
```

## Tail logs (no Application Insights)

```bash
az webapp log config -g rg-readable-dev -n <FUNCTION_APP_NAME> --application-logging filesystem --level information
az functionapp log tail -g rg-readable-dev -n <FUNCTION_APP_NAME>
```

You can also use the Function App "Log stream" blade in the Azure Portal.

## Trigger the pipeline

The Event Grid subscription is filtered to only fire when a `.pdf` is uploaded into the `incoming` container.

Upload a PDF:

```bash
az storage blob upload \
  --account-name <DATA_STORAGE_ACCOUNT_NAME> \
  --container-name incoming \
  --name sample.pdf \
  --file ./sample.pdf \
  --auth-mode login
```
