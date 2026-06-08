using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Tagutils;

namespace server.core.Remediate;

internal static class PdfAnnotationRemediator
{
    private static readonly PdfName ObjrType = new("OBJR");
    private static readonly PdfName StructParentKey = new("StructParent");
    private const string RoleForm = "Form";

    public static int EnsureWidgetAnnotationsAreTagged(PdfDocument pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var structTreeRoot = TryGetStructTreeRoot(pdf);
        if (structTreeRoot is null)
        {
            return 0;
        }

        var parentTree = structTreeRoot.GetAsDictionary(PdfName.ParentTree);
        var parentTreeEntries = parentTree is null
            ? new Dictionary<int, PdfObject>()
            : ReadNumberTreeEntries(parentTree);
        var tagPointer = new TagTreePointer(pdf);

        var tagged = 0;
        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pdf.GetPage(pageNumber);
            foreach (var annotation in page.GetAnnotations())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!PdfName.Widget.Equals(annotation.GetSubtype()))
                {
                    continue;
                }

                var annotationDict = annotation.GetPdfObject();
                var existingStructParent = annotationDict.GetAsNumber(StructParentKey)?.IntValue();
                if (existingStructParent is not null
                    && parentTreeEntries.TryGetValue(existingStructParent.Value, out var existingParentTreeValue)
                    && ParentTreeValueReferencesAnnotation(existingParentTreeValue, annotationDict))
                {
                    continue;
                }

                annotationDict.Remove(StructParentKey);
                tagPointer
                    .MoveToRoot()
                    .SetPageForTagging(page)
                    .AddTag(RoleForm)
                    .AddAnnotationTag(annotation);
                tagged++;
            }
        }

        return tagged;
    }

    public static int RemoveUntaggedAnnotations(PdfDocument pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetStructTreeRoot(pdf) is null)
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
                if (PdfName.Widget.Equals(annotation.GetSubtype()))
                {
                    continue;
                }

                var structParent = dict.GetAsNumber(StructParentKey)?.IntValue();
                if (structParent is null)
                {
                    toRemove.Add(annotation);
                    continue;
                }

                if (parentTree is null || !NumberTreeContainsKey(parentTree, structParent.Value))
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

    private static Dictionary<int, PdfObject> ReadNumberTreeEntries(PdfDictionary numberTree)
    {
        var entries = new Dictionary<int, PdfObject>();
        ReadNumberTreeEntriesRecursive(numberTree, entries, new HashSet<(int objNum, int genNum)>());
        return entries;
    }

    private static void ReadNumberTreeEntriesRecursive(
        PdfDictionary node,
        Dictionary<int, PdfObject> entries,
        HashSet<(int objNum, int genNum)> visited)
    {
        var nodeRef = node.GetIndirectReference();
        if (nodeRef is not null)
        {
            var refKey = (nodeRef.GetObjNumber(), nodeRef.GetGenNumber());
            if (!visited.Add(refKey))
            {
                return;
            }
        }

        var nums = node.GetAsArray(PdfName.Nums);
        if (nums is not null)
        {
            for (var i = 0; i + 1 < nums.Size(); i += 2)
            {
                var key = nums.GetAsNumber(i)?.IntValue();
                if (key is not null)
                {
                    entries[key.Value] = nums.Get(i + 1);
                }
            }
        }

        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null)
        {
            return;
        }

        for (var i = 0; i < kids.Size(); i++)
        {
            var kidObj = Dereference(kids.Get(i), visited);
            if (kidObj is PdfDictionary kidDict)
            {
                ReadNumberTreeEntriesRecursive(kidDict, entries, visited);
            }
        }
    }

    private static bool ParentTreeValueReferencesAnnotation(PdfObject value, PdfDictionary annotationDict)
    {
        var annotationRef = annotationDict.GetIndirectReference();
        if (annotationRef is null)
        {
            return false;
        }

        return ObjectGraphReferencesAnnotation(
            value,
            annotationRef.GetObjNumber(),
            annotationRef.GetGenNumber(),
            new HashSet<(int objNum, int genNum)>());
    }

    private static bool ObjectGraphReferencesAnnotation(
        PdfObject obj,
        int annotationObjNum,
        int annotationGenNum,
        HashSet<(int objNum, int genNum)> visited)
    {
        if (obj is PdfIndirectReference reference)
        {
            var key = (reference.GetObjNumber(), reference.GetGenNumber());
            if (!visited.Add(key))
            {
                return false;
            }

            obj = reference.GetRefersTo(true) ?? new PdfNull();
        }

        if (obj is PdfDictionary dict)
        {
            if (ObjrType.Equals(dict.GetAsName(PdfName.Type)))
            {
                var referencedObj = dict.Get(PdfName.Obj);
                var referencedObjRef = referencedObj as PdfIndirectReference ?? referencedObj?.GetIndirectReference();
                if (referencedObjRef is not null
                    && referencedObjRef.GetObjNumber() == annotationObjNum
                    && referencedObjRef.GetGenNumber() == annotationGenNum)
                {
                    return true;
                }
            }

            foreach (var key in dict.KeySet())
            {
                if (ObjectGraphReferencesAnnotation(dict.Get(key), annotationObjNum, annotationGenNum, visited))
                {
                    return true;
                }
            }

            return false;
        }

        if (obj is PdfArray array)
        {
            for (var i = 0; i < array.Size(); i++)
            {
                if (ObjectGraphReferencesAnnotation(array.Get(i), annotationObjNum, annotationGenNum, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static PdfDictionary? TryGetParentTree(PdfDocument pdf)
    {
        return TryGetStructTreeRoot(pdf)?.GetAsDictionary(PdfName.ParentTree);
    }

    private static PdfDictionary? TryGetStructTreeRoot(PdfDocument pdf)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        return catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
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
