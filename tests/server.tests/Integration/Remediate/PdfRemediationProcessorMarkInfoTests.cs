using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorMarkInfoTests
{
    [Fact]
    public async Task ProcessAsync_WhenTaggedPdfHasMarkedFalse_SetsMarkedTrue()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-markinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var originalPdfPath = Path.Combine(runRoot, "original.pdf");
            CreateTaggedPdf(originalPdfPath);

            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            ForceMarkedFalse(originalPdfPath, inputPdfPath);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();
                IsCatalogMarked(inputPdf).Should().BeFalse();
            }

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
            IsCatalogMarked(outputPdf).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateTaggedPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);
        doc.Add(new Paragraph("A short tagged PDF used to verify catalog MarkInfo metadata."));
    }

    private static void ForceMarkedFalse(string inputPath, string outputPath)
    {
        using var pdf = new PdfDocument(new PdfReader(inputPath), new PdfWriter(outputPath));

        var catalog = pdf.GetCatalog().GetPdfObject();
        var markInfo = catalog.GetAsDictionary(PdfName.MarkInfo) ?? new PdfDictionary();
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(false));
        catalog.Put(PdfName.MarkInfo, markInfo);
    }

    private static bool IsCatalogMarked(PdfDocument pdf)
    {
        var catalog = pdf.GetCatalog().GetPdfObject();
        var marked = catalog.GetAsDictionary(PdfName.MarkInfo)?.Get(PdfName.Marked);

        return marked is PdfBoolean value && value.GetValue();
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

    private sealed class FakePdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake title");
        }
    }
}
