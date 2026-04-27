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
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorHeadingTests
{
    private static readonly PdfName RoleH1 = new("H1");
    private static readonly PdfName RoleH2 = new("H2");
    private static readonly PdfName RoleH3 = new("H3");
    private static readonly PdfName RoleH5 = new("H5");
    private static readonly PdfName RoleP = new("P");
    private static readonly PdfName RoleTd = new("TD");

    [Fact]
    public async Task ProcessAsync_WhenSkippedHeadingLooksLikeTableLabel_DemotesToParagraph()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-headings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithSkippedTableHeading(inputPdfPath);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue();
                ListStructElementsByRole(inputPdf, RoleTd)
                    .Count(td => HasDescendantWithRole(td, RoleH5))
                    .Should()
                    .Be(2);
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var sut = CreateProcessor();

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var outputCells = ListStructElementsByRole(outputPdf, RoleTd);
            outputCells.Count(td => HasDescendantWithRole(td, RoleP)).Should().Be(2);
            outputCells.Should().NotContain(td => HasDescendantWithRole(td, RoleH5));

            ListStructElementsByRole(outputPdf, RoleH1).Should().HaveCount(2);
            ListStructElementsByRole(outputPdf, RoleH2).Should().HaveCount(2);
            ListStructElementsByRole(outputPdf, RoleH3).Should().HaveCount(1);
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
    public async Task ProcessAsync_WhenSkippedHeadingIsOutsideTable_DoesNotDemote()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-heading-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithSkippedHeadingOutsideTable(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            var sut = CreateProcessor();

            await sut.ProcessAsync(
                fileId: "fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ListStructElementsByRole(outputPdf, RoleH5).Should().HaveCount(1);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private static PdfRemediationProcessor CreateProcessor()
    {
        var opts = Options.Create(new PdfRemediationOptions
        {
            DemoteSmallTablesWithoutHeaders = false,
        });

        return new PdfRemediationProcessor(
            new ThrowingAltTextService(),
            new NoopPdfBookmarkService(),
            NoopPdfPageRasterizer.Instance,
            new ThrowingPdfTitleService(),
            opts,
            NullLogger<PdfRemediationProcessor>.Instance);
    }

    private static void CreateTaggedPdfWithSkippedTableHeading(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);
        doc.Add(Heading("Document Title", "H1"));

        var table = new Table(UnitValue.CreatePercentArray([1f])).UseAllAvailableWidth();
        table.AddCell(new Cell().Add(Heading("Location Contact Name:", "H5")));
        table.AddCell(new Cell().Add(Heading("Locat ion Conta ct Name:", "H5")));
        doc.Add(table);

        doc.Add(Heading("Instructions", "H1"));
        doc.Add(Heading("Complete Section 1", "H2"));
        doc.Add(Heading("Complete Section 4", "H2"));
        doc.Add(Heading("Routing Instructions", "H3"));
    }

    private static void CreateTaggedPdfWithSkippedHeadingOutsideTable(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);
        doc.Add(Heading("Document Title", "H1"));
        doc.Add(Heading("Standalone Deep Heading", "H5"));
    }

    private static Paragraph Heading(string text, string role)
    {
        var paragraph = new Paragraph(text);
        paragraph.GetAccessibilityProperties().SetRole(role);
        return paragraph;
    }

    private static bool HasDescendantWithRole(PdfDictionary structElem, PdfName role)
    {
        var kids = structElem.Get(PdfName.K);
        return kids is not null && HasDescendantWithRoleRecursive(kids, role);
    }

    private static bool HasDescendantWithRoleRecursive(PdfObject node, PdfName role)
    {
        node = Dereference(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                if (HasDescendantWithRoleRecursive(item, role))
                {
                    return true;
                }
            }

            return false;
        }

        if (node is not PdfDictionary dict)
        {
            return false;
        }

        if (role.Equals(dict.GetAsName(PdfName.S)))
        {
            return true;
        }

        var kids = dict.Get(PdfName.K);
        return kids is not null && HasDescendantWithRoleRecursive(kids, role);
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

        if (role.Equals(dict.GetAsName(PdfName.S)))
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
            throw new InvalidOperationException("Alt text service should not be called for heading remediation tests.");

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Alt text service should not be called for heading remediation tests.");

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class ThrowingPdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Title service should not be called for heading remediation tests.");
    }
}
