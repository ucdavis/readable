using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorTitleTests
{
    [Fact]
    public async Task ProcessAsync_WhenEnoughText_GeneratesTitleFromFirstPagesOnly()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-title-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdf(
                inputPdfPath,
                title: "Old Title",
                pages:
                [
                    MakePageText("PAGE1TOKEN", wordCount: 40),
                    MakePageText("PAGE2TOKEN", wordCount: 40),
                    MakePageText("PAGE3TOKEN", wordCount: 40),
                    MakePageText("PAGE4TOKEN", wordCount: 40),
                ]);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            var titleService = new CapturingPdfTitleService { TitleToReturn = "New Generated Title" };
            var sut = new PdfRemediationProcessor(new ThrowingAltTextService(), titleService);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            titleService.Requests.Should().ContainSingle();
            titleService.Requests[0].CurrentTitle.Should().Be("Old Title");

            titleService.Requests[0].ExtractedText.Should().Contain("PAGE1TOKEN");
            titleService.Requests[0].ExtractedText.Should().Contain("PAGE2TOKEN");
            titleService.Requests[0].ExtractedText.Should().Contain("PAGE3TOKEN");
            titleService.Requests[0].ExtractedText.Should().NotContain("PAGE4TOKEN");

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            outputPdf.GetDocumentInfo().GetTitle().Should().Be("New Generated Title");
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
    public async Task ProcessAsync_WhenNotEnoughTextAndNoTitle_SetsPlaceholderWithoutCallingAi()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-title-placeholder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdf(
                inputPdfPath,
                title: null,
                pages:
                [
                    "one two",
                    "three four",
                    "five six",
                    "seven eight",
                    "nine ten",
                ]);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            var titleService = new ThrowingPdfTitleService();
            var sut = new PdfRemediationProcessor(new ThrowingAltTextService(), titleService);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            outputPdf.GetDocumentInfo().GetTitle().Should().Be("Untitled PDF document");
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
    public async Task ProcessAsync_WhenNotEnoughTextAndHasTitle_KeepsExistingTitleWithoutCallingAi()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-title-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdf(
                inputPdfPath,
                title: "Existing Title",
                pages:
                [
                    "one two",
                    "three four",
                    "five six",
                    "seven eight",
                    "nine ten",
                ]);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            var titleService = new ThrowingPdfTitleService();
            var sut = new PdfRemediationProcessor(new ThrowingAltTextService(), titleService);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            outputPdf.GetDocumentInfo().GetTitle().Should().Be("Existing Title");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static string MakePageText(string token, int wordCount)
    {
        wordCount.Should().BeGreaterThanOrEqualTo(1);
        return string.Join(' ', new[] { token }.Concat(Enumerable.Repeat("word", wordCount - 1)));
    }

    private static void CreatePdf(string path, string? title, IReadOnlyList<string> pages)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        if (!string.IsNullOrWhiteSpace(title))
        {
            pdf.GetDocumentInfo().SetTitle(title);
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

    private sealed class ThrowingAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for these title tests.");

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for these title tests.");

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class CapturingPdfTitleService : IPdfTitleService
    {
        public List<PdfTitleRequest> Requests { get; } = new();
        public string TitleToReturn { get; set; } = "generated title";

        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(TitleToReturn);
        }
    }

    private sealed class ThrowingPdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PDF title service should not be called when there isn't enough extracted text.");
    }
}

