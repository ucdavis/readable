namespace server.core.Ingest;

public interface IPdfProcessor
{
    Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken);
}

public sealed class NoopPdfProcessor : IPdfProcessor
{
    public Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
    {
        // TODO: Implement PDF ingest processing pipeline:
        // TODO: 1) Split the incoming PDF stream into chunks of <= 200 pages each.
        // TODO:    - Write each chunk to a temp file under `/tmp` (e.g. `/tmp/{fileId}.partNNN.pdf`).
        // TODO:    - Keep an ordered list of the chunk file paths (and any per-chunk metadata).
        // TODO: 2) For each chunk (in order):
        // TODO:    - Call the PDF services to auto-tag (expect: tagged PDF + a report).
        // TODO:    - Persist the returned tagged PDF and report to temp files under `/tmp`.
        // TODO: 3) Merge all auto-tagged chunk PDFs (in original order) into a single tagged PDF.
        // TODO:    - Write the merged PDF to `/tmp/{fileId}.tagged.pdf` (this is the primary artifact we care about).
        // TODO: 4) Merge the per-chunk reports into a single combined report artifact (define format/structure).
        // TODO: 5) Post-process the merged tagged PDF:
        // TODO:    - Walk the PDF structure to find images and links; add/repair alt text where missing.
        // TODO:    - Optionally infer and set a document title (from metadata or first page heading).
        // TODO:    - Optionally generate/insert a Table of Contents (TOC) if the structure supports it.
        // TODO: 6) Generate a new accessibility (a11y) report for the final merged PDF.
        // TODO: 7) Upload final artifacts:
        // TODO:    - Upload the final tagged PDF to the `processed/` folder in storage.
        // TODO:    - Upload/store the combined reports as needed (original autotag report + final a11y report).
        // TODO: 8) Update DB:
        // TODO:    - Mark ingest status transitions (processing -> completed/failed).
        // TODO:    - Store references to uploaded artifacts (URIs) and report outputs.
        // TODO: 9) Cleanup:
        // TODO:    - Delete temp files under `/tmp` (best-effort) on success/failure.

        return Task.CompletedTask;
    }
}
