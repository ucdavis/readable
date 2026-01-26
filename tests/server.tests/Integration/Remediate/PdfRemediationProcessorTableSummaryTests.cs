using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorTableSummaryTests
{
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName AttrOwnerKey = new("O");
    private static readonly PdfName AttrOwnerTable = new("Table");
    private static readonly PdfName AttrSummaryKey = new("Summary");

    [Fact]
    public async Task ProcessAsync_TaggedMissingTableSummary_AddsSummary()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-table-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedTablePdf(inputPdfPath);
            StripAnyExistingTableSummaries(inputPdfPath, Path.Combine(runRoot, "input.no-summary.pdf"));

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();
                var inputTables = ListStructElementsByRole(inputPdf, RoleTable);
                inputTables.Count.Should().BeGreaterThan(0);
                inputTables.Any(t => string.IsNullOrWhiteSpace(GetTableSummary(t))).Should().BeTrue();
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                new ThrowingPdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var outputTables = ListStructElementsByRole(outputPdf, RoleTable);
            outputTables.Count.Should().BeGreaterThan(0);

            foreach (var table in outputTables)
            {
                var summary = GetTableSummary(table);
                summary.Should().NotBeNullOrWhiteSpace();
                summary.Should().Contain("Table with");
                summary.Should().Contain("Name");
                summary.Should().Contain("Age");
            }
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateTaggedTablePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var table = new Table(UnitValue.CreatePercentArray([1f, 1f])).UseAllAvailableWidth();
        table.AddHeaderCell(new Cell().Add(new Paragraph("Name")));
        table.AddHeaderCell(new Cell().Add(new Paragraph("Age")));
        table.AddCell(new Cell().Add(new Paragraph("Alice")));
        table.AddCell(new Cell().Add(new Paragraph("30")));

        doc.Add(table);
    }

    private static void StripAnyExistingTableSummaries(string inputPath, string outputPath)
    {
        using var pdf = new PdfDocument(new PdfReader(inputPath), new PdfWriter(outputPath));
        var tables = ListStructElementsByRole(pdf, RoleTable);
        foreach (var table in tables)
        {
            StripSummary(table);
        }

        pdf.Close();
        File.Copy(outputPath, inputPath, overwrite: true);
    }

    private static void StripSummary(PdfDictionary table)
    {
        var attrs = Dereference(table.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            StripSummaryFromAttributeDict(dict);
            return;
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict)
                {
                    StripSummaryFromAttributeDict(itemDict);
                }
            }
        }
    }

    private static void StripSummaryFromAttributeDict(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        if (owner is not null && AttrOwnerTable.Equals(owner))
        {
            dict.Remove(AttrSummaryKey);
        }
    }

    private static string? GetTableSummary(PdfDictionary table)
    {
        var attrs = Dereference(table.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            return GetSummaryFromAttributeDict(dict);
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict)
                {
                    var summary = GetSummaryFromAttributeDict(itemDict);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        return summary;
                    }
                }
            }
        }

        return null;
    }

    private static string? GetSummaryFromAttributeDict(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        if (owner is null || !AttrOwnerTable.Equals(owner))
        {
            return null;
        }

        return dict.GetAsString(AttrSummaryKey)?.ToUnicodeString();
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
            throw new InvalidOperationException("Alt text service should not be called for table summary tests.");

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for table summary tests.");

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class ThrowingPdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Title service should not be called for table summary tests.");
    }
}

