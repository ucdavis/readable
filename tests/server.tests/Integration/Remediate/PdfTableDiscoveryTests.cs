using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Layout;
using iText.Layout.Element;
using server.core.Remediate;
using server.core.Remediate.Table;

namespace server.tests.Integration.Remediate;

public sealed class PdfTableDiscoveryTests
{
    [Theory]
    [InlineData("small-layout")]
    [InlineData("classified-layout")]
    [InlineData("data")]
    public async Task TableAfterLongTaggedContent_ReachesRemediation_AndPreservesPageContent(string kind)
    {
        using var input = new MemoryStream();
        var inputWriter = new PdfWriter(input);
        inputWriter.SetCloseStream(false);
        using (var pdf = new PdfDocument(inputWriter))
        {
            pdf.SetTagged();
            using var document = new Document(pdf);
            // Paragraph tags, content references and child arrays exceed the former 4,096-step budget.
            for (var i = 0; i < 1800; i++) document.Add(new Paragraph($"Paragraph {i}"));
            var table = new Table(2);
            table.AddCell("Name"); table.AddCell("Value");
            if (kind != "small-layout") { table.AddCell("Example"); table.AddCell("123"); }
            document.Add(table);
        }

        input.Position = 0;
        using var output = new MemoryStream();
        List<byte[]> originalContent;
        List<string> originalText;
        var classifier = new Classifier(kind == "data" ? PdfTableKind.DataTable : PdfTableKind.NotDataTable);
        var outputWriter = new PdfWriter(output);
        outputWriter.SetCloseStream(false);
        using (var pdf = new PdfDocument(new PdfReader(input), outputWriter))
        {
            originalContent = Enumerable.Range(1, pdf.GetNumberOfPages()).Select(n => pdf.GetPage(n).GetContentBytes()).ToList();
            originalText = Enumerable.Range(1, pdf.GetNumberOfPages()).Select(n => PdfTextExtractor.GetTextFromPage(pdf.GetPage(n))).ToList();
            var table = LastTable(pdf);
            var smallDemotions = PdfTableRoleRemediator.DemoteLikelyLayoutTables(pdf, true, CancellationToken.None);
            var decisions = await PdfTableRoleRemediator.RemediateNoHeaderTablesAsync(pdf, classifier, "en", true, true,
                TimeSpan.FromSeconds(5), 1, CancellationToken.None);
            smallDemotions.Should().Be(kind == "small-layout" ? 1 : 0);
            classifier.Calls.Should().Be(kind == "small-layout" ? 0 : 1);
            if (kind != "small-layout")
                decisions.Should().ContainSingle().Which.Action.Should().Be(kind == "data"
                    ? PdfNoHeaderTableRemediationAction.PromotedHeaderRow
                    : PdfNoHeaderTableRemediationAction.DemotedNoHeaderTable);
            table.GetAsName(PdfName.S).Should().Be(kind == "data" ? PdfName.Table : PdfName.Div);
        }

        output.Position = 0;
        using var reopened = new PdfDocument(new PdfReader(output));
        LastTable(reopened).GetAsName(PdfName.S).Should().Be(kind == "data" ? PdfName.Table : PdfName.Div);
        for (var n = 1; n <= reopened.GetNumberOfPages(); n++)
        {
            reopened.GetPage(n).GetContentBytes().Should().Equal(originalContent[n - 1]);
            PdfTextExtractor.GetTextFromPage(reopened.GetPage(n)).Should().Be(originalText[n - 1]);
        }
        // The table is the last direct child of Document, so assertions don't repeat the production traversal.
        static PdfDictionary LastTable(PdfDocument pdf) => pdf.GetStructTreeRoot().GetKids().Single()
            .GetKids().Last() is iText.Kernel.Pdf.Tagging.PdfStructElem element
                ? element.GetPdfObject() : throw new InvalidOperationException("Expected a structure element.");
    }

    [Theory]
    [InlineData("indirect")]
    [InlineData("direct-dictionary")]
    [InlineData("direct-array")]
    public void CyclicPrefix_DoesNotPreventDiscoveryOfLaterTable(string cycleKind)
    {
        using var output = new MemoryStream();
        using var pdf = new PdfDocument(new PdfWriter(output));
        pdf.SetTagged(); pdf.AddNewPage();
        var table = new PdfDictionary(); table.Put(PdfName.S, PdfName.Table); table.MakeIndirect(pdf);
        var cyclic = new PdfDictionary(); cyclic.Put(PdfName.S, PdfName.Div);
        var array = new PdfArray();
        if (cycleKind == "indirect") { cyclic.MakeIndirect(pdf); cyclic.Put(PdfName.K, cyclic.GetIndirectReference()); }
        else if (cycleKind == "direct-dictionary") cyclic.Put(PdfName.K, cyclic);
        else { array.Add(array); cyclic.Put(PdfName.K, array); }
        var root = pdf.GetStructTreeRoot().GetPdfObject();
        root.Put(PdfName.K, new PdfArray(new PdfObject[] { cyclic, table, table }));
        try
        {
            PdfTableRoleRemediator.DemoteLikelyLayoutTables(pdf, true, CancellationToken.None).Should().Be(1);
            table.GetAsName(PdfName.S).Should().Be(PdfName.Div);
        }
        finally
        {
            // Direct cycles cannot be serialized as a PDF; the test targets malformed in-memory structure.
            array.Clear(); cyclic.Remove(PdfName.K); root.Remove(PdfName.K);
        }
    }

    private sealed class Classifier(PdfTableKind kind) : IPdfTableClassificationService
    {
        public int Calls { get; private set; }
        public Task<PdfTableClassificationResult> ClassifyAsync(PdfTableClassificationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            request.Rows.Should().HaveCount(2);
            request.Rows[0].Should().Equal("Name", "Value");
            request.Rows[1].Should().Equal("Example", "123");
            return Task.FromResult(new PdfTableClassificationResult(kind, 1, "Deterministic regression result"));
        }
    }
}
