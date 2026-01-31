using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

namespace server.core.Remediate;

internal static class PdfAnnotationRemediator
{
    private static readonly PdfName StructParentKey = new("StructParent");

    public static int RemoveUntaggedAnnotations(PdfDocument pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return 0;
        }

        var parentTree = TryGetParentTree(pdf);

        var removed = 0;
        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pdf.GetPage(pageNumber);
            var annotations = page.GetAnnotations();
            if (annotations.Count == 0)
            {
                continue;
            }

            var toRemove = new List<PdfAnnotation>(capacity: annotations.Count);
            foreach (var annotation in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dict = annotation.GetPdfObject();

                var structParent = dict.GetAsNumber(StructParentKey)?.IntValue();
                if (structParent is null)
                {
                    toRemove.Add(annotation);
                    continue;
                }

                if (parentTree is not null && !NumberTreeContainsKey(parentTree, structParent.Value))
                {
                    toRemove.Add(annotation);
                }
            }

            foreach (var annotation in toRemove)
            {
                page.RemoveAnnotation(annotation);
                removed++;
            }
        }

        return removed;
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
}

