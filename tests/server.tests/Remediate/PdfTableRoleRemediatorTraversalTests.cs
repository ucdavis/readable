using System.Collections;
using System.Reflection;
using FluentAssertions;
using iText.Kernel.Pdf;
using server.core.Remediate;

namespace server.tests.Remediate;

public sealed class PdfTableRoleRemediatorTraversalTests
{
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTd = new("TD");

    [Fact]
    public void ListMarkedContentRefs_WhenStructElementKidsCycle_StopsTraversal()
    {
        var structElem = new PdfDictionary();
        structElem.Put(PdfName.S, new PdfName("TD"));
        structElem.Put(PdfName.K, structElem);

        var method = typeof(PdfRemediationProcessor)
            .Assembly
            .GetType("server.core.Remediate.PdfTableRoleRemediator")!
            .GetMethod("ListMarkedContentRefs", BindingFlags.NonPublic | BindingFlags.Static)!;

        var refs = ((IEnumerable)method.Invoke(
            null,
            [structElem, new Dictionary<int, int>(), CancellationToken.None])!).Cast<object>();

        refs.Should().BeEmpty();
    }

    [Fact]
    public void FindStructElementParent_WhenStructElementKidsCycle_StopsTraversal()
    {
        using var stream = new MemoryStream();
        using var pdf = new PdfDocument(new PdfWriter(stream));
        pdf.AddNewPage();
        var root = CreateStructElement(pdf, RoleTable);
        var child = CreateStructElement(pdf, RoleTd);
        var missingTarget = CreateStructElement(pdf, RoleTd);
        root.Put(PdfName.K, child.GetIndirectReference());
        child.Put(PdfName.K, root.GetIndirectReference());

        var method = GetRemediatorMethod("FindStructElementParent");

        var parent = method.Invoke(null, [root, missingTarget, CancellationToken.None]);

        parent.Should().BeNull();
    }

    [Fact]
    public void ListDescendantStructElementsByRole_WhenStructElementKidsCycle_StopsTraversal()
    {
        using var stream = new MemoryStream();
        using var pdf = new PdfDocument(new PdfWriter(stream));
        pdf.AddNewPage();
        var root = CreateStructElement(pdf, RoleTable);
        var child = CreateStructElement(pdf, RoleTd);
        root.Put(PdfName.K, child.GetIndirectReference());
        child.Put(PdfName.K, root.GetIndirectReference());

        var method = GetRemediatorMethod("ListDescendantStructElementsByRole");

        var descendants = ((IEnumerable)method.Invoke(
            null,
            [root, RoleTd, CancellationToken.None])!).Cast<PdfDictionary>();

        descendants.Should().ContainSingle().Which.Should().BeSameAs(child);
    }

    [Fact]
    public void TraverseTagTreeForList_WhenStructElementKidsCycle_StopsTraversal()
    {
        using var stream = new MemoryStream();
        using var pdf = new PdfDocument(new PdfWriter(stream));
        pdf.AddNewPage();
        var root = CreateStructElement(pdf, RoleTable);
        var child = CreateStructElement(pdf, RoleTd);
        root.Put(PdfName.K, child.GetIndirectReference());
        child.Put(PdfName.K, root.GetIndirectReference());

        var results = new List<PdfDictionary>();
        var method = GetRemediatorMethod("TraverseTagTreeForList");

        method.Invoke(null, [root.Get(PdfName.K), RoleTd, results, CancellationToken.None]);

        results.Should().ContainSingle().Which.Should().BeSameAs(child);
    }

    private static MethodInfo GetRemediatorMethod(string name) =>
        typeof(PdfRemediationProcessor)
            .Assembly
            .GetType("server.core.Remediate.PdfTableRoleRemediator")!
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static PdfDictionary CreateStructElement(PdfDocument pdf, PdfName role)
    {
        var dict = new PdfDictionary();
        dict.Put(PdfName.S, role);
        dict.MakeIndirect(pdf);
        return dict;
    }
}
