import { useUser } from '@/shared/auth/UserContext.tsx';
import { createFileRoute, Link } from '@tanstack/react-router';

export const Route = createFileRoute('/(authenticated)/')({
  component: RouteComponent,
});

function RouteComponent() {
  const user = useUser();
  return (
    <div className="min-h-screen bg-gradient-to-br from-base-100 to-base-200">
      <div className="container mx-auto max-w-6xl px-4 py-10 space-y-10">
        <header className="text-center space-y-5">
          <img alt="CAES" className="mx-auto h-16 w-auto" src="/caes.svg" />

          <div className="space-y-2">
            <div className="flex flex-wrap items-center justify-center gap-3">
              <h1 className="text-4xl sm:text-5xl font-extrabold tracking-tight">
                Readable
              </h1>
              <span className="badge badge-warning badge-outline">
                Beta / testing
              </span>
            </div>
            <p className="mx-auto max-w-2xl text-base-content/70 text-lg">
              Hi {user.name}. Upload a PDF and get back a remediated copy (title
              metadata, and alt text when possible).
            </p>
          </div>

          <div className="space-y-2">
            <Link className="btn btn-primary btn-lg w-full sm:w-auto" to="/upload">
              Upload a PDF
            </Link>
            <p className="text-sm text-base-content/60">
              This is a best-effort pipeline right now; results may change as we
              iterate.
            </p>
          </div>
        </header>

        <div className="alert alert-info">
          <div className="flex flex-col gap-1">
            <div className="font-semibold">What this app does</div>
            <div className="text-sm text-base-content/80">
              It ingests PDFs on the server and writes an output PDF that
              improves accessibility metadata. Title remediation runs for all
              PDFs; alt text remediation runs for tagged PDFs.
            </div>
          </div>
        </div>

        <section className="grid gap-6 md:grid-cols-3">
          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">How it works</h2>
              <ul className="steps steps-vertical">
                <li className="step step-primary">Upload a PDF</li>
                <li className="step step-primary">Server ingests + remediates</li>
                <li className="step step-primary">Download the output PDF</li>
              </ul>
              <div className="mt-4">
                <Link className="btn btn-primary w-full" to="/upload">
                  Go to Upload
                </Link>
              </div>
            </div>
          </div>

          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">Project basics</h2>
              <div className="space-y-3 text-sm text-base-content/70">
                <div>
                  <div className="font-medium text-base-content">Frontend</div>
                  <div>
                    React + TypeScript (Vite) with TanStack Router and TanStack
                    Query.
                  </div>
                </div>
                <div>
                  <div className="font-medium text-base-content">Backend</div>
                  <div>ASP.NET Core 8 Web API under <code className="font-mono">server/</code>.</div>
                </div>
                <div>
                  <div className="font-medium text-base-content">Code map</div>
                  <div className="space-y-1">
                    <div>
                      <code className="font-mono">client/</code>: UI, routes, queries
                    </div>
                    <div>
                      <code className="font-mono">server.core/</code>: ingest + PDF remediation pipeline
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">Quick links</h2>
              <div className="grid gap-3">
                <Link className="btn btn-primary" to="/upload">
                  Upload
                </Link>
                <Link className="btn btn-outline" to="/styles">
                  Style guide
                </Link>
                <Link className="btn btn-outline" to="/fetch">
                  Table demo
                </Link>
                <Link className="btn btn-outline" to="/form">
                  Form demo
                </Link>
                <Link className="btn btn-ghost" to="/about">
                  About (public)
                </Link>
              </div>
            </div>
          </div>
        </section>

        <section className="grid gap-6 md:grid-cols-2">
          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">Remediation notes</h2>
              <div className="space-y-3 text-sm text-base-content/70">
                <div>
                  <div className="font-medium text-base-content">Title</div>
                  <div>
                    The server extracts some early text and writes a document
                    title into PDF metadata.
                  </div>
                </div>
                <div>
                  <div className="font-medium text-base-content">Alt text</div>
                  <div>
                    For tagged PDFs, the server fills missing <code className="font-mono">Alt</code> on
                    figures and links.
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <h2 className="card-title">Optional AI</h2>
              <p className="text-sm text-base-content/70">
                If <code className="font-mono">OPENAI_API_KEY</code> is set, the server can use an
                OpenAI model to generate titles and alt text; otherwise it uses
                local sample services.
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <span className="badge badge-outline">OPENAI_API_KEY</span>
                <span className="badge badge-outline">OPENAI_ALT_TEXT_MODEL</span>
                <span className="badge badge-outline">OPENAI_PDF_TITLE_MODEL</span>
              </div>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
}
