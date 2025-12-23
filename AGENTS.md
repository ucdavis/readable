# GitHub Copilot Instructions

This is a full-stack web application template using modern React and .NET technologies. Please follow these guidelines when generating code suggestions:

## Architecture Overview

- **Frontend**: React 19 with TypeScript in Vite development environment
- **Backend**: ASP.NET Core 8.0 Web API
- **Development**: SPA proxy setup with hot reload

## Frontend Technology Stack

### Build Tools & Development

- **Vite** (`^7.1.5`) - Primary build tool and dev server (port 5175)
- **TypeScript** (`^5.9.2`) - Primary language for all React components
- **Node.js** `>=22.0.0` - Runtime requirement

### React & Routing

- **React** `^19.1.1` with **React DOM** `^19.1.1`
- **TanStack Router** (`^1.132.33`) - File-based routing system
  - Routes are in `src/routes/` directory
  - Auto-generated route tree in `routeTree.gen.ts`
  - Uses router context with QueryClient integration
  - Default preload strategy: `'intent'`
  - Router devtools available in development

### State Management & Data Fetching

- **TanStack Query** (`^5.90.2`) - Server state management
  - QueryClient configured with React Query devtools
  - Integrated with router context
  - Default preload stale time: 0 (always fresh)

### Forms & Tables

- **TanStack React Form** (`^1.23.5`) - Form state management
- **TanStack React Table** (`^8.21.3`) - Table/data grid functionality

### Styling & UI

- **Tailwind CSS** (`^4.1.14`) - Utility-first CSS framework
- **DaisyUI** (`^5.1.27`) - Tailwind CSS component library
- **UC Davis Gunrock Tailwind** (`^2.4.0`) - Custom design system
- CSS imports structure:
  ```css
  @import "tailwindcss";
  @plugin "daisyui";
  @import "@ucdavis/gunrock-tailwind/imports.css";
  ```

### Code Quality & Linting

- **ESLint** (`^9.35.0`) with custom config (`@nkzw/eslint-config`)
- **Prettier** (`^3.6.2`) - Code formatting
- **TanStack ESLint plugins** for Query and Router
- **Vitest** (`^3.2.4`) - Testing framework

### Path Aliases

- `@/` resolves to `./src/`

## Backend Technology Stack

### Framework & Runtime

- **ASP.NET Core 8.0** - Web API framework
- **.NET 8.0** - Target framework
- **C#** with nullable reference types enabled

### Authentication & Authorization

- **Microsoft Identity Web** (`^3.14.1`) - Authentication integration

### Monitoring & Observability

- **OpenTelemetry** - Distributed tracing and metrics
  - OTLP exporter
  - ASP.NET Core instrumentation
  - HTTP instrumentation

### Development Tools

- **Swashbuckle** (`^6.4.0`) - Swagger/OpenAPI documentation
- **DotNetEnv** (`^3.1.1`) - Environment variable management

## Development Patterns

### Project Structure

```
/
├── client/          # Vite React app
│   ├── src/
│   │   ├── routes/  # TanStack Router file-based routes
│   │   ├── queries/ # TanStack Query hooks
│   │   ├── lib/     # Utility functions
│   │   └── shared/  # Reusable components
└── server/          # ASP.NET Core API
    ├── Controllers/
    ├── Helpers/
    └── Properties/
```

### Routing Conventions

- File-based routing in `src/routes/`
- Protected routes under `(authenticated)/` directory
- Route components should use TanStack Router hooks
- Integrate with React Query for data fetching

### Component Guidelines

- Use TypeScript for all components
- Prefer function components with hooks
- Use Tailwind CSS classes for styling
- Leverage DaisyUI components when appropriate
- Follow UC Davis Gunrock design system patterns

### Data Fetching

- Use TanStack Query for server state
- Create custom hooks in `queries/` directory
- Integrate query invalidation with router navigation
- Use the configured QueryClient from router context

### Form Handling

- Use TanStack React Form for complex forms
- Combine with TanStack Query for server interactions
- Follow validation patterns consistent with the stack

### API Integration

- API endpoints proxy through Vite dev server
- Backend serves from `/api` routes
- Authentication handled via Microsoft Identity Web
- Use type-safe API client patterns

### Development Commands

- `npm run dev` - Start Vite development server
- `npm start` - Start .NET backend with watch mode
- `npm run build` - Build for production
- `npm run lint` - Run ESLint

### Testing

- `cd client && npm test` - Run the client Vitest suite once
- `cd client && npm run test:watch` - Watch client tests while developing
- `dotnet test` - Execute the .NET server test project

## Code Generation Preferences

1. **Always use TypeScript** - No plain JavaScript files
2. **Prefer functional components** - Use hooks over class components
3. **Use Tailwind CSS classes** - Avoid writing custom CSS unless necessary
4. **Leverage TanStack ecosystem** - Use Router, Query, Form, and Table together
5. **Follow file-based routing** - Create route files in appropriate directories
6. **Type-safe API calls** - Generate or maintain TypeScript interfaces for API responses
7. **Use modern React patterns** - Hooks, context, and concurrent features
8. **DaisyUI components** - Prefer DaisyUI components over custom implementations
9. **Environment-aware code** - Handle development vs production differences
10. **Responsive design** - Use Tailwind's responsive utilities

## Common Patterns

### Route Component Example (authenticated route, the default pattern)

```tsx
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";

export const Route = createFileRoute("/(authenticated)/example")({
  component: ExampleComponent,
});

function ExampleComponent() {
  const { data } = useQuery({
    queryKey: ["example"],
    queryFn: () => fetch("/api/example").then((res) => res.json()),
  });

  return (
    <div className="container mx-auto p-4">
      {/* DaisyUI and Tailwind styling */}
    </div>
  );
}
```

### API Controller Pattern

```csharp
[ApiController]
[Route("api/[controller]")]
public class ExampleController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetExample()
    {
        // Implementation
    }
}
```

When generating code, ensure it follows these patterns and integrates well with the existing technology stack.

---

## Backend: PDF Ingest + Remediation Notes (Project-Specific)

### Where the PDF pipeline lives

- PDF ingest is wired up in `server.core/Ingest/IngestServiceCollectionExtensions.cs` (DI registration for ingest + remediation services).
- The remediation implementation is `server.core/Remediate/PdfRemediationProcessor.cs` (runs inside ingest).
- Alt-text service implementations live in `server.core/Remediate/AltText/`:
  - `IAltTextService` + request models
  - `OpenAIAltTextService` (chat-based generation)
  - `SampleAltTextService` (local fallback when no API key)
- PDF-title service implementations live in `server.core/Remediate/Title/`:
  - `IPdfTitleService` + `PdfTitleRequest`
  - `OpenAIPdfTitleService` (chat-based title generation)
  - `SamplePdfTitleService` (local fallback when no API key)

### Remediation behaviors (current design)

- The remediation processor always opens the input PDF and writes an output PDF (it does not “copy through” early just because the PDF is untagged).
- Title remediation runs regardless of tagging:
  - Extracts text page-by-page from page 1 onward until reaching at least `TitleContextMinWords` (currently `100`) or scanning `TitleContextMaxPages` (currently `5`).
  - If there isn’t enough text:
    - If the PDF already has a title, keep it.
    - If the title is missing/blank, set a placeholder (currently `"Untitled PDF document"`).
  - If there is enough text, it calls `IPdfTitleService.GenerateTitleAsync()` and writes the returned title into PDF metadata (`DocumentInfo.Title`).
- Alt text remediation runs only for tagged PDFs:
  - Iterates pages, matches content-stream occurrences to tag-tree elements (role `Figure` and `Link`) using MCID/object refs.
  - Calls `IAltTextService` for images/links missing `Alt`.
  - Includes a fallback “safety net” pass that sets `Alt` on any remaining `Figure`/`Link` tag-tree nodes.

### Environment variables for AI-backed remediation

- `OPENAI_API_KEY`: enables OpenAI-backed services; otherwise “Sample*” services are used.
- `OPENAI_ALT_TEXT_MODEL`: model for `OpenAIAltTextService` (default `gpt-4o-mini`).
- `OPENAI_PDF_TITLE_MODEL`: model for `OpenAIPdfTitleService` (default `gpt-4o-mini`).

### Adding a new remediation step

- Prefer adding steps inside `PdfRemediationProcessor.ProcessAsync()` so it’s obvious what runs, and in what order.
- If the step might be needed even for untagged PDFs (metadata changes, etc.), do it before the `if (!pdf.IsTagged()) return ...` short-circuit.
- For anything that calls external services, keep the service behind a small interface (like `IAltTextService` / `IPdfTitleService`) and register it in `IngestServiceCollectionExtensions`.

## Server Testing Notes (Project-Specific)

### Where tests live and how they’re written

- Server tests are in `tests/server.tests/` using xUnit + FluentAssertions.
- Remediation tests are integration-style and live under `tests/server.tests/Integration/Remediate/`.
- Existing PDF fixtures live in `tests/server.tests/Fixtures/pdfs/` (useful when you need repeatable tagged/structured PDFs).
- Test style:
  - Create an isolated temp directory per test run under `Path.GetTempPath()` and delete it in a `finally` block.
  - Prefer tiny fake/capturing services over hitting OpenAI (tests should remain offline/deterministic).
  - When you need a custom PDF for a test, it’s OK to generate one on the fly with iText (e.g., using `iText.Layout.Document` and `Paragraph`).

### Running tests in sandboxed environments

- `dotnet test` may require extra permissions in some sandboxed environments due to MSBuild using IPC/named pipes.
  - If you see “Permission denied” from MSBuild/NamedPipeServerStream, rerun tests with the necessary elevated permissions in your environment.
