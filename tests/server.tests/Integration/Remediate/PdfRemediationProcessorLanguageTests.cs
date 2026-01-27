using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorLanguageTests
{
    [Fact]
    public async Task ProcessAsync_WhenLangMissing_SetsLangFromDetectedText()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-lang-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdf(
                inputPdfPath,
                existingLang: null,
                pages:
                [
                    MakePageText(
                        "Bonjour, ceci est un document en français. Il contient suffisamment de texte pour détecter la langue principale.",
                        wordCount: 30),
                ]);

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
            GetLang(outputPdf).Should().Be("fr");
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
    public async Task ProcessAsync_WhenLangAlreadySet_DoesNotOverwrite()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-lang-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdf(
                inputPdfPath,
                existingLang: "fr-FR",
                pages:
                [
                    MakePageText("This is an English document but the language is already set.", wordCount: 30),
                ]);

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
            GetLang(outputPdf).Should().Be("fr-FR");
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

    private static string MakePageText(string prefix, int wordCount)
    {
        wordCount.Should().BeGreaterThanOrEqualTo(1);
        return string.Join(' ', new[] { prefix }.Concat(Enumerable.Repeat("word", wordCount - 1)));
    }

    private static void CreatePdf(string path, string? existingLang, IReadOnlyList<string> pages)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);

        if (!string.IsNullOrWhiteSpace(existingLang))
        {
            pdf.GetCatalog().GetPdfObject().Put(PdfName.Lang, new PdfString(existingLang));
        }

        using var doc = new Document(pdf);

        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
            {
                doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            doc.Add(new Paragraph(pages[i]));
        }
    }

    private static string? GetLang(PdfDocument pdf)
        => pdf.GetCatalog().GetPdfObject().GetAsString(PdfName.Lang)?.ToUnicodeString();
}
