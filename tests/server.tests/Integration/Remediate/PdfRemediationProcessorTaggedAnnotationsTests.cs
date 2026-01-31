using FluentAssertions;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorTaggedAnnotationsTests
{
    [Fact]
    public async Task ProcessAsync_WhenPdfHasUntaggedAnnotations_RemovesThoseAnnotations()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "tagged-bad-annotations.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var before = ReadAnnotationStats(inputPdfPath);
        before.TotalAnnotations.Should().BeGreaterThan(0);
        before.UntaggedAnnotations.Should().BeGreaterThan(0);

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-annots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
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

            File.Exists(outputPdfPath).Should().BeTrue($"output should exist at {outputPdfPath}");

            var after = ReadAnnotationStats(outputPdfPath);
            after.UntaggedAnnotations.Should().Be(0);
            after.TotalAnnotations.Should().Be(before.TaggedAnnotations);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed record AnnotationStats(int TotalAnnotations, int TaggedAnnotations, int UntaggedAnnotations);

    private static AnnotationStats ReadAnnotationStats(string pdfPath)
    {
        var total = 0;
        var tagged = 0;
        var untagged = 0;

        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        pdf.IsTagged().Should().BeTrue();

        var parentTree = TryGetParentTree(pdf);

        var structParentKey = new PdfName("StructParent");

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            var page = pdf.GetPage(pageNumber);

            foreach (var annotation in page.GetAnnotations())
            {
                total++;

                var structParent = annotation.GetPdfObject().GetAsNumber(structParentKey)?.IntValue();
                if (structParent is null)
                {
                    untagged++;
                    continue;
                }

                if (parentTree is not null && !NumberTreeContainsKey(parentTree, structParent.Value))
                {
                    untagged++;
                    continue;
                }

                tagged++;
            }
        }

        return new AnnotationStats(total, tagged, untagged);
    }

    private static PdfDictionary? TryGetParentTree(PdfDocument pdf)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        return structTreeRootDict?.GetAsDictionary(PdfName.ParentTree);
    }

    private static bool NumberTreeContainsKey(PdfDictionary numberTree, int key)
    {
        var visited = new HashSet<(int objNum, int genNum)>();
        return NumberTreeContainsKeyRecursive(numberTree, key, visited);
    }

    private static bool NumberTreeContainsKeyRecursive(
        PdfDictionary node,
        int key,
        HashSet<(int objNum, int genNum)> visited)
    {
        var nodeRef = node.GetIndirectReference();
        if (nodeRef is not null)
        {
            var refKey = (nodeRef.GetObjNumber(), nodeRef.GetGenNumber());
            if (!visited.Add(refKey))
            {
                return false;
            }
        }

        var nums = node.GetAsArray(PdfName.Nums);
        if (nums is not null)
        {
            for (var i = 0; i + 1 < nums.Size(); i += 2)
            {
                if (nums.GetAsNumber(i)?.IntValue() == key)
                {
                    return true;
                }
            }

            return false;
        }

        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null)
        {
            return false;
        }

        for (var i = 0; i < kids.Size(); i++)
        {
            var kidObj = kids.Get(i);
            kidObj = Dereference(kidObj, visited);

            if (kidObj is not PdfDictionary kidDict)
            {
                continue;
            }

            var limits = kidDict.GetAsArray(PdfName.Limits);
            if (limits is not null && limits.Size() >= 2)
            {
                var low = limits.GetAsNumber(0)?.IntValue();
                var high = limits.GetAsNumber(1)?.IntValue();
                if (low is not null && high is not null && (key < low.Value || key > high.Value))
                {
                    continue;
                }
            }

            if (NumberTreeContainsKeyRecursive(kidDict, key, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static PdfObject Dereference(PdfObject obj, HashSet<(int objNum, int genNum)> visited)
    {
        if (obj is PdfIndirectReference reference)
        {
            var key = (reference.GetObjNumber(), reference.GetGenNumber());
            if (!visited.Add(key))
            {
                return new PdfNull();
            }

            return reference.GetRefersTo(true) ?? new PdfNull();
        }

        return obj;
    }

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

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
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
}

