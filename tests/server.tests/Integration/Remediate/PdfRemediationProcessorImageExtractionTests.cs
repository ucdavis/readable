using FluentAssertions;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorImageExtractionTests
{
    [Fact]
    public async Task ProcessAsync_TaggedMissingAlt_PassesImageBytesAndContextToAltTextService()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-img-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var altText = new CapturingAltTextService();
            var sut = new PdfRemediationProcessor(
                altText,
                new NoopPdfBookmarkService(),
                new FakePdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            altText.ImageRequests.Should().NotBeEmpty("we should see at least one image passed for alt text generation");
            altText.ImageRequests.Should().OnlyContain(r => r.ImageBytes != null && r.ImageBytes.Length > 0);
            altText.ImageRequests.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.MimeType));

            altText.ImageRequests.Any(r =>
                    !string.IsNullOrWhiteSpace(r.ContextBefore)
                    || !string.IsNullOrWhiteSpace(r.ContextAfter))
                .Should()
                .BeTrue("at least one image should include surrounding text context");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed class CapturingAltTextService : IAltTextService
    {
        public List<ImageAltTextRequest> ImageRequests { get; } = new();
        public List<LinkAltTextRequest> LinkRequests { get; } = new();

        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageRequests.Add(request);
            return Task.FromResult("fake image alt text");
        }

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinkRequests.Add(request);
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
}
