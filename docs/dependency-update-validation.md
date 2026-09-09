# Dependency update validation

Validated on 2026-09-08 on branch `srk/update-dependencies-odl`.

## Updates

- OpenDataLoader PDF 2.4.1 to 2.5.7. The worker image was rebuilt from refreshed .NET 8 base images and reports .NET/ASP.NET Core 8.0.31.
- .NET SDK 8.0.4xx feature band, patch 8.0.425 or later within that band; EF Core and the local EF tool 8.0.31.
- iText and its Bouncy Castle adapter 9.7.0, Adobe PDF Services 4.4.0, OpenAI 2.13.0, Azure Storage/Service Bus, Functions worker packages, and OpenTelemetry.
- Microsoft Identity Web 4.14.2 and Swashbuckle 10.2.3. Swagger's API-key definition was adapted to the current OpenAPI types. OpenAI Responses now uses `ResponsesClientOptions`.
- Compatible React, TanStack, Vite, Vitest, Tailwind, DaisyUI, Gunrock, testing, and lint dependencies. Small compatibility edits satisfy the updated lint rules. The router generated its updated route tree.
- Removed the unused root `npm-run-all` dependency.
- Explicit patched references for Adobe's transitive `log4net`, `RestSharp`, and `System.Net.Http` dependencies. RestSharp stays on the patched 112.x line used in the real Adobe tests.

The application remains on .NET 8. Major frontend compiler, bundler, table, and test framework migrations were kept out of this dependency/security refresh. No database schema or queue contract changes were needed.

## Local checks

- `dotnet build app.sln`: passed with no warnings or errors.
- Server tests: 192 passed. The external PDF theory is explicitly skipped unless enabled.
- Clean `npm ci`, client production build, seven tests across five files, and ESLint: passed.
- React Doctor changed-code scan: no reported findings.
- `npm audit`: zero vulnerabilities, down from 21.
- NuGet audit with transitive dependencies: zero findings in the test project, which references both workers, the API, and core. Separately audited the generated Functions WorkerExtensions project with zero findings.
- ODL Docker image built successfully and reported `opendataloader-pdf==2.5.7`.
- The ingest, bookmarks, and remediation diagnostics runner projects built without warnings/errors; local .NET tools restored successfully.

## Container security findings

Trivy scanned the final image, including Debian, the bundled ODL Java JAR, Python, and .NET dependencies. No Java, Python, or .NET findings remain. Both real ODL/Adobe PDF tests also passed against this final image. Applying OS updates also removed five fixable Debian findings for `libpcre2-8-0`.

The final Debian 12 image still has 439 scanner findings with no published fixed version in the selected distribution: 6 critical, 86 high, 206 medium, and 141 low. Several are marked `fix_deferred` or `will_not_fix`; these are scanner findings, not proof that every vulnerable code path is reachable by the worker. They have not been suppressed or claimed resolved. Eliminating this remaining OS backlog requires upstream fixes or a separately validated base-distribution migration.

The six critical entries are `CVE-2026-58016` in GLib, `CVE-2025-7458` in SQLite, `CVE-2026-13221`, `CVE-2026-42496`, and `CVE-2026-8376` in Perl, and `CVE-2023-45853` in zlib. The full scan is retained locally at `outputs/dependency-validation/container-audit.json`.

Docker Scout could not download its Java advisory database. The completed Trivy scan used the public ECR database mirrors instead.

## Running application

Started the API and Vite against a new local SQL Server database, `ReadableDependencySmoke`. Database migrations completed and `/health` returned HTTP 200 with `Healthy`. Swagger generated 14 API paths and preserved the `X-Api-Key` security scheme and requirement.

An unauthenticated browser reached the expected Entra/UC Davis login redirect. The public FAQ page loaded, a deep link opened its matching accordion, and the accordion could be closed and reopened by navigating away and back to the hash.

This validation did not sign in or run an upload through deployed Azure Service Bus queues. The PDF tests invoke the production intake/finalization processor and real ODL runner locally.

## Real PDF processing

Both fixtures passed with real ODL and Adobe plus deterministic AI fakes, then with real OpenAI title and image-alt services as well. Bookmarks use the test's existing no-op service.

| Fixture | Pages preserved | Form fields preserved | Adobe failed rules before | Adobe failed rules after |
| --- | --- | --- | --- | --- |
| `forms.pdf` | 1 of 1 | 7 of 7 | 8 | 2 |
| `untagged.pdf` | 3 of 3 | No form fields | 19 | 0 |

The tests reopen the output PDFs and assert exact per-page text preservation, page counts, tagged status, and nonempty titles. The form test also asserts preserved field/widget counts, association of every widget with the structure parent tree, and Adobe's "Tagged form fields" rule changing from Failed to Passed.

The newsletter received a generated title and four image descriptions. All four output pages were rendered and visually inspected; the live-AI output renders were pixel-identical to the inspected deterministic-AI renders.

The form's two remaining automated failures are "Character encoding" and "Hides annotation". Adobe also retains two manual checks for the form and three for the newsletter. Passing automated checks is not a claim of full accessibility compliance.

Local outputs and Adobe reports are retained under the ignored directory `outputs/dependency-smoke-live-ai/`. These files are not published in the repository. Run the command below from the repository root to generate them locally:

- Remediated form: `outputs/dependency-smoke-live-ai/forms.remediated.pdf`
- Remediated newsletter: `outputs/dependency-smoke-live-ai/untagged.remediated.pdf`

To repeat the live service checks after building the worker image:

```bash
READABLE_RUN_EXTERNAL_PDF_TESTS=1 \
READABLE_RUN_EXTERNAL_AI_TESTS=1 \
READABLE_EXTERNAL_PDF_ARTIFACT_DIR="$PWD/outputs/dependency-smoke-live-ai" \
ODL_COMMAND_PATH="$PWD/tools/opendataloader-pdf-docker" \
dotnet test tests/server.tests/server.tests.csproj \
  --filter 'FullyQualifiedName~PdfProcessorOpenDataLoaderExternalTests'
```

Credentials are loaded from `server/.env` and environment variables. The live checks make Adobe and OpenAI API calls.
