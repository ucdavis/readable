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

## Image-purpose and composite-figure regression

See [image-purpose behavior](../../../README.md#images-outside-figure-tags-and-composite-figures)
for classification policy, supported scope and preservation rules.

`PdfImagePurposeTests` runs offline through the remediation processor, with real
PDF content streams and a deterministic classifier/rasterizer. It covers per-occurrence
classification of a shared image, the confidence cutoff, paragraph text and reading
order, parent-tree persistence after closing/reopening, PDF 2.0 namespaces,
role-mapped protected owners, errors, cancellation, rollback, invalid configuration,
repeat processing, and preservation of explicit or raster figure components.
Preservation regressions cover object-reference claims, ambiguous owners, inline
descriptions, Form and nested content, and fill/stroke patterns and soft masks,
including graphics state set before marked content. The mixed Form/page MCID
fixture checks reading order for both classification outcomes after reopening.
`OpenAIRemediationResponseOptionsTests` checks the classifier's two-image request
and rejects malformed model responses. Run both with:

```sh
dotnet test tests/server.tests/server.tests.csproj \
  --filter 'FullyQualifiedName~PdfImagePurposeTests|FullyQualifiedName~OpenAIRemediationResponseOptionsTests'
```

For a live document, retain the original and cached Adobe before report. Process a
copy using the configured alt-text model, inspect logged purpose decisions, compare
renders and extracted text, and verify unique MCIDs, balanced marked content and
parent-tree references before requesting one new Adobe after report. Do not rerun
an unchanged before check. Check both Figures alternate text and Other elements
alternate text, and ensure previously passing rules still pass. Keep source PDFs,
model responses and reports in ignored `outputs/`, outside Git. Adobe reading-order
and color-contrast manual checks still need human review.
