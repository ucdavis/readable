using FluentAssertions;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
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
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-vector-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithPlaceholderVectorFigure(inputPdfPath);

            var inputPlaceholderCount = 0;
            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
                var inputFigures = ListStructElementsByRole(inputPdf, PdfName.Figure);
                inputFigures.Count.Should().BeGreaterThan(0);
                inputPlaceholderCount = inputFigures.Count(f => IsPlaceholderAlt(f));
                inputPlaceholderCount.Should().BeGreaterThan(0, "fixture should contain placeholder alt text");
            }

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

    private static void CreateTaggedPdfWithPlaceholderVectorFigure(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        const int mcid = 0;

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();
        var pageDict = page.GetPdfObject();

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

        var figureElem = new PdfDictionary();
        figureElem.MakeIndirect(pdf);
        figureElem.Put(PdfName.Type, new PdfName("StructElem"));
        figureElem.Put(PdfName.S, PdfName.Figure);
        figureElem.Put(PdfName.P, documentElem);
        figureElem.Put(PdfName.Pg, pageDict);
        figureElem.Put(PdfName.Alt, new PdfString("alt text for image", PdfEncodings.UNICODE_BIG));

        var markedContentRef = new PdfDictionary();
        markedContentRef.Put(PdfName.Type, new PdfName("MCR"));
        markedContentRef.Put(PdfName.Pg, pageDict);
        markedContentRef.Put(PdfName.MCID, new PdfNumber(mcid));
        figureElem.Put(PdfName.K, markedContentRef);

        var kids = new PdfArray();
        kids.Add(figureElem);
        documentElem.Put(PdfName.K, kids);

        structTreeRoot.Put(PdfName.K, documentElem);

        var markInfo = new PdfDictionary();
        markInfo.MakeIndirect(pdf);
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

        catalog.Put(PdfName.MarkInfo, markInfo);
        catalog.Put(PdfName.StructTreeRoot, structTreeRoot);

        var markedContentProperties = new PdfDictionary();
        markedContentProperties.Put(PdfName.MCID, new PdfNumber(mcid));

        var canvas = new PdfCanvas(page);
        canvas.BeginMarkedContent(PdfName.Figure, markedContentProperties);
        canvas.SaveState();
        canvas.SetFillColor(ColorConstants.BLACK);
        canvas.Rectangle(100, 500, 120, 80);
        canvas.Fill();
        canvas.RestoreState();
        canvas.EndMarkedContent();
        canvas.Release();
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
