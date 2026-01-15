import { createFileRoute } from '@tanstack/react-router';

export const Route = createFileRoute('/FAQs')({
  component: FAQs,
});

function FAQs() {
  return (
    <div className="container">
      <div className="max-w-prose">
        <p>
          Most PDFs are visually readable but structurally inaccessible. They
          lack proper headings, document structure, reading order, and
          descriptive text for images and links—features required by
          accessibility regulations and essential for screen readers and
          assistive technologies.
        </p>

        <p className="font-semibold text-base-content">
          Readable automatically analyzes your PDF and:
        </p>

        <ul className="list-disc space-y-2 pl-5">
          <li>Detects and adds document structure and hierarchy</li>
          <li>Identifies headings, lists, tables, and reading order</li>
          <li>
            Generates clear, context-aware descriptions for images and links
            using AI
          </li>
          <li>Flags remaining accessibility issues in a detailed report</li>
        </ul>

        <p>
          The result is a PDF that is dramatically closer to full accessibility
          compliance—saving time, reducing manual remediation effort, and
          helping ensure your documents are usable by everyone.
        </p>

        <hr className="border-base-content/10" />

        <div className="space-y-6">
          <h2 className="text-xl font-bold text-base-content">
            About Readable
          </h2>

          <div className="space-y-3">
            <h3 className="text-lg font-semibold text-base-content">
              Why Accessibility Matters
            </h3>

            <p>
              Accessibility regulations such as WCAG, Section 508, and PDF/UA
              require digital documents to be usable by people with
              disabilities. While many PDFs look correct visually, most fail
              accessibility checks because they lack:
            </p>

            <ul className="list-disc space-y-2 pl-5">
              <li>Semantic structure (headings, sections, lists)</li>
              <li>Logical reading order</li>
              <li>Text alternatives for images and graphics</li>
              <li>Meaningful descriptions for links and interactive content</li>
            </ul>

            <p>
              Without these elements, screen readers cannot interpret the
              document correctly—making it inaccessible to many users.
            </p>
          </div>

          <hr className="border-base-content/10" />

          <div className="space-y-3">
            <h3 className="text-lg font-semibold text-base-content">
              How Readable Works
            </h3>

            <p>
              Readable bridges the gap between visually correct PDFs and truly
              accessible documents. When you upload a PDF, Readable
              automatically:
            </p>

            <ol className="list-decimal space-y-4 pl-5">
              <li>
                <div className="font-semibold text-base-content">
                  Analyzes the document structure
                </div>
                <p className="mt-1">
                  It detects headings, paragraphs, lists, tables, and layout
                  patterns to establish a logical hierarchy and reading order.
                </p>
              </li>

              <li>
                <div className="font-semibold text-base-content">
                  Adds accessibility metadata
                </div>
                <p className="mt-1">
                  The PDF is enhanced with structural tags, titles, and
                  navigation elements required by accessibility standards.
                </p>
              </li>

              <li>
                <div className="font-semibold text-base-content">
                  Generates intelligent descriptions using AI
                </div>
                <p className="mt-1">
                  Readable uses AI to create useful, context-aware descriptions
                  for:
                </p>
                <ul className="mt-2 list-disc space-y-2 pl-5">
                  <li>Images and figures</li>
                  <li>Links and references</li>
                  <li>Complex content blocks</li>
                </ul>
                <p className="mt-2">
                  These descriptions are designed to be helpful—not generic—so
                  assistive technology users get meaningful information.
                </p>
              </li>

              <li>
                <div className="font-semibold text-base-content">
                  Produces an accessibility report
                </div>
                <p className="mt-1">
                  After processing, Readable provides a report highlighting:
                </p>
                <ul className="mt-2 list-disc space-y-2 pl-5">
                  <li>What was automatically fixed</li>
                  <li>Remaining issues that may require manual review</li>
                  <li>
                    Recommendations for achieving higher compliance levels
                  </li>
                </ul>
              </li>
            </ol>
          </div>

          <hr className="border-base-content/10" />

          <div className="space-y-3">
            <h3 className="text-lg font-semibold text-base-content">
              What You Get
            </h3>

            <ul className="list-disc space-y-2 pl-5">
              <li>Significantly improved WCAG and PDF/UA compliance</li>
              <li>Dramatically reduced remediation time</li>
              <li>Clear visibility into remaining accessibility gaps</li>
              <li>A practical balance of automation and human review</li>
            </ul>

            <p>
              Readable doesn’t claim to replace human judgment entirely—but it
              does eliminate the most time-consuming and technical parts of PDF
              accessibility work.
            </p>
          </div>

          <hr className="border-base-content/10" />

          <div className="space-y-3">
            <h3 className="text-lg font-semibold text-base-content">
              Who Readable Is For
            </h3>

            <p>Readable is designed for:</p>

            <ul className="list-disc space-y-2 pl-5">
              <li>Universities and research institutions</li>
              <li>Government and public-sector organizations</li>
              <li>Companies publishing reports, manuals, or forms</li>
              <li>
                Teams responsible for compliance, accessibility, or digital
                content
              </li>
            </ul>

            <p>
              If you produce PDFs, Readable helps ensure they’re usable by
              everyone.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
