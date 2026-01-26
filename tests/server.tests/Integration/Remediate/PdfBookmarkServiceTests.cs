using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Layout.Properties;
using iText.Layout.Element;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate.Bookmarks;

namespace server.tests.Integration.Remediate;

public sealed class PdfBookmarkServiceTests
{
    [Fact]
    public async Task EnsureBookmarksAsync_TaggedMissingBookmarksFixture_AddsOutlines()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-bookmarks.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
            HasOutlines(inputPdf).Should().BeFalse("fixture should not already have bookmarks");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-bookmarks-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
                await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
            }

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            HasOutlines(outputPdf).Should().BeTrue("bookmark remediation should add outlines");

            var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);
            outlineRoot.GetAllChildren().Count.Should().BeGreaterThan(0);

            var titles = ListOutlineTitles(outlineRoot).ToArray();
            titles.Should().Contain(t => t.Contains("delayed", StringComparison.OrdinalIgnoreCase));
            titles.Should().NotContain(t => t.Contains("dela y", StringComparison.OrdinalIgnoreCase));
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
    public async Task EnsureBookmarksAsync_TaggedFixtureWithExistingOutlines_DoesNotRemoveOutlines()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-missing-alt.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var inputOutlineRootChildCount = 0;
        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
            HasOutlines(inputPdf).Should().BeTrue("fixture should already have bookmarks");

            var outlineRoot = inputPdf.GetOutlines(updateOutlines: true);
            inputOutlineRootChildCount = outlineRoot.GetAllChildren().Count;
            inputOutlineRootChildCount.Should().BeGreaterThan(0);
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-bookmarks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
                await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
            }

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            HasOutlines(outputPdf).Should().BeTrue("existing outlines should be preserved");

            var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);
            outlineRoot.GetAllChildren().Count.Should().Be(inputOutlineRootChildCount);
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
    public async Task EnsureBookmarksAsync_TaggedWithHeadingsAndNoOutlines_AddsOutlines()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-bookmarks-tagged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithHeadings(inputPdfPath);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue("input should be tagged");
                HasOutlines(inputPdf).Should().BeFalse("input should not already have outlines");
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
                await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
            }

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            HasOutlines(outputPdf).Should().BeTrue("bookmark remediation should add outlines");

            var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);
            var rootTitles = outlineRoot.GetAllChildren().Select(c => c.GetTitle()).ToArray();
            rootTitles.Should().Contain("Chapter 1");
            rootTitles.Should().Contain("Chapter 2");
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
    public async Task EnsureBookmarksAsync_FallsBackToSections_WhenHeadingsAreTooSparse()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-bookmarks-sections-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateTaggedPdfWithSparseHeadingsAndSections(inputPdfPath, pageCount: 10);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeTrue("input should be tagged");
                HasOutlines(inputPdf).Should().BeFalse("input should not already have outlines");
                inputPdf.GetNumberOfPages().Should().Be(10);
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
                await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
            }

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            HasOutlines(outputPdf).Should().BeTrue("bookmark remediation should add outlines");

            var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);
            var titles = outlineRoot.GetAllChildren().Select(c => c.GetTitle()).ToArray();
            titles.Should().Contain("Section 1");
            titles.Should().Contain("Section 5");
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
    public async Task EnsureBookmarksAsync_Untagged_DoesNothing()
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-bookmarks-untagged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreateUntaggedPdf(inputPdfPath);

            using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
            {
                inputPdf.IsTagged().Should().BeFalse("input should be untagged");
                HasOutlines(inputPdf).Should().BeFalse("input should not already have outlines");
            }

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");

            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
                await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
            }

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            HasOutlines(outputPdf).Should().BeFalse("untagged PDFs should be left unchanged");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
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

    private static bool HasOutlines(PdfDocument pdf)
    {
        var catalog = pdf.GetCatalog().GetPdfObject();
        var outlinesRoot = catalog.GetAsDictionary(PdfName.Outlines);
        if (outlinesRoot is null)
        {
            return false;
        }

        var first = outlinesRoot.Get(PdfName.First);
        return first is not null && first is not PdfNull;
    }

    private static void CreateUntaggedPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        using var doc = new Document(pdf);
        doc.Add(new Paragraph("Hello world"));
    }

    private static void CreateTaggedPdfWithHeadings(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var h1 = new Paragraph("Chapter 1");
        h1.GetAccessibilityProperties().SetRole("H1");
        doc.Add(h1);
        doc.Add(new Paragraph("Body text for chapter 1."));

        var h2 = new Paragraph("Section 1.1");
        h2.GetAccessibilityProperties().SetRole("H2");
        doc.Add(h2);
        doc.Add(new Paragraph("More body text."));

        var h1b = new Paragraph("Chapter 2");
        h1b.GetAccessibilityProperties().SetRole("H1");
        doc.Add(h1b);
        doc.Add(new Paragraph("Body text for chapter 2."));
    }

    private static void CreateTaggedPdfWithSparseHeadingsAndSections(string path, int pageCount)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        using var doc = new Document(pdf);

        var intro = new Paragraph("Intro");
        intro.GetAccessibilityProperties().SetRole("H1");
        doc.Add(intro);

        for (var i = 1; i <= pageCount; i++)
        {
            if (i > 1)
            {
                doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            var section = new Paragraph($"Section {i}");
            section.GetAccessibilityProperties().SetRole("Sect");
            doc.Add(section);
            doc.Add(new Paragraph($"Body text for section {i}."));
        }
    }

    private static IEnumerable<string> ListOutlineTitles(PdfOutline outline)
    {
        foreach (var child in outline.GetAllChildren())
        {
            var title = child.GetTitle();
            if (!string.IsNullOrWhiteSpace(title))
            {
                yield return title;
            }

            foreach (var nested in ListOutlineTitles(child))
            {
                yield return nested;
            }
        }
    }
}
