# remediate.diagnostics.runner

Small console runner for inspecting PDF remediation inputs (tag tree vs rendered images).

It’s useful for understanding why a PDF ends up with the fallback alt text (e.g. `"alt text for image"`) on many
`/Figure` elements.

## Run

From the repo root:

```bash
dotnet run --project tools/remediate.diagnostics.runner -- --input /path/to/file.pdf
```

Optionally extract unique raster images found during content-stream scanning:

```bash
dotnet run --project tools/remediate.diagnostics.runner -- --input /path/to/file.pdf --extract-images /tmp/extracted-images
```

