using System.Collections;
using System.Reflection;
using FluentAssertions;
using iText.Kernel.Pdf;
using server.core.Remediate;

namespace server.tests.Remediate;

public sealed class PdfTableRoleRemediatorTraversalTests
{
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
}
