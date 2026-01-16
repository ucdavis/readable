using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate.Bookmarks;

namespace server.tests.Integration.Remediate;

public sealed class PdfBookmarkServiceTests
{
    [Fact]
    public async Task EnsureBookmarksAsync_TaggedMissingBookmarks_AddsOutlines()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "needs-bookmarks.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        using (var inputPdf = new PdfDocument(new PdfReader(inputPdfPath)))
        {
            inputPdf.IsTagged().Should().BeTrue("fixture should be tagged");
            HasOutlines(inputPdf).Should().BeFalse("fixture should not already have bookmarks");
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
            HasOutlines(outputPdf).Should().BeTrue("bookmark remediation should add outlines");

            var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);
            outlineRoot.GetAllChildren().Count.Should().BeGreaterThan(0);
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
        => pdf.GetCatalog().GetPdfObject().ContainsKey(PdfName.Outlines);

    private static void CreateUntaggedPdf(string path)
    {
        using var writer = new PdfWriter(path);
        using var pdf = new PdfDocument(writer);
        using var doc = new Document(pdf);
        doc.Add(new Paragraph("Hello world"));
    }
}
