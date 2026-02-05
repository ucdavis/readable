using System.Diagnostics;
using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using server.core.Remediate;
using server.core.Telemetry;

namespace server.core.Ingest;

public interface IPdfProcessor
{
    Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken);
}

public sealed record PdfProcessResult(
    string OutputPdfPath,
    string? BeforeAccessibilityReportJson = null,
    string? BeforeAccessibilityReportPath = null,
    string? AfterAccessibilityReportJson = null,
    string? AfterAccessibilityReportPath = null,
    int PageCount = 0);

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

    /// <summary>
    /// Runs the PDF ingest pipeline: chunking, autotagging, merging, and remediation.
    /// </summary>
    /// <remarks>
    /// This method writes intermediate artifacts to a per-file working directory under <c>/tmp</c> (or a configured
    /// root), and expects <see cref="IAdobePdfServices" /> to produce tagged PDFs for each chunk.
    /// </remarks>
    public async Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var safeFileId = SanitizeForFileName(fileId);
        var workDir = GetWorkDir(safeFileId, _options.WorkDirRoot);
        Directory.CreateDirectory(workDir);

        // persist the incoming stream locally so we can reliably split it.
        var sourcePath = Path.Combine(workDir, $"{safeFileId}.source.pdf");
        using (LogStage.Begin(_logger, fileId, "write_source_pdf", new { sourcePath, workDir }))
        {
            await using var sourceFile = File.Create(sourcePath);
            await pdfStream.CopyToAsync(sourceFile, cancellationToken);
        }

        try
        {
            var sizeBytes = new FileInfo(sourcePath).Length;
            _logger.LogInformation("Source PDF persisted: {fileId} path={path} sizeBytes={sizeBytes}", fileId, sourcePath, sizeBytes);
        }
        catch
        {
            // ignore
        }

        // Best-effort: generate a "before" accessibility report on the original uploaded PDF.
        AdobeAccessibilityCheckOutput? beforeAccessibilityReport = null;
        try
        {
            var beforeAccessibilityPdfPath = Path.Combine(workDir, $"{safeFileId}.before.a11y.pdf");
            var beforeAccessibilityReportPath = Path.Combine(workDir, $"{safeFileId}.before.a11y-report.json");

            using (LogStage.Begin(
                       _logger,
                       fileId,
                       "adobe_before_a11y",
                       new { input = sourcePath, outputPdf = beforeAccessibilityPdfPath, outputReport = beforeAccessibilityReportPath }))
            {
                beforeAccessibilityReport = await _adobePdfServices.RunAccessibilityCheckerAsync(
                    inputPdfPath: sourcePath,
                    outputPdfPath: beforeAccessibilityPdfPath,
                    outputReportPath: beforeAccessibilityReportPath,
                    pageStart: null,
                    pageEnd: null,
                    cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate BEFORE accessibility report for {fileId}", fileId);
        }

        var pageCount = 0;
        string taggedPdfPath;
        if (_options.UseAdobePdfServices)
        {
            _logger.LogInformation(
                "Adobe autotagging enabled: {fileId} autotagTaggedPdfs={autotagTaggedPdfs} maxPagesPerChunk={maxPagesPerChunk} remediation={useRemediation} bookmarks={useBookmarks}",
                fileId,
                _options.AutotagTaggedPdfs,
                _options.MaxPagesPerChunk,
                _options.UsePdfRemediationProcessor,
                _options.UsePdfBookmarks);

            var outputTaggedPath = Path.Combine(workDir, $"{safeFileId}.tagged.pdf");
            var sourceInfo = ReadSourcePdfInfo(
                sourcePath,
                beforeAccessibilityReport?.ReportJson,
                out var retagTriggers,
                out var retagDecisionError);
            pageCount = sourceInfo.PageCount > 0 ? sourceInfo.PageCount : 0;
            if (!string.IsNullOrWhiteSpace(retagDecisionError))
            {
                _logger.LogDebug(
                    "Could not evaluate BEFORE accessibility report retag triggers for {fileId}: {error}",
                    fileId,
                    retagDecisionError);
            }

            if (sourceInfo.TaggingState == PdfTaggingState.TaggedUsable && !_options.AutotagTaggedPdfs)
            {
                _logger.LogInformation("Skipping Adobe autotagging for {fileId}: PDF is already tagged.", fileId);
                taggedPdfPath = sourcePath;
            }
            else
            {
                if (sourceInfo.TaggingState == PdfTaggingState.TaggedUsable && _options.AutotagTaggedPdfs)
                {
                    _logger.LogInformation("Retagging already-tagged PDF for {fileId} (AutotagTaggedPdfs=true).", fileId);
                }
                else if (sourceInfo.TaggingState == PdfTaggingState.TaggedBroken)
                {
                    if (retagTriggers.Count > 0)
                    {
                        _logger.LogWarning(
                            "BEFORE accessibility report indicates tag/structure issues ({triggers}); autotagging {fileId}.",
                            string.Join(", ", retagTriggers),
                            fileId);
                    }
                    else
                    {
                        _logger.LogWarning("PDF appears tagged but tag tree looks incomplete; autotagging {fileId}.", fileId);
                    }
                }

                if (sourceInfo.PageCount <= _options.MaxPagesPerChunk)
                {
                    // Common case: avoid split/merge overhead when the PDF fits in a single chunk.
                    var reportPath = Path.Combine(workDir, $"{safeFileId}.autotag-report.xlsx");
                    using (LogStage.Begin(
                               _logger,
                               fileId,
                               "adobe_autotag_single",
                               new { input = sourcePath, outputTagged = outputTaggedPath, outputReport = reportPath, pageCount = sourceInfo.PageCount }))
                    {
                        await _adobePdfServices.AutotagPdfAsync(
                            inputPdfPath: sourcePath,
                            outputTaggedPdfPath: outputTaggedPath,
                            outputTaggingReportPath: reportPath,
                            cancellationToken: cancellationToken);
                    }

                    taggedPdfPath = outputTaggedPath;
                }
                else
                {
                    // 1) Split the incoming PDF stream into chunks of <= MaxPagesPerChunk pages.
                    //    - Write each chunk to a temp file under the work directory.
                    //    - Keep an ordered list of the chunk file paths.
                    List<PdfChunk> chunks;
                    using (LogStage.Begin(
                               _logger,
                               fileId,
                               "split_into_chunks",
                               new { input = sourcePath, maxPagesPerChunk = _options.MaxPagesPerChunk }))
                    {
                        chunks = SplitIntoChunks(sourcePath, workDir, safeFileId, _options.MaxPagesPerChunk, cancellationToken);
                    }

                    _logger.LogInformation(
                        "Split {fileId} into {chunkCount} chunk(s) in {workDir}",
                        fileId,
                        chunks.Count,
                        workDir);

                    // 3. Autotag each chunk via Adobe PDF Services.
                    var taggedChunkPaths = new List<string>(chunks.Count);
                    foreach (var chunk in chunks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var taggedPath = Path.Combine(workDir, $"{safeFileId}.part{chunk.Index + 1:000}.tagged.pdf");
                        var reportPath = Path.Combine(workDir, $"{safeFileId}.part{chunk.Index + 1:000}.autotag-report.xlsx");

                        using (LogStage.Begin(
                                   _logger,
                                   fileId,
                                   "adobe_autotag_chunk",
                                   new
                                   {
                                       chunk = chunk.Index + 1,
                                       chunkFromPage = chunk.FromPage,
                                       chunkToPage = chunk.ToPage,
                                       input = chunk.Path,
                                       outputTagged = taggedPath,
                                       outputReport = reportPath
                                   }))
                        {
                            await _adobePdfServices.AutotagPdfAsync(
                                inputPdfPath: chunk.Path,
                                outputTaggedPdfPath: taggedPath,
                                outputTaggingReportPath: reportPath,
                                cancellationToken: cancellationToken);
                        }

                        taggedChunkPaths.Add(taggedPath);
                    }

                    // 4. Merge all tagged chunk PDFs back into a single tagged PDF.
                    using (LogStage.Begin(_logger, fileId, "merge_chunks", new { chunkCount = taggedChunkPaths.Count, output = outputTaggedPath }))
                    {
                        MergePdfsInOrder(taggedChunkPaths, outputTaggedPath, cancellationToken);
                    }
                    _logger.LogInformation("Merged tagged PDF written to {path}", outputTaggedPath);

                    taggedPdfPath = outputTaggedPath;
                }
            }
        }
        else
        {
            // When Adobe autotagging is disabled, avoid splitting/merging with iText.
            taggedPdfPath = sourcePath;
            pageCount = TryReadPageCount(sourcePath);
        }

        string finalPdfPath;
        if (_options.UsePdfRemediationProcessor)
        {
            // 5. Post-process the tagged PDF:
            //    - Walk the PDF structure by page to find images and links; add/repair alt text where missing.
            //    - Infer/set a document title.
            //    - Optional: build/insert a TOC.
            var remediatedPdfPath = Path.Combine(workDir, $"{safeFileId}.remediated.pdf");
            PdfRemediationResult remediation;
            using (LogStage.Begin(_logger, fileId, "pdf_remediation", new { input = taggedPdfPath, output = remediatedPdfPath }))
            {
                remediation = await _pdfRemediationProcessor.ProcessAsync(
                    fileId,
                    taggedPdfPath,
                    remediatedPdfPath,
                    cancellationToken);
            }
            finalPdfPath = remediation.OutputPdfPath;

            _logger.LogInformation("Remediated PDF written to {path}", finalPdfPath);
        }
        else
        {
            finalPdfPath = taggedPdfPath;
        }

        // 6. Generate a new accessibility (a11y) report for the final PDF.
        AdobeAccessibilityCheckOutput? accessibilityReport = null;
        try
        {
            var accessibilityPdfPath = Path.Combine(workDir, $"{safeFileId}.a11y.pdf");
            var accessibilityReportPath = Path.Combine(workDir, $"{safeFileId}.a11y-report.json");

            using (LogStage.Begin(
                       _logger,
                       fileId,
                       "adobe_after_a11y",
                       new { input = finalPdfPath, outputPdf = accessibilityPdfPath, outputReport = accessibilityReportPath }))
            {
                accessibilityReport = await _adobePdfServices.RunAccessibilityCheckerAsync(
                    inputPdfPath: finalPdfPath,
                    outputPdfPath: accessibilityPdfPath,
                    outputReportPath: accessibilityReportPath,
                    pageStart: null,
                    pageEnd: null,
                    cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate accessibility report for {fileId}", fileId);
        }

        _logger.LogInformation(
            "PDF processing finished: {fileId} elapsedMs={elapsedMs} workDir={workDir}",
            fileId,
            totalSw.Elapsed.TotalMilliseconds,
            workDir);

        return new PdfProcessResult(
            finalPdfPath,
            beforeAccessibilityReport?.ReportJson,
            beforeAccessibilityReport?.ReportPath,
            accessibilityReport?.ReportJson,
            accessibilityReport?.ReportPath,
            PageCount: pageCount);
    }

    private enum PdfTaggingState
    {
        Untagged = 0,
        TaggedUsable = 1,
        TaggedBroken = 2,
        Unknown = 3,
    }

    private sealed record SourcePdfInfo(int PageCount, PdfTaggingState TaggingState);

    private static int TryReadPageCount(string pdfPath)
    {
        try
        {
            using var pdf = new PdfDocument(new PdfReader(pdfPath));
            var pageCount = pdf.GetNumberOfPages();
            return pageCount > 0 ? pageCount : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static SourcePdfInfo ReadSourcePdfInfo(
        string pdfPath,
        string? beforeAccessibilityReportJson,
        out IReadOnlyList<string> retagTriggers,
        out string? retagDecisionError)
    {
        retagTriggers = Array.Empty<string>();
        retagDecisionError = null;

        var pageCount = 0;
        try
        {
            using var pdf = new PdfDocument(new PdfReader(pdfPath));

            pageCount = pdf.GetNumberOfPages();

            if (!pdf.IsTagged())
            {
                return new SourcePdfInfo(pageCount, PdfTaggingState.Untagged);
            }

            if (AdobeAccessibilityReportRetagDecider.TryShouldRetag(
                beforeAccessibilityReportJson,
                out var shouldRetag,
                out var triggers,
                out var error))
            {
                if (shouldRetag)
                {
                    retagTriggers = triggers;
                    return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedBroken);
                }
            }
            else
            {
                retagDecisionError = error;
            }

            var catalog = pdf.GetCatalog().GetPdfObject();
            var structTreeRoot = catalog.GetAsDictionary(PdfName.StructTreeRoot);
            if (structTreeRoot is null)
            {
                return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedBroken);
            }

            var rootKids = structTreeRoot.Get(PdfName.K);
            if (rootKids is null || rootKids is PdfNull)
            {
                return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedBroken);
            }

            var parentTree = structTreeRoot.Get(PdfName.ParentTree);
            if (parentTree is null || parentTree is PdfNull)
            {
                return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedBroken);
            }

            // Some PDFs present as "tagged" but contain an effectively empty tag tree (e.g., a single /Document
            // struct elem with no marked-content references). Treat these as broken so we can force re-tagging.
            if (!TagTreeHasContentItems(rootKids))
            {
                return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedBroken);
            }

            return new SourcePdfInfo(pageCount, PdfTaggingState.TaggedUsable);
        }
        catch
        {
            return new SourcePdfInfo(pageCount, PdfTaggingState.Unknown);
        }
    }

    private static bool TagTreeHasContentItems(PdfObject rootKids)
    {
        const int maxNodesToScan = 20_000;

        var visited = new HashSet<(int objNum, int genNum)>();
        var stack = new Stack<PdfObject>();
        stack.Push(rootKids);

        var nodesScanned = 0;
        while (stack.Count > 0 && nodesScanned < maxNodesToScan)
        {
            var node = stack.Pop();
            node = Dereference(node, visited);
            nodesScanned++;

            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    stack.Push(item);
                }

                continue;
            }

            if (node is PdfNumber)
            {
                // Integers in a structure element's /K can represent MCIDs.
                return true;
            }

            if (node is not PdfDictionary dict)
            {
                continue;
            }

            if (dict.GetAsNumber(PdfName.MCID) is not null)
            {
                return true;
            }

            if (dict.Get(PdfName.Obj) is not null)
            {
                return true;
            }

            var kids = dict.Get(PdfName.K);
            if (kids is not null && kids is not PdfNull)
            {
                stack.Push(kids);
            }
        }

        return false;
    }

    private static PdfObject Dereference(PdfObject obj, HashSet<(int objNum, int genNum)> visited)
    {
        if (obj is PdfIndirectReference reference)
        {
            var key = (reference.GetObjNumber(), reference.GetGenNumber());
            if (!visited.Add(key))
            {
                return new PdfNull();
            }

            return reference.GetRefersTo(true) ?? new PdfNull();
        }

        return obj;
    }

    /// <summary>
    /// Splits a PDF into ordered page-range chunks and writes each chunk to disk.
    /// </summary>
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

    /// <summary>
    /// Computes the working directory used for intermediate ingest artifacts.
    /// </summary>
    /// <remarks>
    /// Prefers <c>/tmp</c> when available to avoid filling application directories, and falls back to the platform
    /// temp directory when needed.
    /// </remarks>
    private static string GetWorkDir(string safeFileId, string? workDirRoot)
    {
        // Prefer `/tmp`; fall back to platform temp if unavailable.
        var baseTmp =
            string.IsNullOrWhiteSpace(workDirRoot)
                ? (Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                : workDirRoot.TrimEnd(Path.DirectorySeparatorChar);

        return Path.Combine(baseTmp, "readable-ingest", safeFileId);
    }

    /// <summary>
    /// Produces a filesystem-safe identifier for use in temporary file names.
    /// </summary>
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

    /// <summary>
    /// Merges PDFs into a single document, preserving the provided input order.
    /// </summary>
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
    public async Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
    {
        var safeFileId = Sanitize(fileId);
        var tmpRoot = Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var workDir = Path.Combine(tmpRoot, "readable-ingest", safeFileId);
        Directory.CreateDirectory(workDir);

        var outputPath = Path.Combine(workDir, $"{safeFileId}.noop.pdf");
        await using (var output = File.Create(outputPath))
        {
            await pdfStream.CopyToAsync(output, cancellationToken);
        }

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

        return new PdfProcessResult(outputPath);
    }

    private static string Sanitize(string value)
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
}
