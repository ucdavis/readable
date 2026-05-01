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

    [Fact]
    public async Task ProcessAsync_TaggedMissingAlt_PassesPrimaryLanguageToAltTextService()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
        File.Exists(fixturePath).Should().BeTrue($"fixture should exist at {fixturePath}");

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-img-lang-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input-with-lang.pdf");
            using (var pdf = new PdfDocument(new PdfReader(fixturePath), new PdfWriter(inputPdfPath)))
            {
                pdf.GetCatalog().GetPdfObject().Put(PdfName.Lang, new PdfString("fr-CA"));
            }

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

            altText.ImageRequests.Should().NotBeEmpty();
            altText.ImageRequests.Should().OnlyContain(r => r.PrimaryLanguage == "fr-CA");
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
    public async Task ProcessAsync_WhenRasterImageAltGenerationFails_LeavesFigureWithoutAlt()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-alt-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                new FakePdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);

            var result = await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(result.OutputPdfPath));
            var outputFigures = ListStructElementsByRole(outputPdf, PdfName.Figure);
            outputFigures.Count.Should().BeGreaterThan(0);
            outputFigures.Any(f => !HasNonEmptyAlt(f)).Should().BeTrue("failed automation should leave figures for manual remediation instead of writing placeholder alt");
            outputFigures.Should().NotContain(f => GetAlt(f) == "fake image alt text");
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

    private sealed class ThrowingAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("fake alt text failure");
        }

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("fake link alt text failure");
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

    private static bool HasNonEmptyAlt(PdfDictionary structElem)
    {
        var alt = GetAlt(structElem);
        return !string.IsNullOrWhiteSpace(alt);
    }

    private static string? GetAlt(PdfDictionary structElem) => structElem.GetAsString(PdfName.Alt)?.ToUnicodeString();

    private static List<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
    {
        var results = new List<PdfDictionary>();

        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        if (structTreeRootDict is null)
        {
            return results;
        }

        var rootKids = structTreeRootDict.Get(PdfName.K);
        if (rootKids is null)
        {
            return results;
        }

        Traverse(rootKids, role, results);
        return results;
    }

    private static void Traverse(PdfObject node, PdfName role, List<PdfDictionary> results)
    {
        node = Dereference(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                Traverse(item, role, results);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        var s = dict.GetAsName(PdfName.S);
        if (role.Equals(s))
        {
            results.Add(dict);
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            Traverse(kids, role, results);
        }
    }

    private static PdfObject Dereference(PdfObject obj)
        => obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;
}
