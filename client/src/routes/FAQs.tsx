import {
  ArrowUpTrayIcon,
  Bars3BottomLeftIcon,
  CheckBadgeIcon,
  CheckCircleIcon,
  ClipboardDocumentCheckIcon,
  DocumentMagnifyingGlassIcon,
  ExclamationTriangleIcon,
  SparklesIcon,
} from '@heroicons/react/24/outline';
import { Link, createFileRoute } from '@tanstack/react-router';
import type { ReactNode } from 'react';

export const Route = createFileRoute('/FAQs')({
  component: FAQs,
});

type FaqItemProps = {
  children: ReactNode;
  defaultOpen?: boolean;
  question: string;
};

function FaqItem({ children, defaultOpen = false, question }: FaqItemProps) {
  return (
    <div className="collapse collapse-arrow bg-base-100 shadow border border-base-300">
      <input defaultChecked={defaultOpen} type="checkbox" />
      <div className="collapse-title text-base font-semibold">{question}</div>
      <div className="collapse-content text-base-content/80">{children}</div>
    </div>
  );
}

function FAQs() {
  return (
    <div className="container pb-12">
      <div className="mx-auto max-w-5xl">
        <header className="py-6">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div>
              <h1 className="mt-3 text-3xl sm:text-4xl font-extrabold tracking-tight">
                How it works
              </h1>
              <p className="mt-3 max-w-prose text-base sm:text-lg">
                Most PDFs look fine—but accessibility depends on structure.
                Readable helps you get dramatically closer to WCAG + PDF/UA
                expectations without doing everything by hand.
              </p>
            </div>

            <div className="flex flex-wrap gap-2">
              <Link className="btn btn-primary" to="/">
                Upload a PDF
              </Link>
              <a className="btn btn-ghost" href="#faq">
                Jump to FAQs
              </a>
            </div>
          </div>
        </header>

        <section
          aria-labelledby="overview"
          className="grid gap-4 lg:grid-cols-2"
        >
          <div className="card shadow bg-base-100">
            <div className="card-body text-base sm:text-lg leading-relaxed">
              <h2 className="card-title" id="overview">
                The core issue
              </h2>
              <p className="text-base-content/80">
                Most PDFs are visually readable but structurally inaccessible.
                They lack proper headings, document structure, reading order,
                and descriptive text for images and links—features required by
                accessibility regulations and essential for screen readers and
                assistive technologies.
              </p>
            </div>
          </div>

          <div className="card shadow bg-base-100">
            <div className="card-body text-base sm:text-lg leading-relaxed">
              <h2 className="card-title">What Readable does automatically</h2>
              <ul className="mt-2 space-y-3">
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Runs an accessibility check before and after processing
                  </span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Auto-tags or re-tags PDFs when needed (Adobe AutoTag) so
                    assistive technology can navigate the structure
                  </span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Fixes key metadata (Title shown in the title bar, primary
                    language, and tagged-PDF tab order)
                  </span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Fills missing alt text for tagged figures (and optionally
                    links), adds table summaries, and generates bookmarks when
                    possible
                  </span>
                </li>
              </ul>

              <div className="mt-5 alert bg-base-200 border border-base-300 text-base sm:text-lg">
                <CheckBadgeIcon aria-hidden="true" className="h-5 w-5" />
                <span className="text-base-content/80">
                  The result is a PDF that is dramatically closer to full
                  accessibility compliance—saving time, reducing manual
                  remediation effort, and clearly showing what still needs
                  manual review.
                </span>
              </div>
            </div>
          </div>
        </section>

        <section aria-labelledby="how" className="mt-10">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="text-2xl font-extrabold" id="how">
                How Readable works
              </h2>
              <p className="mt-1 text-base-content/70 max-w-prose">
                A simple workflow, with accessibility-focused output.
              </p>
            </div>
            <div className="text-sm text-base-content/70">
              From upload → to remediated PDF
            </div>
          </div>

          <div className="mt-5 card shadow bg-base-100">
            <div className="card-body text-base sm:text-lg leading-relaxed">
              <ul className="timeline timeline-snap-icon max-md:timeline-compact timeline-vertical">
                <li>
                  <div className="timeline-middle">
                    <ArrowUpTrayIcon
                      aria-hidden="true"
                      className="h-5 w-5 text-primary"
                    />
                  </div>
                  <div className="timeline-start md:text-end timeline-box text-base sm:text-lg leading-relaxed">
                    <div className="text-sm font-mono text-base-content/60">
                      Step 1
                    </div>
                    <div className="text-lg sm:text-xl font-black">
                      Upload your PDF
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Drop a file in and Readable begins inspecting pages,
                      layout, and content.
                    </p>
                  </div>
                  <hr className="bg-base-300" />
                </li>

                <li>
                  <hr className="bg-base-300" />
                  <div className="timeline-middle">
                    <DocumentMagnifyingGlassIcon
                      aria-hidden="true"
                      className="h-5 w-5 text-primary"
                    />
                  </div>
                  <div className="timeline-end timeline-box text-base sm:text-lg leading-relaxed">
                    <div className="text-sm font-mono text-base-content/60">
                      Step 2
                    </div>
                    <div className="text-lg sm:text-xl font-black">
                      Check accessibility baseline
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Runs an accessibility checker report so you can see what
                      passed, what failed, and what needs manual verification.
                    </p>
                  </div>
                  <hr className="bg-base-300" />
                </li>

                <li>
                  <hr className="bg-base-300" />
                  <div className="timeline-middle">
                    <Bars3BottomLeftIcon
                      aria-hidden="true"
                      className="h-5 w-5 text-primary"
                    />
                  </div>
                  <div className="timeline-start md:text-end timeline-box text-base sm:text-lg leading-relaxed">
                    <div className="text-sm font-mono text-base-content/60">
                      Step 3
                    </div>
                    <div className="text-lg sm:text-xl font-black">
                      Auto-tag (when needed)
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Uses Adobe AutoTag to create or repair the tag tree so
                      screen readers can navigate the document structure.
                    </p>
                  </div>
                  <hr className="bg-base-300" />
                </li>

                <li>
                  <hr className="bg-base-300" />
                  <div className="timeline-middle">
                    <SparklesIcon
                      aria-hidden="true"
                      className="h-5 w-5 text-primary"
                    />
                  </div>
                  <div className="timeline-end timeline-box text-base sm:text-lg leading-relaxed">
                    <div className="text-sm font-mono text-base-content/60">
                      Step 4
                    </div>
                    <div className="text-lg sm:text-xl font-black">
                      Apply targeted fixes
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Sets Title + primary language, improves tagged-PDF tab
                      order, generates bookmarks, adds table summaries, and
                      drafts alt text for tagged figures (AI-assisted).
                    </p>
                  </div>
                  <hr className="bg-base-300" />
                </li>

                <li>
                  <hr className="bg-base-300" />
                  <div className="timeline-middle">
                    <ClipboardDocumentCheckIcon
                      aria-hidden="true"
                      className="h-5 w-5 text-primary"
                    />
                  </div>
                  <div className="timeline-start md:text-end timeline-box text-base sm:text-lg leading-relaxed">
                    <div className="text-sm font-mono text-base-content/60">
                      Step 5
                    </div>
                    <div className="text-lg sm:text-xl font-black">
                      Deliver output + report
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Get an updated PDF plus a clear breakdown of what was
                      improved and what still needs review.
                    </p>
                  </div>
                </li>
              </ul>
            </div>
          </div>
        </section>

        <section aria-labelledby="what-you-get" className="mt-10">
          <h2 className="text-2xl font-extrabold" id="what-you-get">
            What you get
          </h2>
          <div className="mt-4 grid gap-4 sm:grid-cols-2">
            <div className="card shadow bg-base-100">
              <div className="card-body text-base sm:text-lg leading-relaxed">
                <div className="flex items-start gap-3">
                  <Bars3BottomLeftIcon
                    aria-hidden="true"
                    className="h-6 w-6 flex-none text-primary mt-0.5"
                  />
                  <div>
                    <h3 className="font-black text-lg sm:text-xl">
                      Tags + structure
                    </h3>
                    <p className="mt-1 text-base-content/70">
                      A tagged PDF structure that assistive technology can
                      navigate (when tagging is possible).
                    </p>
                  </div>
                </div>
              </div>
            </div>

            <div className="card shadow bg-base-100">
              <div className="card-body text-base sm:text-lg leading-relaxed">
                <div className="flex items-start gap-3">
                  <SparklesIcon
                    aria-hidden="true"
                    className="h-6 w-6 flex-none text-primary mt-0.5"
                  />
                  <div>
                    <h3 className="font-black text-lg sm:text-xl">
                      Descriptions that help
                    </h3>
                    <p className="mt-1 text-base-content/70">
                      Context-aware alt text for tagged figures (and optionally
                      links), drafted to be useful instead of generic.
                    </p>
                  </div>
                </div>
              </div>
            </div>

            <div className="card shadow bg-base-100">
              <div className="card-body text-base sm:text-lg leading-relaxed">
                <div className="flex items-start gap-3">
                  <CheckBadgeIcon
                    aria-hidden="true"
                    className="h-6 w-6 flex-none text-primary mt-0.5"
                  />
                  <div>
                    <h3 className="font-black text-lg sm:text-xl">
                      Better document metadata
                    </h3>
                    <p className="mt-1 text-base-content/70">
                      Title shown in the title bar, primary language, and
                      tagged-PDF tab order—plus bookmarks when possible.
                    </p>
                  </div>
                </div>
              </div>
            </div>

            <div className="card shadow bg-base-100">
              <div className="card-body text-base sm:text-lg leading-relaxed">
                <div className="flex items-start gap-3">
                  <ClipboardDocumentCheckIcon
                    aria-hidden="true"
                    className="h-6 w-6 flex-none text-primary mt-0.5"
                  />
                  <div>
                    <h3 className="font-black text-lg sm:text-xl">
                      A clear remediation report
                    </h3>
                    <p className="mt-1 text-base-content/70">
                      Before/after checker reports showing what changed and
                      what still needs manual verification.
                    </p>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <div className="mt-4 alert border border-base-300 bg-base-200 text-base sm:text-lg">
            <ExclamationTriangleIcon aria-hidden="true" className="h-5 w-5" />
            <span className="text-base-content/80">
              Some Acrobat checker items are inherently manual (like logical
              reading order, color contrast, and complex tables). Readable
              focuses on high-confidence fixes and makes the remaining work
              obvious.
            </span>
          </div>
        </section>

        <section aria-labelledby="faq" className="mt-12">
          <h2 className="text-2xl font-extrabold" id="faq">
            FAQs
          </h2>

          <div className="mt-4 space-y-3">
            <FaqItem
              defaultOpen
              question="Why does accessibility matter for PDFs?"
            >
              <p>
                Accessibility regulations such as WCAG, Section 508, and PDF/UA
                require digital documents to be usable by people with
                disabilities. Many PDFs look correct visually, but fail
                accessibility checks because they lack:
              </p>
              <ul className="mt-3 list-disc space-y-2 pl-5">
                <li>Semantic structure (headings, sections, lists)</li>
                <li>Logical reading order</li>
                <li>Text alternatives for images and graphics</li>
                <li>
                  Meaningful descriptions for links and interactive content
                </li>
              </ul>
              <p className="mt-3">
                Without these elements, screen readers can’t interpret the
                document correctly—making it inaccessible to many users.
              </p>
            </FaqItem>

            <FaqItem
              question="What can Readable fix automatically vs. what still needs manual work?"
            >
              <p>
                Think of Readable as a combination of automated tagging plus a
                set of targeted “Fix” actions (similar to what Acrobat can do
                quickly), followed by a report that highlights what remains.
              </p>

              <div className="mt-4 grid gap-4 md:grid-cols-2">
                <div className="rounded-box border border-base-300 bg-base-200 p-4">
                  <h3 className="font-black">Typically handled automatically</h3>
                  <ul className="mt-3 space-y-2">
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Auto-tagging / re-tagging untagged or broken PDFs (Adobe
                        AutoTag)
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Title metadata (and “show title in title bar”) + primary
                        language
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Tagged-PDF tab order set to “Use Document Structure”
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Missing alt text for tagged figures (AI-assisted; vector
                        figures supported). Optional link alt text.
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Bookmarks (outlines) generated when the tag structure
                        supports it
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Table summaries, and demotion of likely layout tables to
                        reduce false failures
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <CheckCircleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-success mt-0.5"
                      />
                      <span>
                        Cleanup of untagged annotations (may remove them if they
                        can’t be associated with the structure tree)
                      </span>
                    </li>
                  </ul>
                </div>

                <div className="rounded-box border border-base-300 bg-base-200 p-4">
                  <h3 className="font-black">Usually manual (Acrobat / source)</h3>
                  <ul className="mt-3 space-y-2">
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>Logical reading order verification and fixes</span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>Color contrast and other visual design issues</span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>
                        Complex tables (true headers, TR/TH/TD structure,
                        spanning cells, regularity)
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>
                        Lists + heading hierarchy issues that require editorial
                        judgment
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>
                        OCR/text recognition for scanned or image-only PDFs
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>
                        Content QA (decorative vs. meaningful images, accuracy
                        of alt text)
                      </span>
                    </li>
                    <li className="flex gap-3">
                      <ExclamationTriangleIcon
                        aria-hidden="true"
                        className="h-5 w-5 flex-none text-warning mt-0.5"
                      />
                      <span>
                        Security/permission flags and other locked-PDF settings
                      </span>
                    </li>
                  </ul>
                </div>
              </div>

              <p className="mt-3 text-sm text-base-content/70">
                Note: Some fixes only apply to tagged PDFs. If a document can’t
                be reliably tagged, Readable may still improve metadata (like
                Title and Language) but can’t fully remediate structure-based
                issues.
              </p>
            </FaqItem>

            <FaqItem question="Does Readable guarantee full compliance?">
              <p>
                No tool can guarantee full compliance in every case. Readable is
                designed to get you dramatically closer, then clearly show what
                still needs human review (especially for complex layouts,
                tables, and meaning-dependent images).
              </p>
              <p className="mt-3">
                In Acrobat terms: Readable can automate many “Fix” items, but
                manual checks (like reading order and contrast) and nuanced
                structural edits still require a human.
              </p>
            </FaqItem>

            <FaqItem question="What kinds of PDFs work best?">
              <p>
                Text-based PDFs with real selectable text are typically the best
                candidates. If your PDF is scanned (image-only), it may require
                OCR before it can be meaningfully remediated.
              </p>
              <p className="mt-3">
                Many of Readable’s fixes depend on the document being tagged (or
                being taggable). If the tag tree is missing or broken, the
                pipeline will typically re-tag the PDF before applying targeted
                fixes.
              </p>
            </FaqItem>

            <FaqItem question="How is AI used?">
              <p>
                AI is used to draft clear, context-aware alt text for tagged
                figures (and optionally links), and to suggest a reasonable
                document title when there’s enough real text to infer one.
              </p>
              <p className="mt-3">
                AI output should still be reviewed—especially for charts,
                diagrams, and meaning-dependent images.
              </p>
            </FaqItem>

            <FaqItem question="What do I receive after processing?">
              <p>
                You’ll get an updated PDF plus a report showing what Readable
                improved automatically and what may still require manual fixes.
              </p>
            </FaqItem>

            <FaqItem question="Who is Readable for?">
              <p>
                Readable is designed for UC Davis by the College of
                Agricultural and Environmental Sciences Dean&apos;s Office @ UC
                Davis
              </p>
            </FaqItem>

            <div className="card shadow bg-base-100">
              <div className="card-body">
                <h3 className="card-title">Want more detail?</h3>
                <p className="text-base-content/80">
                  Start with the{' '}
                  <a
                    className="link"
                    href="https://digitalaccessibility.ucop.edu/index.html"
                    rel="noopener noreferrer"
                    target="_blank"
                  >
                    UCOP Digital Accessibility guidelines
                  </a>{' '}
                  for a practical overview of WCAG and PDF expectations.
                </p>
              </div>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
}
