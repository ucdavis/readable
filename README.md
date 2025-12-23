# Readable

This project will allow uploading PDF files to Azure Blob Storage. We'll then queue up processing and use Adobe PDF Services to grade the file for accessibility, auto-tag them, use AI to generate summaries/titles/images alt, etc, score again, and store the results back in a database for reporting.

## Architecture

- **Backend**: .NET 8 Web API with ASP.NET Core
- **Frontend**: React 19 with Vite, TypeScript, and TanStack Router/Query/Table
- **Authentication**: OIDC with Microsoft Entra ID (Azure AD)
- **Styling**: Tailwind CSS
- **Development**: Hot reload for both frontend and backend
- **Diagrams**: See `docs/architecture.md`

## Quick Start

1. **Clone the repository**

   ```bash
   git clone https://github.com/ucdavis/readable
   cd readable
   ```

2. **Open In DevContainer**

   - Open the project folder in Visual Studio Code.
   - Click the prompt to open in container (or manually select from the command palette).

_Using the DevContainer is optional, but it will get you the right version of dotnet + node, plus install all dependencies and setup a local SQL instance for you_
_The DevContainer also includes Azure Functions Core Tools v4 so you can run functions locally (`func`). See https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local._

3. **Start the application**

   ```bash
   npm start
   ```

   This command automatically installs dependencies (if needed) and starts both the .NET backend and Vite frontend with hot reload enabled.

   _Optional: If dependencies change, you can manually reinstall with `npm install && cd client && npm install && cd ..` but you shouldn't have to, the `npm start` should handle it._

4. **Access the application**

The application will auto launch in your browser (to http://localhost:5175).

If you want to access endpoints individually, you can do so at the following URLs:

- Frontend: http://localhost:5175
- Backend API: http://localhost:5166 (nothing to see, but /api/\* has the direct API endpoints)
- API Documentation (Swagger): http://localhost:5166/swagger/index.html
- Health check: http://localhost:5166/health

## PDF Processing (Ingest + Remediation)

At a high level, PDFs flow through:

1. Upload to Blob Storage
2. Queue/background processing (ingest worker)
3. Accessibility evaluation + (optional) auto-tagging via Adobe PDF Services
4. Remediation pass (metadata + tag-tree fixes)
5. Persist results for reporting

### What remediation currently does

- **PDF title**: extracts text from the first pages and writes a descriptive title into PDF metadata. If there isn't enough text, the existing title is kept (or a placeholder is used when missing).
- **Alt text (tagged PDFs only)**: fills missing `Alt` text for tagged `Figure` and `Link` elements, using AI when configured and a fallback otherwise.

### Database configuration

The backend requires a SQL Server connection string. By default `appsettings.Development.json` has a connection string configured for the local SQL Server instance.

When you want to specify your own DB connection, provide it by setting the `DB_CONNECTION` environment variable (for example in a `.env` file) or by updating `ConnectionStrings:DefaultConnection` in `appsettings.*.json` (`.env` is recommended)

### AI configuration (optional)

AI-backed remediation is enabled when `OPENAI_API_KEY` is set. If it is not set, the app uses local “sample” implementations (useful for development/tests).

- `OPENAI_API_KEY`: enables OpenAI-backed services
- `OPENAI_ALT_TEXT_MODEL`: model for image/link alt text generation (default: `gpt-4o-mini`)
- `OPENAI_PDF_TITLE_MODEL`: model for PDF title generation (default: `gpt-4o-mini`)

### Observability (OpenTelemetry / OTLP)

The Web API and the `function_ingest` worker are configured to export logs/traces/metrics via OTLP using standard `OTEL_*` environment variables.

- Web API: copy `server/.env.example` to `server/.env` and set `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_HEADERS` / `OTEL_SERVICE_NAME`.
- Azure Functions (local): set the same keys in `workers/function.ingest/local.settings.json` under `Values`.

### Auth Configuration

We use OIDC with Microsoft Entra ID (Azure AD) for authentication. The auth flow doesn't use any secrets and the settings in `appsettings.*.json` are sufficient for local development.

When you are ready to get your own, go to [Microsoft Entra ID](https://entra.microsoft.com/) and create a new application registration. Set the redirect url to `http://localhost:5166/signin-oidc` and check the box for "ID tokens".

You might also want to set the publisher domain to ucdavis.edu and fill in the other general branding info.

### Health check

The health check endpoint (`/health`) is configured to return the status of the application and its dependencies. It includes a database health check to ensure the SQL Server connection is healthy. See [Health Checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-9.0#entity-framework-core-dbcontext-probe).

## Development

### Backend Development

The backend is configured with hot reload via `dotnet watch`. Any changes to C# files will automatically restart the server.

### Frontend Development

The frontend uses Vite's hot module replacement (HMR). Changes to React components, TypeScript files, and CSS will be reflected immediately.

### Authentication Flow

1. Frontend routes requiring authentication redirect to the backend's login endpoint
2. Backend handles OIDC flow with Microsoft Entra ID
3. Upon successful authentication, a same-site cookie is set
4. Frontend API calls automatically include the authentication cookie
5. Backend validates the cookie for protected endpoints

## Testing

### Client tests

- Run `cd client && npm test` to execute the Vitest suite once.
- Use `npm run test:watch` inside `client/` for red/green feedback while you work.
- Tests run against a jsdom environment with Testing Library so you do not need the backend running.

### Server tests

- Run `dotnet test` from the repository root to execute the .NET test project included in `app.sln`.
- Alternatively, target the project directly with `dotnet test tests/server.tests/server.tests.csproj`.
- The tests use EF Core's in-memory provider (see `tests/server.tests/TestDbContextFactory.cs`) so no SQL Server instance is required.
- PDF remediation tests live under `tests/server.tests/Integration/Remediate/` and may use PDFs from `tests/server.tests/Fixtures/pdfs/` or generate simple PDFs on the fly.

## Updating Dependencies

### Client

- JavaScript/TypeScript packages: run `npm outdated` at the repository root and inside `client/` to see what can be updated. Use `npm update` in each location for compatible updates, or `npm install <package>@latest` when you need to jump to a new major version.
- After updating Node packages, reinstall if needed (`npm install`, `cd client && npm install`) and rerun key checks like `npm run lint`, `cd client && npm test`, and `dotnet test`.

### Server

.Net is a bit more complicated, but we're going to use the dotnet-outdated tool to help.

Run the following command from the repository root:

```
dotnet-outdated
```

and it'll show you a nice table of what can be updated. Be careful when updating major versions, especially with packages that are pinned to the .net version.

You can update individual packages or you can use the `--upgrade` flag to update all at once. Here's a nice way to do it and only update minor/patch versions:

```
dotnet-outdated --upgrade --version-lock Major
```

If you update `Microsoft.EntityFrameworkCore.Design` or another package that a tool depends on, you'll want to update that tool as well to match, ex: `dotnet tool update dotnet-ef --local --version 8.0.21`. That will update it for you but also set the value in our `dotnet-tools.json` so it's consistent for everyone.

And as always, after updating dependencies, make sure to run `dotnet build` and `dotnet test` to verify everything is working.

## Project Structure

```
├── client/ # React frontend
│   ├── src/
│   │   ├── routes/ # TanStack Router routes
│   │   ├── queries/ # TanStack Query hooks
│   │   ├── lib/ # API client and utilities
│   │   └── shared/ # Shared components
│   ├── package.json
│   └── vite.config.ts
├── server/ # .NET backend
│   ├── Controllers/ # API controllers
│   ├── Helpers/ # Utility classes
│   ├── Properties/ # Launch settings
│   ├── Program.cs # Application entry point
│   └── server.csproj
├── package.json # Root package.json with start script
└── app.sln # Visual Studio solution file
```

## Available Scripts

### Root Level

- `npm start` - Starts both backend and frontend with hot reload

### Client Directory

- `npm run dev` - Start Vite development server
- `npm run build` - Build for production
- `npm run lint` - Run ESLint
- `npm run preview` - Preview production build
- `npm test` - Run tests

### Server Directory

- `dotnet run` - Start the .NET application
- `dotnet watch` - Start with hot reload
- `dotnet build` - Build the application
- `dotnet test` - Run tests
