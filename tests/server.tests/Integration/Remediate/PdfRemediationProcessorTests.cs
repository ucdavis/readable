using FluentAssertions;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorTests
{
    [Fact]
    public async Task ProcessAsync_TaggedMissingAlt_AddsAltToMatchedFigures()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue();
            var inputFigures = ListStructElementsByRole(inputPdf, PdfName.Figure);
            inputFigures.Count.Should().BeGreaterThan(0);
            inputFigures.Any(f => !HasNonEmptyAlt(f)).Should().BeTrue("fixture should have figures missing alt text");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var altText = new FakeAltTextService();
            var sut = new PdfRemediationProcessor(
                altText,
                new NoopPdfBookmarkService(),
                new FakePdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);

            var result = await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            File.Exists(result.OutputPdfPath).Should().BeTrue();

            using var outputPdf = new PdfDocument(new PdfReader(result.OutputPdfPath));
            outputPdf.IsTagged().Should().BeTrue();

            var outputFigures = ListStructElementsByRole(outputPdf, PdfName.Figure);
            outputFigures.Count.Should().BeGreaterThan(0);
            outputFigures.Any(f => GetAlt(f) == "fake image alt text").Should().BeTrue();
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
    public async Task ProcessAsync_RepeatedRasterFigures_AddsAltWithoutDemotingToSpan()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-repeated-raster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        var inputPdfPath = Path.Combine(runRoot, "input.pdf");
        CreateTaggedPdfWithRepeatedRasterFigures(inputPdfPath);

        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue();

            var inputFigures = ListStructElementsByRole(inputPdf, PdfName.Figure);
            inputFigures.Should().HaveCount(3, "fixture has the same tagged raster figure repeated on each page");
            inputFigures.Should().OnlyContain(f => !HasNonEmptyAlt(f));
        }

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var altText = new FakeAltTextService();
            var sut = new PdfRemediationProcessor(
                altText,
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
            outputFigures.Should().HaveCount(3);
            outputFigures.Should().OnlyContain(f => GetAlt(f) == "fake image alt text");
            altText.ImageCalls.Should().Be(1, "identical raster images should reuse generated alt text");

            ListStructElementsByRole(outputPdf, RoleSpan)
                .Should()
                .BeEmpty("repeated raster image figures should not be demoted to /Span with image content");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateTaggedPdfWithRepeatedRasterFigures(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        pdf.SetTagged();

        using var document = new Document(pdf);
        var imageData = ImageDataFactory.Create(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lN3fWQAAAABJRU5ErkJggg=="));

        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            if (pageNumber > 1)
            {
                document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            var image = new Image(imageData)
                .SetWidth(120)
                .SetHeight(120);
            image.GetAccessibilityProperties().SetRole("Figure");
            document.Add(image);
        }
    }

    private sealed class FakeAltTextService : IAltTextService
    {
        public int ImageCalls { get; private set; }

        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            ImageCalls++;
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

    private static readonly PdfName RoleSpan = new("Span");

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
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }
}
