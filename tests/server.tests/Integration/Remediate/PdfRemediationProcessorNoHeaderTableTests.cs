using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Rasterize;
using server.core.Remediate.Table;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorNoHeaderTableTests
{
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    private static readonly PdfName AttrOwnerKey = new("O");
    private static readonly PdfName AttrOwnerTable = new("Table");
    private static readonly PdfName AttrScopeKey = new("Scope");
    private static readonly PdfName AttrScopeColumn = new("Column");
    private static readonly PdfName AttrHeadersKey = new("Headers");
    private static readonly PdfName StructElemIdKey = new("ID");

    [Fact]
    public async Task ProcessAsync_NoHeaderDataTable_PromotesFirstRowCellsToColumnHeaders()
    {
        await RunPdfTestAsync(
            "no-header-data-table",
            CreateNoHeaderServiceDatesTablePdf,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.DataTable, 0.95, "Looks like a data table."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);

                var headers = ListStructElementsByRole(outputPdf, RoleTh);
                headers.Should().HaveCount(3);
                headers.Should().OnlyContain(header => HasColumnScope(header));
                headers.Should().OnlyContain(header => !string.IsNullOrWhiteSpace(GetHeaderId(header)));
                ListStructElementsByRole(outputPdf, RoleTd)
                    .Should()
                    .OnlyContain(cell => HasHeaderReference(cell));
            });
    }

    [Fact]
    public async Task ProcessAsync_NoHeaderFormLayoutTable_DemotesTableRoles()
    {
        await RunPdfTestAsync(
            "no-header-form-layout-table",
            CreateNoHeaderContactFormTablePdf,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.NotDataTable, 0.95, "Does not look like a data table."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTd).Should().BeEmpty();
            });
    }

    [Fact]
    public async Task ProcessAsync_TableAlreadyContainingHeaders_LeavesHeadersUnchanged()
    {
        await RunPdfTestAsync(
            "table-with-existing-headers",
            CreateTableWithHeadersPdf,
            new QueuePdfTableClassificationService([]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTh).Should().HaveCount(2);
            });
    }

    [Fact]
    public async Task ProcessAsync_AmbiguousNoHeaderTable_DefaultsToNonDataAndDemotesTable()
    {
        await RunPdfTestAsync(
            "ambiguous-no-header-table",
            CreateAmbiguousNoHeaderTablePdf,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.NotDataTable, 0.3, "Low confidence that this is a data table."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTh).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTd).Should().BeEmpty();
            });
    }

    [Fact]
    public async Task ProcessAsync_LowConfidenceDataTable_DefaultsToNonDataAndDemotesTable()
    {
        await RunPdfTestAsync(
            "low-confidence-data-table",
            CreateNoHeaderServiceDatesTablePdf,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.DataTable, 0.4, "Possibly data, but confidence is low."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTh).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTd).Should().BeEmpty();
            });
    }

    [Fact]
    public async Task ProcessAsync_VerificationPdfShape_DemotesFormTableAndPromotesServiceDateHeaders()
    {
        await RunPdfTestAsync(
            "verification-pdf-shape",
            CreateVerificationPdfShape,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.NotDataTable, 0.95, "Top table is not a data table."),
                new PdfTableClassificationResult(PdfTableKind.DataTable, 0.95, "Service dates are tabular records."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);

                var headers = ListStructElementsByRole(outputPdf, RoleTh);
                headers.Should().HaveCount(3);
                headers.Should().OnlyContain(header => HasColumnScope(header));
                headers.Should().OnlyContain(header => !string.IsNullOrWhiteSpace(GetHeaderId(header)));
                ListStructElementsByRole(outputPdf, RoleTd)
                    .Should()
                    .OnlyContain(cell => HasHeaderReference(cell));
            });
    }

    private static async Task RunPdfTestAsync(
        string testName,
        Action<string> createPdf,
        QueuePdfTableClassificationService tableClassificationService,
        Action<PdfDocument> assert)
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            createPdf(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                NoopPdfPageRasterizer.Instance,
                tableClassificationService,
                new StablePdfTitleService(),
                Options.Create(new PdfRemediationOptions()),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: testName,
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            assert(outputPdf);
            tableClassificationService.AssertAllResponsesConsumed();
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static void CreateNoHeaderServiceDatesTablePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var table = new Table(UnitValue.CreatePercentArray([1f, 1f, 1f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(new Paragraph("From / To")));
        table.AddCell(new Cell().Add(new Paragraph("Department")));
        table.AddCell(new Cell().Add(new Paragraph("Status (Staff, Academic)")));
        table.AddCell(new Cell().Add(new Paragraph("Jan 2024 - Feb 2024")));
        table.AddCell(new Cell().Add(new Paragraph("Human Resources")));
        table.AddCell(new Cell().Add(new Paragraph("Staff")));
        table.AddCell(new Cell().Add(new Paragraph("Mar 2024 - Apr 2024")));
        table.AddCell(new Cell().Add(new Paragraph("Finance")));
        table.AddCell(new Cell().Add(new Paragraph("Academic")));
        doc.Add(table);
    }

    private static void CreateNoHeaderContactFormTablePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);
        AddContactFormTable(doc);
    }

    private static void CreateTableWithHeadersPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var table = new Table(UnitValue.CreatePercentArray([1f, 1f])).UseAllAvailableWidth();
        var nameHeader = new Cell().Add(new Paragraph("Name"));
        nameHeader.GetAccessibilityProperties().SetRole("TH");
        var amountHeader = new Cell().Add(new Paragraph("Amount"));
        amountHeader.GetAccessibilityProperties().SetRole("TH");
        table.AddCell(nameHeader);
        table.AddCell(amountHeader);
        table.AddCell(new Cell().Add(new Paragraph("Alice")));
        table.AddCell(new Cell().Add(new Paragraph("$10")));
        doc.Add(table);
    }

    private static void CreateAmbiguousNoHeaderTablePdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var table = new Table(UnitValue.CreatePercentArray([1f, 1f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(new Paragraph("Alpha")));
        table.AddCell(new Cell().Add(new Paragraph("Beta")));
        table.AddCell(new Cell().Add(new Paragraph("Gamma")));
        table.AddCell(new Cell().Add(new Paragraph("Delta")));
        doc.Add(table);
    }

    private static void CreateVerificationPdfShape(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        AddContactFormTable(doc);
        doc.Add(new Paragraph("Service Dates"));
        AddServiceDatesFormTable(doc);
    }

    private static void AddContactFormTable(Document doc)
    {
        var table = new Table(UnitValue.CreatePercentArray([1f, 1f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(new Paragraph("TO BE COMPLETED BY EMPLOYEE")));
        table.AddCell(new Cell().Add(new Paragraph("Contact information for PREVIOUS employer")));
        table.AddCell(new Cell().Add(new Paragraph("Payroll processor in CURRENT UCD Department")));
        table.AddCell(new Cell().Add(new Paragraph("Attention / Name")));
        table.AddCell(new Cell().Add(new Paragraph("Employer / Dept")));
        table.AddCell(new Cell().Add(new Paragraph("Address / Address")));
        table.AddCell(new Cell().Add(new Paragraph("Phone")));
        table.AddCell(new Cell().Add(new Paragraph("Email")));
        table.AddCell(new Cell().Add(new Paragraph("Signature")));
        table.AddCell(new Cell().Add(new Paragraph("Date")));
        doc.Add(table);
    }

    private static void AddServiceDatesFormTable(Document doc)
    {
        var table = new Table(UnitValue.CreatePercentArray([1f, 1f, 1f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(new Paragraph("From / To")));
        table.AddCell(new Cell().Add(new Paragraph("Department")));
        table.AddCell(new Cell().Add(new Paragraph("Status (Staff, Academic)")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        table.AddCell(new Cell().Add(new Paragraph("")));
        doc.Add(table);
    }

    private static bool HasColumnScope(PdfDictionary structElem)
    {
        var attrs = Dereference(structElem.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            return HasColumnScopeAttribute(dict);
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict && HasColumnScopeAttribute(itemDict))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasColumnScopeAttribute(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        var scope = dict.GetAsName(AttrScopeKey);
        return AttrOwnerTable.Equals(owner) && AttrScopeColumn.Equals(scope);
    }

    private static string? GetHeaderId(PdfDictionary structElem) =>
        structElem.GetAsString(StructElemIdKey)?.ToUnicodeString();

    private static bool HasHeaderReference(PdfDictionary structElem)
    {
        var attrs = Dereference(structElem.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            return HasHeaderReferenceAttribute(dict);
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict && HasHeaderReferenceAttribute(itemDict))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasHeaderReferenceAttribute(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        if (!AttrOwnerTable.Equals(owner))
        {
            return false;
        }

        var headers = Dereference(dict.Get(AttrHeadersKey));
        return headers is PdfArray array && array.Size() > 0;
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
        var dereferenced = Dereference(node);
        if (dereferenced is null)
        {
            return;
        }

        node = dereferenced;

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

    private static PdfObject? Dereference(PdfObject? obj)
    {
        if (obj is null)
        {
            return null;
        }

        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }

    private sealed class ThrowingAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for no-header table tests.");

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for no-header table tests.");

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class QueuePdfTableClassificationService : IPdfTableClassificationService
    {
        private readonly Queue<PdfTableClassificationResult> _responses;

        public QueuePdfTableClassificationService(IEnumerable<PdfTableClassificationResult> responses)
        {
            _responses = new Queue<PdfTableClassificationResult>(responses);
        }

        public Task<PdfTableClassificationResult> ClassifyAsync(
            PdfTableClassificationRequest request,
            CancellationToken cancellationToken)
        {
            request.RowCount.Should().BeGreaterThanOrEqualTo(2);
            request.MaxColumnCount.Should().BeGreaterThanOrEqualTo(2);
            request.Rows.Should().NotBeEmpty();
            cancellationToken.ThrowIfCancellationRequested();

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Table classifier was called more times than expected.");
            }

            return Task.FromResult(_responses.Dequeue());
        }

        public void AssertAllResponsesConsumed() => _responses.Should().BeEmpty();
    }

    private sealed class StablePdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("Generated test document title");
    }
}
