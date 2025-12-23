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
                MaxPagesPerChunk = 3,
                WorkDirRoot = runRoot
            });

            var sut = new PdfProcessor(adobe, remediation, options, loggerFactory.CreateLogger<PdfProcessor>());

            await sut.ProcessAsync(fileId, inputStream, CancellationToken.None);

            var safeFileId = fileId;
            var workDir = Path.Combine(runRoot, "readable-ingest", safeFileId);

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

    private static void CreateTestPdf(string outputPath, int pageCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        for (var i = 0; i < pageCount; i++)
        {
            pdf.AddNewPage();
        }
    }

    private static int ReadPageCount(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        return pdf.GetNumberOfPages();
    }
}
