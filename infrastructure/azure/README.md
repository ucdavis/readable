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
