# ingest.runner

Small console runner for the ingest pipeline so you can iterate locally without Azure Functions / Service Bus.

It uses the same `server.core` ingest services that the `function_ingest` Azure Function calls.

## Prereqs

- .NET SDK 8 (`global.json` roll-forwards to latest patch, e.g. `8.0.416`).

## Run

From the repo root:

```bash
dotnet run --project tools/ingest.runner -- --blob-url "https://<account>.blob.core.windows.net/incoming/<fileId>.pdf"
```

Or parse a CloudEvent JSON file (same shape as Service Bus message payload):

```bash
dotnet run --project tools/ingest.runner -- --cloud-event-json /path/to/event.json
```

## Storage access

The runner opens the blob in one of these ways:

1. If `Storage__ConnectionString` is set, it uses that to authenticate and open the blob.
2. Otherwise it tries the blob URL directly (works for public blobs or URLs with a SAS token).

### Local env

The runner loads environment values from `server/.env` (and `.env` at the repo root) if present, and then overlays OS environment variables.

Set this in your environment (or in `server/.env` to reuse the same value as the Web API):

```bash
export Storage__ConnectionString="<storage-connection-string>"
```

## OpenTelemetry (optional)

If `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_HEADERS` / `OTEL_SERVICE_NAME` are set, the runner will emit logs/traces/metrics via OTLP.

To fully disable local OTEL:

```bash
export OTEL_SDK_DISABLED=true
```
