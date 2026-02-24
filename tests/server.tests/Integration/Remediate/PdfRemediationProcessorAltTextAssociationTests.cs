using FluentAssertions;
using iText.IO.Font;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorAltTextAssociationTests
{
    private static readonly PdfName RoleSpan = new("Span");

    [Fact]
    public async Task ProcessAsync_ContentlessFigureWithoutAlt_DemotesRole()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-alt-no-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithContentlessFigure(inputPdfPath, includePlaceholderAlt: false);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();
                ListStructElementsByRole(inputPdf, PdfName.Figure).Count.Should().BeGreaterThan(0);
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
            ListStructElementsByRole(outputPdf, PdfName.Figure).Should().BeEmpty("contentless /Figure nodes should be demoted");
            ListStructElementsByRole(outputPdf, RoleSpan).Count.Should().BeGreaterThan(0, "demoted nodes should use a neutral /Span role");
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
    public async Task ProcessAsync_ContentlessFigureWithPlaceholderAlt_RemovesAltAndDemotesRole()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-alt-no-content-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithContentlessFigure(inputPdfPath, includePlaceholderAlt: true);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();

                var figures = ListStructElementsByRole(inputPdf, PdfName.Figure);
                figures.Count.Should().BeGreaterThan(0);
                figures.Any(f => string.Equals(GetAlt(f), "alt text for image", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
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

            ListStructElementsByRole(outputPdf, PdfName.Figure).Should().BeEmpty("contentless /Figure nodes should be demoted");
            ListStructElementsByRole(outputPdf, RoleSpan).Count.Should().BeGreaterThan(0, "demoted nodes should use a neutral /Span role");

            ListStructElements(outputPdf)
                .Any(e => string.Equals(GetAlt(e), "alt text for image", StringComparison.OrdinalIgnoreCase))
                .Should()
                .BeFalse("placeholder /Alt should be removed from contentless nodes");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateTaggedPdfWithContentlessFigure(string outputPath, bool includePlaceholderAlt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var pdf = new PdfDocument(new PdfWriter(outputPath)))
        {
            pdf.AddNewPage();

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

            if (includePlaceholderAlt)
            {
                figureElem.Put(PdfName.Alt, new PdfString("alt text for image", PdfEncodings.UNICODE_BIG));
            }

            var kids = new PdfArray();
            kids.Add(figureElem);
            documentElem.Put(PdfName.K, kids);

            structTreeRoot.Put(PdfName.K, documentElem);

            var markInfo = new PdfDictionary();
            markInfo.MakeIndirect(pdf);
            markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

            catalog.Put(PdfName.MarkInfo, markInfo);
            catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
        }
    }

    private static List<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
        => ListStructElements(pdf).Where(e => role.Equals(e.GetAsName(PdfName.S))).ToList();

    private static List<PdfDictionary> ListStructElements(PdfDocument pdf)
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

        Traverse(rootKids, results);
        return results;
    }

    private static void Traverse(PdfObject node, List<PdfDictionary> results)
    {
        node = Dereference(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                Traverse(item, results);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        if (dict.GetAsName(PdfName.S) is not null)
        {
            results.Add(dict);
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            Traverse(kids, results);
        }
    }

    private static PdfObject Dereference(PdfObject obj)
        => obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;

    private static string? GetAlt(PdfDictionary structElem)
        => structElem.GetAsString(PdfName.Alt)?.ToUnicodeString();

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

        public string GetFallbackAltTextForImage() => "alt text for image";

        public string GetFallbackAltTextForLink() => "alt text for link";
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

