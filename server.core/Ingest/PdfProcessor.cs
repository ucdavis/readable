using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using server.core.Remediate;

namespace server.core.Ingest;

public interface IPdfProcessor
{
    Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken);
}

public sealed record PdfChunk(int Index, int FromPage, int ToPage, string Path)
{
    public int PageCount => ToPage - FromPage + 1;
}

public sealed class PdfProcessor : IPdfProcessor
{
    private readonly IAdobePdfServices _adobePdfServices;
    private readonly IPdfRemediationProcessor _pdfRemediationProcessor;
    private readonly PdfProcessorOptions _options;
    private readonly ILogger<PdfProcessor> _logger;

    public PdfProcessor(
        IAdobePdfServices adobePdfServices,
        IPdfRemediationProcessor pdfRemediationProcessor,
        IOptions<PdfProcessorOptions> options,
        ILogger<PdfProcessor> logger)
    {
        _adobePdfServices = adobePdfServices;
        _pdfRemediationProcessor = pdfRemediationProcessor;
        _options = options.Value;
        _logger = logger;

        if (_options.MaxPagesPerChunk <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_options.MaxPagesPerChunk),
                _options.MaxPagesPerChunk,
                "MaxPagesPerChunk must be > 0.");
        }
    }

    public async Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
    {
        var safeFileId = SanitizeForFileName(fileId);
        var workDir = GetWorkDir(safeFileId, _options.WorkDirRoot);
        Directory.CreateDirectory(workDir);

        // persist the incoming stream locally so we can reliably split it.
        var sourcePath = Path.Combine(workDir, $"{safeFileId}.source.pdf");
        await using (var sourceFile = File.Create(sourcePath))
        {
            await pdfStream.CopyToAsync(sourceFile, cancellationToken);
        }

        // 1) Split the incoming PDF stream into chunks of <= 200 pages each.
        //    - Write each chunk to a temp file under `/tmp` (e.g. `/tmp/{fileId}.partNNN.pdf`).
        //    - Keep an ordered list of the chunk file paths (and any per-chunk metadata).

        var chunks = SplitIntoChunks(sourcePath, workDir, safeFileId, _options.MaxPagesPerChunk, cancellationToken);

        _logger.LogInformation(
            "Split {fileId} into {chunkCount} chunk(s) in {workDir}",
            fileId,
            chunks.Count,
            workDir);

        // 2. TODO (maybe) - get an a10y report for the 'before' step

        // 3. Autotag each chunk via Adobe PDF Services.
        var taggedChunkPaths = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var taggedPath = Path.Combine(workDir, $"{safeFileId}.part{chunk.Index + 1:000}.tagged.pdf");
            var reportPath = Path.Combine(workDir, $"{safeFileId}.part{chunk.Index + 1:000}.autotag-report.xlsx");

            await _adobePdfServices.AutotagPdfAsync(
                inputPdfPath: chunk.Path,
                outputTaggedPdfPath: taggedPath,
                outputTaggingReportPath: reportPath,
                cancellationToken: cancellationToken);

            taggedChunkPaths.Add(taggedPath);
        }

        // 4. Merge all tagged chunk PDFs back into a single tagged PDF.
        var mergedTaggedPath = Path.Combine(workDir, $"{safeFileId}.tagged.pdf");
        MergePdfsInOrder(taggedChunkPaths, mergedTaggedPath, cancellationToken);

        _logger.LogInformation("Merged tagged PDF written to {path}", mergedTaggedPath);

        // 5. Post-process the merged tagged PDF:
        //    - Walk the PDF structure by page to find images and links; add/repair alt text where missing.
        //    - Infer/set a document title.
        //    - Optional: build/insert a TOC.
        var remediatedPdfPath = Path.Combine(workDir, $"{safeFileId}.remediated.pdf");
        var remediation = await _pdfRemediationProcessor.ProcessAsync(
            fileId,
            mergedTaggedPath,
            remediatedPdfPath,
            cancellationToken);
        var finalPdfPath = remediation.OutputPdfPath;

        _logger.LogInformation("Remediated PDF written to {path}", finalPdfPath);

        // TODO: Generate a final a11y report on the remediated PDF (PDFAccessibilityCheckerJob) and persist JSON to DB.
        // TODO: Merge/tagging reports, upload to `processed/`, and update DB status + artifact URIs.
    }

    private static List<PdfChunk> SplitIntoChunks(
        string sourcePath,
        string workDir,
        string safeFileId,
        int maxPagesPerChunk,
        CancellationToken cancellationToken)
    {
        using var src = new PdfDocument(new PdfReader(sourcePath));
        var totalPages = src.GetNumberOfPages();

        var chunkCount = (int)Math.Ceiling(totalPages / (double)maxPagesPerChunk);
        var chunks = new List<PdfChunk>(capacity: Math.Max(1, chunkCount));

        var chunkIndex = 0;
        for (var fromPage = 1; fromPage <= totalPages; fromPage += maxPagesPerChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toPage = Math.Min(totalPages, fromPage + maxPagesPerChunk - 1);
            var chunkPath = Path.Combine(workDir, $"{safeFileId}.part{chunkIndex + 1:000}.pdf");

            using (var dest = new PdfDocument(new PdfWriter(chunkPath)))
            {
                src.CopyPagesTo(fromPage, toPage, dest);
            }

            chunks.Add(new PdfChunk(chunkIndex, fromPage, toPage, chunkPath));
            chunkIndex++;
        }

        return chunks;
    }

    private static string GetWorkDir(string safeFileId, string? workDirRoot)
    {
        // Prefer `/tmp`; fall back to platform temp if unavailable.
        var baseTmp =
            string.IsNullOrWhiteSpace(workDirRoot)
                ? (Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                : workDirRoot.TrimEnd(Path.DirectorySeparatorChar);

        return Path.Combine(baseTmp, "readable-ingest", safeFileId);
    }

    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "file";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return sb.ToString().Trim();
    }

    private static void MergePdfsInOrder(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var dest = new PdfDocument(new PdfWriter(outputPath));
        var merger = new PdfMerger(dest);

        foreach (var inputPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var src = new PdfDocument(new PdfReader(inputPath));
            merger.Merge(src, 1, src.GetNumberOfPages());
        }
    }
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
