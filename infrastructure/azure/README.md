# Azure infrastructure

Use the environment-specific deploy scripts to provision Azure resources.

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

## Deploy (test)

```bash
export SQL_ADMIN_PASSWORD='your-strong-password'
./infrastructure/azure/scripts/deploy_test.sh
```

## Deploy (prod)

```bash
export SQL_ADMIN_PASSWORD='your-strong-password'
./infrastructure/azure/scripts/deploy_prod.sh
```

## Notes

- The script expects Azure CLI to be installed and will fail fast if it is missing.
- The wrapper scripts set defaults for subscription, resource group, and environment for each target (`dev`, `test`, `prod`).

## Deploy function code (ingest)

The infra deployment provisions the Function App, but does not publish code to it.

1. Find the Function App name:

```bash
az functionapp list -g rg-readable-dev --query "[].name" -o tsv
```

2. Publish the function from this repo:

```bash
cd workers/function.ingest
func azure functionapp publish <FUNCTION_APP_NAME> --dotnet-isolated --nozip
```

## Deploy OpenDataLoader Container App

The base Bicep deployment now provisions:

- an Azure Container Registry,
- a Container Apps environment,
- and, once an image + secret are supplied, the `opendataloader` Container App itself.

After the base infra exists, build and push the worker image:

```bash
az acr login --name <ACR_NAME>
docker build -f workers/opendataloader.api/Dockerfile -t <ACR_LOGIN_SERVER>/opendataloader-api:<TAG> .
docker push <ACR_LOGIN_SERVER>/opendataloader-api:<TAG>
```

Then rerun the deploy script with the image reference and API key:

```bash
export ODL_SHARED_SECRET='your-shared-secret'
export OPEN_DATA_LOADER_IMAGE='<ACR_LOGIN_SERVER>/opendataloader-api:<TAG>'
export OPEN_DATA_LOADER_SHARED_SECRET="$ODL_SHARED_SECRET"
export DEPLOY_EVENTGRID_SUBSCRIPTION=false
./infrastructure/azure/scripts/deploy_dev.sh
```

`ODL_SHARED_SECRET` is the runtime secret used by the function app and local
calls to the OpenDataLoader API. `OPEN_DATA_LOADER_SHARED_SECRET` is the deploy
script parameter that provisions the Container App with that same secret.

Get the URL from deployment outputs:

```bash
az deployment group show \
  --resource-group <RESOURCE_GROUP> \
  --name <DEPLOYMENT_NAME> \
  --query "properties.outputs.openDataLoaderContainerAppUrl.value" \
  -o tsv
```

Call the API:

```bash
curl -X POST \
  https://<CONTAINER_APP_FQDN>/convert \
  -H 'Content-Type: application/pdf' \
  -H 'X-Api-Key: <ODL_SHARED_SECRET>' \
  --data-binary @./sample.pdf \
  --output ./sample.tagged.pdf
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
