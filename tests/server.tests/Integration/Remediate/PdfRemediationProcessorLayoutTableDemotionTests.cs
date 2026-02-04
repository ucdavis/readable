using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorLayoutTableDemotionTests
{
    private static readonly PdfName RoleTable = new("Table");

    [Fact]
    public async Task ProcessAsync_WhenSmallTableHasNoHeaders_AndDemotionEnabled_DemotesTableRole()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-layout-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateSmallNoHeaderTablePdf(inputPdfPath);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();
                ListStructElementsByRole(inputPdf, RoleTable).Count.Should().BeGreaterThan(0);
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var opts = Options.Create(new PdfRemediationOptions
            {
                DemoteSmallTablesWithoutHeaders = true,
            });

            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                new ThrowingPdfTitleService(),
                opts,
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ListStructElementsByRole(outputPdf, RoleTable).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateSmallNoHeaderTablePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        // Common false-positive "layout table": 1 row with 2 cells, no header cells.
        var table = new Table(UnitValue.CreatePercentArray([1f, 2f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(new Paragraph("Label")));
        table.AddCell(new Cell().Add(new Paragraph("Value")));
        doc.Add(table);
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
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }

    private sealed class ThrowingAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for layout table demotion tests.");

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for layout table demotion tests.");

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class ThrowingPdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Title service should not be called for layout table demotion tests.");
    }
}

