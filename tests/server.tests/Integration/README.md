# Integration tests

These tests exercise multiple components together (file I/O, PDF processing, etc.)

Tests that call real external services (e.g., OpenAI, Adobe PDF Services) will be gated behind environment variables and skip when not configured.

## Form annotation alt-text regression

See [form annotation alt text](../../../README.md#form-annotation-alt-text)
for the cleanup's eligibility and preservation rules.

The offline `PdfFormAnnotationAltTextTests` exercise the guards, processor wiring,
field preservation, and repeat processing. To check a previously processed PDF
with the real Adobe checker, run from the repository root:

```sh
READABLE_RUN_EXTERNAL_PDF_TESTS=1 \
READABLE_EXTERNAL_FORM_PDF=/absolute/path/to/failing-processed.pdf \
READABLE_EXTERNAL_PDF_ARTIFACT_DIR="$PWD/outputs/form-annotation-alt" \
dotnet test tests/server.tests/server.tests.csproj \
  --filter FullyQualifiedName~LabelledForm_WithRealAdobe
```

This opt-in test loads Adobe credentials from `server/.env`, the root `.env`,
then the environment, with later values taking precedence.
It runs two Adobe accessibility checks and applies only the form
cleanup between them, without calling ODL or OpenAI. It requires the target rule
to change from Failed to Passed and all previously passing rules to remain
Passed. Adobe's manual checks still require human review.
Outputs and both JSON reports are retained in the requested artifact
directory. Keep source PDFs and generated artifacts out of Git.
