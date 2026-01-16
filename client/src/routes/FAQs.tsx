import {
  ArrowUpTrayIcon,
  Bars3BottomLeftIcon,
  CheckBadgeIcon,
  CheckCircleIcon,
  ClipboardDocumentCheckIcon,
  DocumentMagnifyingGlassIcon,
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
              <div className="badge badge-secondary badge-outline">
                PDF Accessibility
              </div>
              <h1 className="mt-3 text-3xl sm:text-4xl font-extrabold tracking-tight">
                How it works
              </h1>
              <p className="mt-3 max-w-prose text-base sm:text-lg text-base-content/70">
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

        <section aria-labelledby="overview" className="grid gap-4 lg:grid-cols-2">
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
                  <span>Detects and adds document structure and hierarchy</span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Identifies headings, lists, tables, and reading order
                  </span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>
                    Generates clear, context-aware descriptions for images and
                    links using AI
                  </span>
                </li>
                <li className="flex gap-3">
                  <CheckCircleIcon
                    aria-hidden="true"
                    className="h-5 w-5 flex-none text-success mt-0.5"
                  />
                  <span>Flags remaining accessibility issues in a report</span>
                </li>
              </ul>

              <div className="mt-5 alert bg-base-200 border border-base-300 text-base sm:text-lg">
                <CheckBadgeIcon aria-hidden="true" className="h-5 w-5" />
                <span className="text-base-content/80">
                  The result is a PDF that is dramatically closer to full
                  accessibility compliance—saving time, reducing manual
                  remediation effort, and helping ensure your documents are
                  usable by everyone.
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
                      Analyze structure + reading order
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Detects headings, paragraphs, lists, tables, and a
                      sensible order for assistive technology.
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
                      Add tags + metadata
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Applies structure and accessibility metadata so the PDF
                      has meaningful navigation and semantics.
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
                      Generate useful descriptions
                    </div>
                    <p className="mt-1 text-base-content/80">
                      Uses AI to draft context-aware alt text for images and
                      links (so it’s helpful—not generic).
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
                      Tags + reading order
                    </h3>
                    <p className="mt-1 text-base-content/70">
                      Headings, lists, tables, and hierarchy so assistive
                      technology can navigate the document.
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
                      Context-aware alt text for images and links, drafted to
                      be useful instead of generic.
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
                      Improved document info that supports navigation,
                      discoverability, and accessibility expectations.
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
                      What was fixed automatically, what remains, and where to
                      focus manual review for compliance.
                    </p>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </section>

        <section aria-labelledby="faq" className="mt-12">
          <h2 className="text-2xl font-extrabold" id="faq">
            FAQs
          </h2>

          <div className="mt-4 space-y-3">
            <FaqItem defaultOpen question="Why does accessibility matter for PDFs?">
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
                <li>Meaningful descriptions for links and interactive content</li>
              </ul>
              <p className="mt-3">
                Without these elements, screen readers can’t interpret the
                document correctly—making it inaccessible to many users.
              </p>
            </FaqItem>

            <FaqItem question="Does Readable guarantee full compliance?">
              <p>
                No tool can guarantee full compliance in every case. Readable
                is designed to get you dramatically closer, then clearly show
                what still needs human review (especially for complex layouts,
                tables, and meaning-dependent images).
              </p>
            </FaqItem>

            <FaqItem question="What kinds of PDFs work best?">
              <p>
                Text-based PDFs with real selectable text are typically the best
                candidates. If your PDF is scanned (image-only), it may require
                OCR before it can be meaningfully remediated.
              </p>
            </FaqItem>

            <FaqItem question="How is AI used?">
              <p>
                AI is used to draft clear, context-aware descriptions for images
                and links. The goal is helpful descriptions that reflect the
                surrounding content, not generic alt text.
              </p>
            </FaqItem>

            <FaqItem question="What do I receive after processing?">
              <p>
                You’ll get an updated PDF plus a report showing what Readable
                improved automatically and what may still require manual fixes.
              </p>
            </FaqItem>

            <FaqItem question="Who is Readable for?">
              <p>Readable is designed for teams that publish PDFs, including:</p>
              <ul className="mt-3 list-disc space-y-2 pl-5">
                <li>Universities and research institutions</li>
                <li>Government and public-sector organizations</li>
                <li>Companies publishing reports, manuals, or forms</li>
                <li>Teams responsible for compliance and digital content</li>
              </ul>
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
