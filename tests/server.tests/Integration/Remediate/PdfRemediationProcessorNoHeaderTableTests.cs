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
    private static readonly PdfName RoleTHead = new("THead");
    private static readonly PdfName RoleTBody = new("TBody");
    private static readonly PdfName RoleTFoot = new("TFoot");
    private static readonly PdfName RoleTr = new("TR");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    [Fact]
    public async Task ProcessAsync_NoHeaderDataTable_MovesFirstRowIntoTableHead()
    {
        var tableClassificationService = new QueuePdfTableClassificationService([
            new PdfTableClassificationResult(PdfTableKind.DataTable, 0.95, "Looks like a data table."),
        ]);

        await RunPdfTestAsync(
            "no-header-data-table",
            CreateNoHeaderServiceDatesTablePdf,
            tableClassificationService,
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTHead).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTBody).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTr).Should().HaveCount(3);
                ListStructElementsByRole(outputPdf, RoleTh).Should().HaveCount(3);
                ListStructElementsByRole(outputPdf, RoleTd).Should().HaveCount(6);
                AssertPromotedHeaderStructure(outputPdf);
            });

        tableClassificationService.Requests.Should().ContainSingle();
        var request = tableClassificationService.Requests.Single();
        request.RowCount.Should().Be(3);
        request.MaxColumnCount.Should().Be(3);
        request.HasNestedTable.Should().BeFalse();
        request.Rows.Should().HaveCount(3);
        request.Rows[0].Should().Equal("From / To", "Department", "Status (Staff, Academic)");
        request.Rows[1].Should().Equal("Jan 2024 - Feb 2024", "Human Resources", "Staff");
        request.Rows[2].Should().Equal("Mar 2024 - Apr 2024", "Finance", "Academic");
    }

    [Fact]
    public async Task ProcessAsync_NoHeaderDataTable_WhenHeaderPromotionDisabled_LeavesTableUnchanged()
    {
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "readable-tests",
            $"no-header-data-table-promotion-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateNoHeaderServiceDatesTablePdf(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var tableClassificationService = new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.DataTable, 0.95, "Looks like a data table."),
            ]);
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                NoopPdfPageRasterizer.Instance,
                tableClassificationService,
                new StablePdfTitleService(),
                Options.Create(new PdfRemediationOptions
                {
                    PromoteFirstRowHeadersForNoHeaderTables = false,
                }),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "no-header-data-table-promotion-disabled",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);
            ListStructElementsByRole(outputPdf, RoleTHead).Should().BeEmpty();
            ListStructElementsByRole(outputPdf, RoleTBody).Should().BeEmpty();
            ListStructElementsByRole(outputPdf, RoleTr).Should().HaveCount(3);
            ListStructElementsByRole(outputPdf, RoleTh).Should().BeEmpty();
            ListStructElementsByRole(outputPdf, RoleTd).Should().HaveCount(9);
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
    public async Task ProcessAsync_WhenDemoteNoHeaderTablesDisabled_LeavesEligibleNoHeaderTableUnchanged()
    {
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "readable-tests",
            $"no-header-demotion-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateNoHeaderContactFormTablePdf(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var tableClassificationService = new QueuePdfTableClassificationService([]);
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                NoopPdfPageRasterizer.Instance,
                tableClassificationService,
                new StablePdfTitleService(),
                Options.Create(new PdfRemediationOptions
                {
                    DemoteNoHeaderTables = false,
                }),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "no-header-demotion-disabled",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);
            ListStructElementsByRole(outputPdf, RoleTr).Should().HaveCount(5);
            ListStructElementsByRole(outputPdf, RoleTh).Should().BeEmpty();
            ListStructElementsByRole(outputPdf, RoleTd).Should().HaveCount(10);
            tableClassificationService.Requests.Should().BeEmpty();
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

    [Fact]
    public async Task ProcessAsync_NoHeaderSectionedFormLayoutTable_DemotesTableSectionRoles()
    {
        await RunPdfTestAsync(
            "no-header-sectioned-form-layout-table",
            CreateSectionedNoHeaderContactFormTablePdf,
            new QueuePdfTableClassificationService([
                new PdfTableClassificationResult(PdfTableKind.NotDataTable, 0.95, "Does not look like a data table."),
            ]),
            outputPdf =>
            {
                ListStructElementsByRole(outputPdf, RoleTable).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTHead).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTBody).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTFoot).Should().BeEmpty();
                ListStructElementsByRole(outputPdf, RoleTr).Should().BeEmpty();
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
    public async Task ProcessAsync_NoHeaderTableClassificationTimeout_LeavesTableUnchanged()
    {
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "readable-tests",
            $"no-header-classification-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateNoHeaderContactFormTablePdf(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var tableClassificationService = new NeverCompletingPdfTableClassificationService();
            var sut = new PdfRemediationProcessor(
                new ThrowingAltTextService(),
                new NoopPdfBookmarkService(),
                NoopPdfPageRasterizer.Instance,
                tableClassificationService,
                new StablePdfTitleService(),
                Options.Create(new PdfRemediationOptions
                {
                    NoHeaderTableClassificationTimeoutSeconds = 1,
                }),
                NullLogger<PdfRemediationProcessor>.Instance);

            await sut.ProcessAsync(
                fileId: "no-header-classification-timeout",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            tableClassificationService.CallCount.Should().Be(1);
            ListStructElementsByRole(outputPdf, RoleTable).Should().HaveCount(1);
            ListStructElementsByRole(outputPdf, RoleTr).Should().NotBeEmpty();
            ListStructElementsByRole(outputPdf, RoleTd).Should().NotBeEmpty();
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
    public async Task ProcessAsync_VerificationPdfShape_DemotesFormTableAndAddsDataTableHeader()
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
                ListStructElementsByRole(outputPdf, RoleTHead).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTBody).Should().HaveCount(1);
                ListStructElementsByRole(outputPdf, RoleTr).Should().HaveCount(4);
                ListStructElementsByRole(outputPdf, RoleTh).Should().HaveCount(3);
                ListStructElementsByRole(outputPdf, RoleTd).Should().HaveCount(9);
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

    private static void CreateSectionedNoHeaderContactFormTablePdf(string path)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(path) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(path)}.base.pdf");

        try
        {
            CreateNoHeaderContactFormTablePdf(basePath);

            using var pdf = new PdfDocument(new PdfReader(basePath), new PdfWriter(path));
            WrapFirstTableRowsInSections(pdf);
        }
        finally
        {
            if (File.Exists(basePath))
            {
                File.Delete(basePath);
            }
        }
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

    private static void WrapFirstTableRowsInSections(PdfDocument pdf)
    {
        var table = ListStructElementsByRole(pdf, RoleTable).Single();
        var kids = Dereference(table.Get(PdfName.K)) as PdfArray
            ?? throw new InvalidOperationException("Expected table children to be an array.");

        var rowObjects = new List<PdfObject>();
        foreach (var item in kids)
        {
            if (Dereference(item) is PdfDictionary dict && RoleTr.Equals(dict.GetAsName(PdfName.S)))
            {
                rowObjects.Add(item);
            }
        }

        rowObjects.Count.Should().BeGreaterThanOrEqualTo(3);

        var thead = CreateStructElement(RoleTHead, table);
        var tbody = CreateStructElement(RoleTBody, table);
        var tfoot = CreateStructElement(RoleTFoot, table);

        AddRowsToSection(thead, rowObjects.Take(1));
        AddRowsToSection(tbody, rowObjects.Skip(1).Take(rowObjects.Count - 2));
        AddRowsToSection(tfoot, rowObjects.TakeLast(1));

        var sectionedKids = new PdfArray();
        sectionedKids.Add(thead);
        sectionedKids.Add(tbody);
        sectionedKids.Add(tfoot);
        table.Put(PdfName.K, sectionedKids);
    }

    private static PdfDictionary CreateStructElement(PdfName role, PdfDictionary parent)
    {
        var structElem = new PdfDictionary();
        structElem.Put(PdfName.Type, new PdfName("StructElem"));
        structElem.Put(PdfName.S, role);
        structElem.Put(PdfName.P, parent);

        var page = parent.Get(PdfName.Pg);
        if (page is not null)
        {
            structElem.Put(PdfName.Pg, page);
        }

        return structElem;
    }

    private static void AddRowsToSection(PdfDictionary section, IEnumerable<PdfObject> rows)
    {
        var kids = new PdfArray();
        foreach (var row in rows)
        {
            kids.Add(row);
            if (Dereference(row) is PdfDictionary rowDict)
            {
                rowDict.Put(PdfName.P, section);
            }
        }

        section.Put(PdfName.K, kids);
    }

    private static void AssertPromotedHeaderStructure(PdfDocument pdf)
    {
        var table = ListStructElementsByRole(pdf, RoleTable).Single();
        var tableChildren = ListDirectStructElementChildren(table);
        tableChildren.Select(c => c.GetAsName(PdfName.S)).Should().Equal(RoleTHead, RoleTBody);

        var thead = tableChildren[0];
        var tbody = tableChildren[1];
        IsSameStructElement(thead.Get(PdfName.P), table).Should().BeTrue();
        IsSameStructElement(tbody.Get(PdfName.P), table).Should().BeTrue();

        var headerRows = ListDirectStructElementChildren(thead);
        headerRows.Should().ContainSingle();
        IsSameStructElement(headerRows[0].Get(PdfName.P), thead).Should().BeTrue();
        ListDirectStructElementChildren(headerRows[0])
            .Select(c => c.GetAsName(PdfName.S))
            .Should()
            .Equal(RoleTh, RoleTh, RoleTh);

        var bodyRows = ListDirectStructElementChildren(tbody);
        bodyRows.Should().HaveCount(2);
        bodyRows.Should().OnlyContain(row => IsSameStructElement(row.Get(PdfName.P), tbody));
        foreach (var row in bodyRows)
        {
            ListDirectStructElementChildren(row)
                .Select(c => c.GetAsName(PdfName.S))
                .Should()
                .Equal(RoleTd, RoleTd, RoleTd);
        }
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

    private static List<PdfDictionary> ListDirectStructElementChildren(PdfDictionary structElem)
    {
        var kids = structElem.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var dereferenced = Dereference(kids);
        if (dereferenced is PdfDictionary kidDict)
        {
            return kidDict.ContainsKey(PdfName.S) ? [kidDict] : [];
        }

        if (dereferenced is not PdfArray array)
        {
            return [];
        }

        var results = new List<PdfDictionary>(array.Size());
        foreach (var item in array)
        {
            if (Dereference(item) is PdfDictionary itemDict && itemDict.ContainsKey(PdfName.S))
            {
                results.Add(itemDict);
            }
        }

        return results;
    }

    private static bool IsSameStructElement(PdfObject? candidate, PdfDictionary target)
    {
        var targetRef = target.GetIndirectReference();
        if (targetRef is not null && candidate is PdfIndirectReference candidateRef)
        {
            return candidateRef.GetObjNumber() == targetRef.GetObjNumber()
                && candidateRef.GetGenNumber() == targetRef.GetGenNumber();
        }

        var candidateDict = Dereference(candidate) as PdfDictionary;
        if (candidateDict is null)
        {
            return false;
        }

        if (ReferenceEquals(candidateDict, target))
        {
            return true;
        }

        var candidateRefFromDict = candidateDict.GetIndirectReference();
        return targetRef is not null
            && candidateRefFromDict is not null
            && candidateRefFromDict.GetObjNumber() == targetRef.GetObjNumber()
            && candidateRefFromDict.GetGenNumber() == targetRef.GetGenNumber();
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
        private readonly List<PdfTableClassificationRequest> _requests = [];

        public QueuePdfTableClassificationService(IEnumerable<PdfTableClassificationResult> responses)
        {
            _responses = new Queue<PdfTableClassificationResult>(responses);
        }

        public IReadOnlyList<PdfTableClassificationRequest> Requests => _requests;

        public Task<PdfTableClassificationResult> ClassifyAsync(
            PdfTableClassificationRequest request,
            CancellationToken cancellationToken)
        {
            request.RowCount.Should().BeGreaterThanOrEqualTo(2);
            request.MaxColumnCount.Should().BeGreaterThanOrEqualTo(2);
            request.Rows.Should().NotBeEmpty();
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Table classifier was called more times than expected.");
            }

            return Task.FromResult(_responses.Dequeue());
        }

        public void AssertAllResponsesConsumed() => _responses.Should().BeEmpty();
    }

    private sealed class NeverCompletingPdfTableClassificationService : IPdfTableClassificationService
    {
        public int CallCount { get; private set; }

        public Task<PdfTableClassificationResult> ClassifyAsync(
            PdfTableClassificationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return new TaskCompletionSource<PdfTableClassificationResult>().Task;
        }
    }

    private sealed class StablePdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("Generated test document title");
    }
}
