using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfRemediationProcessorFormsFixtureTests
{
    private static readonly PdfName StructParentKey = new("StructParent");

    [Fact]
    public async Task ProcessAsync_WhenFormsFixtureHasUntaggedWidgets_PreservesFieldsAndTagsWidgets()
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", "forms.pdf");
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var before = ReadFormWidgetStats(inputPdfPath);
        before.AcroFormFieldCount.Should().BeGreaterThan(0, "fixture should contain AcroForm fields");
        before.WidgetAnnotationCount.Should().BeGreaterThan(0, "fixture should contain form widget annotations");

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"remediate-forms-fixture-{Guid.NewGuid():N}");
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
                fileId: "forms-fixture",
                inputPdfPath: inputPdfPath,
                outputPdfPath: outputPdfPath,
                cancellationToken: CancellationToken.None);

            File.Exists(outputPdfPath).Should().BeTrue($"output should exist at {outputPdfPath}");

            var after = ReadFormWidgetStats(outputPdfPath);
            after.AcroFormFieldCount.Should().Be(before.AcroFormFieldCount, "form fields should be preserved");
            after.WidgetAnnotationCount.Should().Be(before.WidgetAnnotationCount, "form widget annotations should be preserved");
            after.UnassociatedWidgetAnnotationCount.Should()
                .Be(0, "all form widget annotations should be associated with the structure parent tree");
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed record FormWidgetStats(
        int AcroFormFieldCount,
        int WidgetAnnotationCount,
        int UnassociatedWidgetAnnotationCount);

    private static FormWidgetStats ReadFormWidgetStats(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));

        var acroFormFieldCount = CountAcroFormFields(pdf);
        var parentTree = TryGetParentTree(pdf);
        var widgetAnnotationCount = 0;
        var unassociatedWidgetAnnotationCount = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            foreach (var annotation in pdf.GetPage(pageNumber).GetAnnotations())
            {
                if (!PdfName.Widget.Equals(annotation.GetSubtype()))
                {
                    continue;
                }

                widgetAnnotationCount++;

                var structParent = annotation.GetPdfObject().GetAsNumber(StructParentKey)?.IntValue();
                if (structParent is null)
                {
                    unassociatedWidgetAnnotationCount++;
                    continue;
                }

                if (parentTree is null || !NumberTreeContainsKey(parentTree, structParent.Value))
                {
                    unassociatedWidgetAnnotationCount++;
                }
            }
        }

        return new FormWidgetStats(
            acroFormFieldCount,
            widgetAnnotationCount,
            unassociatedWidgetAnnotationCount);
    }

    private static int CountAcroFormFields(PdfDocument pdf)
    {
        var acroForm = pdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
        var fields = acroForm?.GetAsArray(PdfName.Fields);
        if (fields is null)
        {
            return 0;
        }

        return CountFieldDictionaries(fields, new HashSet<(int objNum, int genNum)>());
    }

    private static int CountFieldDictionaries(PdfArray fields, HashSet<(int objNum, int genNum)> visited)
    {
        var count = 0;
        for (var i = 0; i < fields.Size(); i++)
        {
            var field = DereferenceField(fields.Get(i), visited);
            if (field is null)
            {
                continue;
            }

            if (field.ContainsKey(PdfName.FT) || field.ContainsKey(PdfName.T))
            {
                count++;
            }

            var kids = field.GetAsArray(PdfName.Kids);
            if (kids is not null)
            {
                count += CountFieldDictionaries(kids, visited);
            }
        }

        return count;
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
            var kidObj = Dereference(kids.Get(i));

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

    private static PdfDictionary? DereferenceField(PdfObject obj, HashSet<(int objNum, int genNum)> visited)
    {
        if (obj is PdfIndirectReference reference)
        {
            var key = (reference.GetObjNumber(), reference.GetGenNumber());
            if (!visited.Add(key))
            {
                return null;
            }

            return reference.GetRefersTo(true) as PdfDictionary;
        }

        return obj as PdfDictionary;
    }

    private static PdfObject Dereference(PdfObject obj)
    {
        return obj is PdfIndirectReference reference
            ? reference.GetRefersTo(true) ?? new PdfNull()
            : obj;
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

    private sealed class FakePdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
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
