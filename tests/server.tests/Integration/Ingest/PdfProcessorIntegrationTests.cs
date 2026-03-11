using FluentAssertions;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using server.core.Ingest;
using server.core.Remediate;

namespace server.tests.Integration.Ingest;

public sealed class PdfProcessorIntegrationTests
{
    [Fact]
    public async Task ProcessAsync_WithSmallChunkSize_SplitsAndMergesInOrder()
    {
        var fileId = $"pdf-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 7);

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new NoopAdobePdfServices(loggerFactory.CreateLogger<NoopAdobePdfServices>());
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = true,
                MaxPagesPerChunk = 3,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            var safeFileId = fileId;
            var workDir = FindSingleAttemptWorkDir(runRoot, safeFileId);

            File.Exists(Path.Combine(workDir, $"{safeFileId}.source.pdf")).Should().BeTrue();
            File.Exists(Path.Combine(workDir, $"{safeFileId}.part001.pdf")).Should().BeTrue();
            File.Exists(Path.Combine(workDir, $"{safeFileId}.part002.pdf")).Should().BeTrue();
            File.Exists(Path.Combine(workDir, $"{safeFileId}.part003.pdf")).Should().BeTrue();

            ReadPageCount(Path.Combine(workDir, $"{safeFileId}.part001.pdf")).Should().Be(3);
            ReadPageCount(Path.Combine(workDir, $"{safeFileId}.part002.pdf")).Should().Be(3);
            ReadPageCount(Path.Combine(workDir, $"{safeFileId}.part003.pdf")).Should().Be(1);

            var mergedTagged = Path.Combine(workDir, $"{safeFileId}.tagged.pdf");
            var remediated = Path.Combine(workDir, $"{safeFileId}.remediated.pdf");
            File.Exists(mergedTagged).Should().BeTrue();
            File.Exists(remediated).Should().BeTrue();

            ReadPageCount(mergedTagged).Should().Be(7);
            ReadPageCount(remediated).Should().Be(7);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WithAdobeAndRemediationDisabled_PreservesExistingTags()
    {
        var fileId = $"pdf-tagged-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var repoRoot = FindRepoRoot();
            var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
            File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new NoopAdobePdfServices(loggerFactory.CreateLogger<NoopAdobePdfServices>());
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = false,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 2,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);
            result.OutputPdfPath.Should().EndWith(".source.pdf");

            using var output = new PdfDocument(new PdfReader(result.OutputPdfPath));
            output.IsTagged().Should().BeTrue();

            var workDir = FindSingleAttemptWorkDir(runRoot, fileId);
            Directory.GetFiles(workDir, "*.part*.pdf").Should().BeEmpty();
            Directory.GetFiles(workDir, "*.part*.tagged.pdf").Should().BeEmpty();
            Directory.GetFiles(workDir, "*.remediated.pdf").Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WithAdobeEnabledAndSmallPdf_DoesNotSplitOrMerge()
    {
        var fileId = $"pdf-small-adobe-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 2);
            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new CapturingAdobePdfServices();
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 200,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            adobe.Calls.Should().HaveCount(1);
            adobe.Calls[0].InputPdfPath.Should().EndWith(".source.pdf");
            adobe.Calls[0].OutputTaggedPdfPath.Should().EndWith(".tagged.pdf");

            var workDir = FindSingleAttemptWorkDir(runRoot, fileId);
            File.Exists(Path.Combine(workDir, $"{fileId}.tagged.pdf")).Should().BeTrue();
            Directory.GetFiles(workDir, "*.part*.pdf").Should().BeEmpty();
            Directory.GetFiles(workDir, "*.part*.tagged.pdf").Should().BeEmpty();

            result.OutputPdfPath.Should().EndWith(".tagged.pdf");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WithAdobeEnabledAndTaggedPdf_SkipsAutotagByDefault()
    {
        var fileId = $"pdf-tagged-adobe-skip-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var repoRoot = FindRepoRoot();
            var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
            File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new CapturingAdobePdfServices();
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 2,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            adobe.Calls.Should().BeEmpty("already-tagged PDFs should skip Adobe autotagging by default");
            result.OutputPdfPath.Should().EndWith(".source.pdf");

            using var output = new PdfDocument(new PdfReader(result.OutputPdfPath));
            output.IsTagged().Should().BeTrue();

            var workDir = FindSingleAttemptWorkDir(runRoot, fileId);
            Directory.GetFiles(workDir, "*.part*.pdf").Should().BeEmpty();
            File.Exists(Path.Combine(workDir, $"{fileId}.tagged.pdf")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WithAdobeEnabledAndTriviallyTaggedPdf_Autotags()
    {
        var fileId = $"pdf-tagged-adobe-trivial-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTriviallyTaggedPdf(inputPdfPath);

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new CapturingAdobePdfServices();
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 200,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            adobe.Calls.Should().ContainSingle("trivially-tagged PDFs should be treated as broken and re-tagged");
            result.OutputPdfPath.Should().EndWith(".tagged.pdf");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenMaxUploadPagesExceeded_ThrowsBeforeAdobeOrRemediation()
    {
        var fileId = $"pdf-page-limit-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 26);

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new CapturingAdobePdfServices();
            var remediation = new CapturingRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = true,
                MaxPagesPerChunk = 200,
                MaxUploadPages = 25,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var act = () => sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            var ex = await act.Should().ThrowAsync<PdfPageLimitExceededException>();
            ex.Which.ActualPageCount.Should().Be(26);
            ex.Which.MaxAllowedPages.Should().Be(25);
            adobe.AutotagCalls.Should().Be(0);
            adobe.AccessibilityCheckCalls.Should().Be(0);
            remediation.Calls.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenMaxUploadPagesDisabled_DoesNotRejectLargePdf()
    {
        var fileId = $"pdf-page-limit-disabled-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 26);

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new NoopAdobePdfServices(loggerFactory.CreateLogger<NoopAdobePdfServices>());
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = false,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 200,
                MaxUploadPages = 0,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            result.PageCount.Should().Be(26);
            result.OutputPdfPath.Should().EndWith(".source.pdf");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WithDotDotFileId_NormalizesAttemptRoot()
    {
        const string fileId = "..";
        const string normalizedFileId = "invalid-file-id";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"pdf-dotdot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 1);

            await using var inputStream = File.OpenRead(inputPdfPath);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new NoopAdobePdfServices(loggerFactory.CreateLogger<NoopAdobePdfServices>());
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = false,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 200,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            var result = await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);
            var attemptRoot = Path.Combine(runRoot, "readable-ingest", normalizedFileId);
            var fullOutputPath = Path.GetFullPath(result.OutputPdfPath);
            var fullAttemptRoot = Path.GetFullPath(attemptRoot + Path.DirectorySeparatorChar);

            Directory.Exists(attemptRoot).Should().BeTrue();
            fullOutputPath.Should().StartWith(fullAttemptRoot);
            Path.GetFileName(result.OutputPdfPath).Should().Be($"{normalizedFileId}.source.pdf");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NoopProcessAsync_WithDotDotFileId_NormalizesAttemptRoot()
    {
        const string normalizedFileId = "invalid-file-id";
        string? attemptRoot = null;

        try
        {
            await using var inputStream = new MemoryStream("%PDF-1.7"u8.ToArray());
            var sut = new NoopPdfProcessor();

            var result = await sut.ProcessAsync("..", inputStream, CancellationToken.None);
            var workDir = Path.GetDirectoryName(result.OutputPdfPath)!;
            attemptRoot = Directory.GetParent(workDir)!.FullName;

            Path.GetFileName(result.OutputPdfPath).Should().Be($"{normalizedFileId}.noop.pdf");
            Directory.GetParent(workDir)!.Name.Should().Be(normalizedFileId);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(attemptRoot) && Directory.Exists(attemptRoot))
            {
                Directory.Delete(attemptRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenRunTwiceForSameFileId_UsesDistinctAttemptDirectories()
    {
        var fileId = $"pdf-retry-test-{Guid.NewGuid():N}";
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", fileId);
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTestPdf(inputPdfPath, pageCount: 2);

            using var loggerFactory = LoggerFactory.Create(_ => { });
            var adobe = new NoopAdobePdfServices(loggerFactory.CreateLogger<NoopAdobePdfServices>());
            var remediation = new NoopPdfRemediationProcessor();
            var options = Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = false,
                UsePdfRemediationProcessor = false,
                MaxPagesPerChunk = 200,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            await using var firstInputStream = File.OpenRead(inputPdfPath);
            var firstResult = await sut.ProcessAsync(fileId, firstInputStream, CancellationToken.None);

            await using var secondInputStream = File.OpenRead(inputPdfPath);
            var secondResult = await sut.ProcessAsync(fileId, secondInputStream, CancellationToken.None);

            var attemptRoot = FindAttemptRoot(runRoot, fileId);
            var workDirs = Directory.GetDirectories(attemptRoot);

            workDirs.Should().HaveCount(2);
            firstResult.OutputPdfPath.Should().NotBe(secondResult.OutputPdfPath);
            Path.GetDirectoryName(firstResult.OutputPdfPath).Should().NotBe(Path.GetDirectoryName(secondResult.OutputPdfPath));
            workDirs.Should().Contain(Path.GetDirectoryName(firstResult.OutputPdfPath)!);
            workDirs.Should().Contain(Path.GetDirectoryName(secondResult.OutputPdfPath)!);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed record AdobeCall(string InputPdfPath, string OutputTaggedPdfPath, string OutputTaggingReportPath);

    private sealed class CapturingAdobePdfServices : IAdobePdfServices
    {
        public List<AdobeCall> Calls { get; } = [];
        public int AccessibilityCheckCalls { get; private set; }
        public int AutotagCalls => Calls.Count;

        public async Task<AdobeAutotagOutput> AutotagPdfAsync(
            string inputPdfPath,
            string outputTaggedPdfPath,
            string outputTaggingReportPath,
            CancellationToken cancellationToken)
        {
            Calls.Add(new AdobeCall(inputPdfPath, outputTaggedPdfPath, outputTaggingReportPath));

            Directory.CreateDirectory(Path.GetDirectoryName(outputTaggedPdfPath)!);
            await using (var input = File.OpenRead(inputPdfPath))
            await using (var output = File.Open(outputTaggedPdfPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputTaggingReportPath)!);
            await File.WriteAllTextAsync(outputTaggingReportPath, "noop", cancellationToken);

            return new AdobeAutotagOutput(outputTaggedPdfPath, outputTaggingReportPath);
        }

        public Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
            string inputPdfPath,
            string outputPdfPath,
            string outputReportPath,
            int? pageStart,
            int? pageEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccessibilityCheckCalls++;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPdfPath)!);
            File.Copy(inputPdfPath, outputPdfPath, overwrite: true);

            Directory.CreateDirectory(Path.GetDirectoryName(outputReportPath)!);
            File.WriteAllText(outputReportPath, "{\"Summary\":{\"Failed\":0}}");

            return Task.FromResult(new AdobeAccessibilityCheckOutput(
                outputPdfPath,
                outputReportPath,
                "{\"Summary\":{\"Failed\":0}}"));
        }
    }

    private sealed class CapturingRemediationProcessor : IPdfRemediationProcessor
    {
        public int Calls { get; private set; }

        public Task<PdfRemediationResult> ProcessAsync(
            string fileId,
            string inputPdfPath,
            string outputPdfPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PdfRemediationResult(outputPdfPath));
        }
    }

    private static string FindAttemptRoot(string runRoot, string safeFileId)
    {
        var attemptRoot = Path.Combine(runRoot, "readable-ingest", safeFileId);
        Directory.Exists(attemptRoot).Should().BeTrue($"attempt root should exist at {attemptRoot}");
        return attemptRoot;
    }

    private static string FindSingleAttemptWorkDir(string runRoot, string safeFileId)
    {
        var attemptRoot = FindAttemptRoot(runRoot, safeFileId);
        var workDirs = Directory.GetDirectories(attemptRoot);
        workDirs.Should().ContainSingle($"expected exactly one attempt directory under {attemptRoot}");
        return workDirs[0];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Unable to locate repo root (missing app.sln).");
        }

        return dir.FullName;
    }

    private static void CreateTestPdf(string outputPath, int pageCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        for (var i = 0; i < pageCount; i++)
        {
            pdf.AddNewPage();
        }
    }

    private static void CreateTriviallyTaggedPdf(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var pdf = new PdfDocument(new PdfWriter(outputPath)))
        {
            pdf.AddNewPage();

            var catalog = pdf.GetCatalog().GetPdfObject();

            var structTreeRoot = new PdfDictionary();
            structTreeRoot.MakeIndirect(pdf);
            structTreeRoot.Put(PdfName.Type, PdfName.StructTreeRoot);

            var parentTree = new PdfDictionary();
            parentTree.MakeIndirect(pdf);
            structTreeRoot.Put(PdfName.ParentTree, parentTree);

            var documentElem = new PdfDictionary();
            documentElem.MakeIndirect(pdf);
            documentElem.Put(PdfName.Type, new PdfName("StructElem"));
            documentElem.Put(PdfName.S, new PdfName("Document"));
            documentElem.Put(PdfName.P, structTreeRoot);

            structTreeRoot.Put(PdfName.K, documentElem);

            var markInfo = new PdfDictionary();
            markInfo.MakeIndirect(pdf);
            markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

            catalog.Put(PdfName.MarkInfo, markInfo);
            catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
        }

        using var verify = new PdfDocument(new PdfReader(outputPath));
        verify.IsTagged().Should().BeTrue("fixture should appear tagged");
    }

    private static int ReadPageCount(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        return pdf.GetNumberOfPages();
    }
}



