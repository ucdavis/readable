#!/usr/bin/env bash
set -euo pipefail

# Ensure Azure CLI is available before proceeding.
if ! command -v az >/dev/null 2>&1; then
  echo "Azure CLI (az) is required but was not found on PATH." >&2
  exit 1
fi

# Warn early if the user is not logged in to Azure.
if ! az account show >/dev/null 2>&1; then
  echo "You must run 'az login' (or use a service principal) before running this script." >&2
  exit 1
fi

SUBSCRIPTION_NAME="UC Davis CAES Test"
SUBSCRIPTION_ID="105dede4-4731-492e-8c28-5121226319b0"

echo "Setting Azure subscription to $SUBSCRIPTION_NAME ($SUBSCRIPTION_ID)..."
az account set --subscription "$SUBSCRIPTION_ID"

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
BICEP_FILE="$SCRIPT_DIR/../main.bicep"

RESOURCE_GROUP="rg-readable-dev"
APP_NAME="readable"
ENVIRONMENT="dev"
CORS_ALLOWED_ORIGINS='["*"]'
SQL_ADMIN_LOGIN="readable"
LOCATION="westus2"
SQL_ADMIN_PASSWORD=${SQL_ADMIN_PASSWORD:-}

if [[ -z "$SQL_ADMIN_PASSWORD" ]]; then
  echo "Set the SQL_ADMIN_PASSWORD environment variable before running this script." >&2
  echo "Example: SQL_ADMIN_PASSWORD=123 $0" >&2
  exit 1
fi

echo "Ensuring resource group $RESOURCE_GROUP exists in $LOCATION..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none

echo "Deploying $BICEP_FILE in resource group $RESOURCE_GROUP..."

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$BICEP_FILE" \
  --parameters appName="$APP_NAME" env="$ENVIRONMENT" \
  --parameters corsAllowedOrigins="$CORS_ALLOWED_ORIGINS" \
  --parameters sqlAdminLogin="$SQL_ADMIN_LOGIN" sqlAdminPassword="$SQL_ADMIN_PASSWORD"

echo "Deployment request submitted. Review the Azure CLI output for details."
