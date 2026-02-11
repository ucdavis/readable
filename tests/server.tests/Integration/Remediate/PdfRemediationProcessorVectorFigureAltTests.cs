using FluentAssertions;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Rasterize;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorVectorFigureAltTests
{
    [Fact]
    public async Task ProcessAsync_TaggedHasPlaceholderAlt_ReplacesPlaceholderAltForVectorFigures()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-bad-alt.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var inputPlaceholderCount = 0;
        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
            var inputFigures = ListStructElementsByRole(inputPdf, PdfName.Figure);
            inputFigures.Count.Should().BeGreaterThan(0);
            inputPlaceholderCount = inputFigures.Count(f => IsPlaceholderAlt(f));
            inputPlaceholderCount.Should().BeGreaterThan(0, "fixture should contain placeholder alt text");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-vector-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            var altText = new FakeAltTextService();
            var sut = new PdfRemediationProcessor(
                altText,
                new NoopPdfBookmarkService(),
                new FakePdfTitleService(),
                new FakePdfPageRasterizer(),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var outputFigures = ListStructElementsByRole(outputPdf, PdfName.Figure);
            var outputPlaceholderCount = outputFigures.Count(f => IsPlaceholderAlt(f));

            outputPlaceholderCount.Should().BeLessThan(inputPlaceholderCount, "vector figure remediation should reduce placeholder alt text");
            altText.ImageCalls.Should().BeGreaterThan(0, "vector figure remediation should call the alt text service at least once");
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
        public int ImageCalls { get; private set; }

        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            ImageCalls++;
            return Task.FromResult("generated alt text");
        }

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("generated link alt");
        }

        public string GetFallbackAltTextForImage() => "alt text for image";

        public string GetFallbackAltTextForLink() => "alt text for link";
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

    private sealed class FakePdfPageRasterizer : IPdfPageRasterizer
    {
        private static readonly BgraBitmap BlankPage = CreateBlankPage(widthPx: 200, heightPx: 200);

        public bool IsAvailable => true;

        public IPdfRasterDocument OpenDocument(string pdfPath, int dpi)
        {
            _ = pdfPath;
            _ = dpi;
            return new Doc();
        }

        private sealed class Doc : IPdfRasterDocument
        {
            public void Dispose()
            {
            }

            public BgraBitmap RenderPage(int pageNumber1Based, CancellationToken cancellationToken)
            {
                _ = pageNumber1Based;
                cancellationToken.ThrowIfCancellationRequested();
                return BlankPage;
            }
        }

        private static BgraBitmap CreateBlankPage(int widthPx, int heightPx)
        {
            var stride = widthPx * 4;
            var bytes = new byte[stride * heightPx];
            for (var i = 0; i < bytes.Length; i += 4)
            {
                bytes[i + 0] = 255; // B
                bytes[i + 1] = 255; // G
                bytes[i + 2] = 255; // R
                bytes[i + 3] = 255; // A
            }

            return new BgraBitmap(bytes, widthPx, heightPx, stride);
        }
    }

    private static bool IsPlaceholderAlt(PdfDictionary structElem)
    {
        var alt = structElem.GetAsString(PdfName.Alt)?.ToUnicodeString() ?? string.Empty;
        return string.Equals(alt.Trim(), "alt text for image", StringComparison.OrdinalIgnoreCase);
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

