using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorTabOrderTests
{
    [Fact]
    public async Task ProcessAsync_WhenTabOrderNotUsingDocumentStructure_SetsTabsToS()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-taborder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var originalPdfPath = Path.Combine(runRoot, "original.pdf");
            CreateTaggedPdf(originalPdfPath, pageCount: 2);

            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            ForcePageTabOrder(originalPdfPath, inputPdfPath, PdfName.R);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            var sut = new PdfRemediationProcessor(
                new FakeAltTextService(),
                new NoopPdfBookmarkService(),
                new FakePdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            outputPdf.IsTagged().Should().BeTrue();

            for (var pageNumber = 1; pageNumber <= outputPdf.GetNumberOfPages(); pageNumber++)
            {
                var tabs = outputPdf.GetPage(pageNumber).GetPdfObject().GetAsName(PdfName.Tabs);
                PdfName.S.Equals(tabs).Should().BeTrue($"page {pageNumber} should have /Tabs /S");
            }
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed class FakeAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake image alt text");
        }

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake link alt text");
        }

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class FakePdfTitleService : server.core.Remediate.Title.IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(server.core.Remediate.Title.PdfTitleRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake title");
        }
    }

    private static void CreateTaggedPdf(string path, int pageCount)
    {
        pageCount.Should().BeGreaterThanOrEqualTo(1);

        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        for (var i = 0; i < pageCount; i++)
        {
            if (i > 0)
            {
                doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            doc.Add(new Paragraph($"Page {i + 1}"));
        }
    }

    private static void ForcePageTabOrder(string inputPath, string outputPath, PdfName tabOrder)
    {
        using var pdf = new PdfDocument(new PdfReader(inputPath), new PdfWriter(outputPath));

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            pdf.GetPage(pageNumber).GetPdfObject().Put(PdfName.Tabs, tabOrder);
        }
    }
}

