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

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
BICEP_FILE="$SCRIPT_DIR/../main.bicep"

CORS_ALLOWED_ORIGINS=${CORS_ALLOWED_ORIGINS:-'[]'}
DEPLOY_EVENTGRID_SUBSCRIPTION=${DEPLOY_EVENTGRID_SUBSCRIPTION:-true}
EVENTGRID_SUBSCRIPTION_WAIT_SECONDS=${EVENTGRID_SUBSCRIPTION_WAIT_SECONDS:-60}
EVENTGRID_SUBSCRIPTION_RETRY_WAIT_SECONDS=${EVENTGRID_SUBSCRIPTION_RETRY_WAIT_SECONDS:-30}
EVENTGRID_SUBSCRIPTION_MAX_ATTEMPTS=${EVENTGRID_SUBSCRIPTION_MAX_ATTEMPTS:-5}

required_vars=(
  DEPLOYMENT_NAME
  RESOURCE_GROUP
  LOCATION
  APP_NAME
  ENVIRONMENT
  SQL_ADMIN_LOGIN
  SQL_ADMIN_PASSWORD
)

missing_vars=()
for var in "${required_vars[@]}"; do
  if [[ -z "${!var:-}" ]]; then
    missing_vars+=("$var")
  fi
done

if (( ${#missing_vars[@]} > 0 )); then
  echo "Missing required environment variables: ${missing_vars[*]}" >&2
  exit 1
fi

subscription_value=""
subscription_label=""

if [[ -n "${SUBSCRIPTION_ID:-}" ]]; then
  subscription_value="$SUBSCRIPTION_ID"
  subscription_label=${SUBSCRIPTION_NAME:-}
elif [[ -n "${SUBSCRIPTION_NAME:-}" ]]; then
  subscription_value="$SUBSCRIPTION_NAME"
fi

if [[ -n "$subscription_value" ]]; then
  if [[ -n "$subscription_label" ]]; then
    echo "Setting Azure subscription to $subscription_label ($subscription_value)..."
  else
    echo "Setting Azure subscription to $subscription_value..."
  fi
  az account set --subscription "$subscription_value"
fi

echo "Ensuring resource group $RESOURCE_GROUP exists in $LOCATION..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none

echo "Deploying base resources in $RESOURCE_GROUP..."

deployment_name_args=(--name "$DEPLOYMENT_NAME")

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  "${deployment_name_args[@]}" \
  --template-file "$BICEP_FILE" \
  --parameters appName="$APP_NAME" env="$ENVIRONMENT" \
  --parameters corsAllowedOrigins="$CORS_ALLOWED_ORIGINS" \
  --parameters sqlAdminLogin="$SQL_ADMIN_LOGIN" sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
  --parameters deployEventGridSubscription=false

deploy_eventgrid_subscription=$(printf '%s' "$DEPLOY_EVENTGRID_SUBSCRIPTION" | tr '[:upper:]' '[:lower:]')
if [[ "$deploy_eventgrid_subscription" != "true" ]]; then
  echo "Deployment complete (Event Grid subscription skipped)."
  exit 0
fi

echo "Waiting for role assignments to propagate before creating the Event Grid subscription..."
sleep "$EVENTGRID_SUBSCRIPTION_WAIT_SECONDS"

max_attempts=$EVENTGRID_SUBSCRIPTION_MAX_ATTEMPTS
attempt=1

while (( attempt <= max_attempts )); do
  echo "Deploying Event Grid subscription (attempt $attempt/$max_attempts)..."
  if az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    "${deployment_name_args[@]}" \
    --template-file "$BICEP_FILE" \
    --parameters appName="$APP_NAME" env="$ENVIRONMENT" \
    --parameters corsAllowedOrigins="$CORS_ALLOWED_ORIGINS" \
    --parameters sqlAdminLogin="$SQL_ADMIN_LOGIN" sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
    --parameters deployEventGridSubscription=true; then
    echo "Deployment complete."
    exit 0
  fi

  if (( attempt == max_attempts )); then
    echo "Event Grid subscription deployment failed after $max_attempts attempts." >&2
    exit 1
  fi

  echo "Retrying Event Grid subscription deployment after a short wait..."
  sleep "$EVENTGRID_SUBSCRIPTION_RETRY_WAIT_SECONDS"
  attempt=$(( attempt + 1 ))
done
