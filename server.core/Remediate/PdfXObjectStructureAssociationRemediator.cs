using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;

namespace server.core.Remediate;

internal sealed record PdfXObjectStructureAssociationRemediationResult(
    int Inspected,
    int Protected,
    int Disconnected,
    int InlineAltApplied = 0);

internal static class PdfXObjectStructureAssociationRemediator
{
    // This pass handles XObject structure associations that Acrobat evaluates separately from normal tag-tree
    // Figure alt text. It preserves meaningful structure claims, disconnects unclaimed XObject residue, and
    // mirrors tag-tree Figure /Alt into Form XObject inline /Figure marked-content properties when Acrobat
    // would otherwise report "Other elements alternate text" for the inline marked content.
    private static readonly PdfName RoleAnnot = new("Annot");
    private static readonly PdfName RoleFormula = new("Formula");
    private static readonly PdfName StructParentsKey = new("StructParents");
    private static readonly Regex FigureMarkedContentDictionaryRegex = new(
        @"/Figure\s*<<(?<dict>(?:(?:<[^>]*>)|(?:\([^()]*\))|[^<>])*?/MCID\s+(?<mcid>\d+)(?:(?:<[^>]*>)|(?:\([^()]*\))|[^<>])*?)>>\s*BDC",
        RegexOptions.Compiled);

    public static PdfXObjectStructureAssociationRemediationResult Remediate(
        PdfDocument pdf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return new PdfXObjectStructureAssociationRemediationResult(0, 0, 0);
        }

        var parentTree = TryGetParentTree(pdf);
        var visitedXObjects = new HashSet<(int objNum, int genNum)>();

        var inspected = 0;
        var protectedCount = 0;
        var disconnected = 0;
        var inlineAltApplied = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resources = GetPageResources(pdf.GetPage(pageNumber));
            RemediateResources(
                resources,
                parentTree,
                visitedXObjects,
                ref inspected,
                ref protectedCount,
                ref disconnected,
                ref inlineAltApplied,
                cancellationToken);
        }

        return new PdfXObjectStructureAssociationRemediationResult(
            inspected,
            protectedCount,
            disconnected,
            inlineAltApplied);
    }

    private static PdfDictionary? GetPageResources(PdfPage page)
    {
        var pageDict = page.GetPdfObject();
        var resources = pageDict.GetAsDictionary(PdfName.Resources);
        if (resources is not null)
        {
            return resources;
        }

        var parent = pageDict.GetAsDictionary(PdfName.Parent);
        while (parent is not null)
        {
            resources = parent.GetAsDictionary(PdfName.Resources);
            if (resources is not null)
            {
                return resources;
            }

            parent = parent.GetAsDictionary(PdfName.Parent);
        }

        return null;
    }

    private static void RemediateResources(
        PdfDictionary? resources,
        PdfDictionary? parentTree,
        HashSet<(int objNum, int genNum)> visitedXObjects,
        ref int inspected,
        ref int protectedCount,
        ref int disconnected,
        ref int inlineAltApplied,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var xObjects = resources?.GetAsDictionary(PdfName.XObject);
        if (xObjects is null)
        {
            return;
        }

        foreach (var name in xObjects.KeySet())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetUnvisitedXObjectStream(xObjects.Get(name), visitedXObjects, out var xObject))
            {
                continue;
            }

            var subtype = xObject.GetAsName(PdfName.Subtype);
            if (!PdfName.Form.Equals(subtype) && !PdfName.Image.Equals(subtype))
            {
                continue;
            }

            inspected++;

            var structParents = xObject.GetAsNumber(StructParentsKey)?.IntValue();
            if (structParents is not null)
            {
                // /StructParents points from an XObject back into the structure parent tree. If that entry is
                // meaningfully claimed, keep the association and normalize the claim; otherwise remove only the
                // low-level hook so decorative/autotag residue stops participating in accessibility checks.
                var claim = AnalyzeParentTreeClaim(parentTree, structParents.Value);
                NormalizeClaimMarkedContentReferences(claim.StructElements, xObject);
                inlineAltApplied += ApplyClaimInlineFigureAltText(claim.StructElements, xObject);
                if (claim.IsProtected)
                {
                    protectedCount++;
                }
                else
                {
                    xObject.Remove(StructParentsKey);
                    disconnected++;
                }
            }

            if (PdfName.Form.Equals(subtype))
            {
                RemediateResources(
                    xObject.GetAsDictionary(PdfName.Resources),
                    parentTree,
                    visitedXObjects,
                    ref inspected,
                    ref protectedCount,
                    ref disconnected,
                    ref inlineAltApplied,
                    cancellationToken);
            }
        }
    }

    private static bool TryGetUnvisitedXObjectStream(
        PdfObject? xObjectRefOrStream,
        HashSet<(int objNum, int genNum)> visitedXObjects,
        out PdfStream xObject)
    {
        xObject = null!;
        if (xObjectRefOrStream is null || xObjectRefOrStream is PdfNull)
        {
            return false;
        }

        var indirectRef = xObjectRefOrStream as PdfIndirectReference ?? xObjectRefOrStream.GetIndirectReference();
        if (indirectRef is not null)
        {
            var key = (indirectRef.GetObjNumber(), indirectRef.GetGenNumber());
            if (!visitedXObjects.Add(key))
            {
                return false;
            }
        }

        var dereferenced = Dereference(xObjectRefOrStream);
        if (dereferenced is not PdfStream stream)
        {
            return false;
        }

        xObject = stream;
        return true;
    }

    private static ParentTreeClaimAnalysis AnalyzeParentTreeClaim(PdfDictionary? parentTree, int structParents)
    {
        if (parentTree is null)
        {
            return ParentTreeClaimAnalysis.Unclaimed;
        }

        var claim = TryGetNumberTreeValue(parentTree, structParents);
        if (claim is null || claim is PdfNull)
        {
            return ParentTreeClaimAnalysis.Unclaimed;
        }

        var structElems = new List<PdfDictionary>();
        CollectParentTreeClaimStructElements(claim, structElems, new HashSet<(int objNum, int genNum)>());
        if (structElems.Count == 0)
        {
            return ParentTreeClaimAnalysis.Unclaimed;
        }

        return structElems.Any(IsMeaningfulStructureElement)
            ? ParentTreeClaimAnalysis.ProtectedClaim(structElems)
            : ParentTreeClaimAnalysis.Unclaimed;
    }

    // Some Form-backed MCR dictionaries omit /Pg. Acrobat can still display the tag-tree Figure, but its
    // content-properties lookup may show a blank Structure Tag. Adding /Pg gives the MCR explicit page context.
    private static void NormalizeClaimMarkedContentReferences(
        IReadOnlyList<PdfDictionary> structElems,
        PdfStream xObject)
    {
        if (!PdfName.Form.Equals(xObject.GetAsName(PdfName.Subtype)))
        {
            return;
        }

        var xObjectRef = xObject.GetIndirectReference();
        if (xObjectRef is null)
        {
            return;
        }

        foreach (var structElem in structElems)
        {
            var page = structElem.GetAsDictionary(PdfName.Pg);
            if (page is null)
            {
                continue;
            }

            var kids = structElem.Get(PdfName.K);
            if (kids is not null)
            {
                NormalizeMarkedContentReferenceKids(kids, page, xObjectRef, new HashSet<(int objNum, int genNum)>());
            }
        }
    }

    private static void NormalizeMarkedContentReferenceKids(
        PdfObject node,
        PdfDictionary page,
        PdfIndirectReference xObjectRef,
        HashSet<(int objNum, int genNum)> visited)
    {
        node = Dereference(node, visited);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                NormalizeMarkedContentReferenceKids(item, page, xObjectRef, visited);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        if (dict.GetAsNumber(PdfName.MCID) is not null
            && TryReferencesObject(dict.Get(PdfName.Stm), xObjectRef))
        {
            if (dict.GetAsDictionary(PdfName.Pg) is null)
            {
                dict.Put(PdfName.Pg, page);
            }

            return;
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            NormalizeMarkedContentReferenceKids(kids, page, xObjectRef, visited);
        }
    }

    private static bool TryReferencesObject(PdfObject? obj, PdfIndirectReference targetRef)
    {
        var objRef = obj as PdfIndirectReference ?? obj?.GetIndirectReference();
        return objRef is not null
            && objRef.GetObjNumber() == targetRef.GetObjNumber()
            && objRef.GetGenNumber() == targetRef.GetGenNumber();
    }

    private static int ApplyClaimInlineFigureAltText(
        IReadOnlyList<PdfDictionary> structElems,
        PdfStream xObject)
    {
        if (!PdfName.Form.Equals(xObject.GetAsName(PdfName.Subtype)))
        {
            return 0;
        }

        var altByMcid = BuildInlineFigureAltTextByMcid(structElems);
        if (altByMcid.Count == 0)
        {
            return 0;
        }

        // Acrobat's "Other elements alternate text" rule checks /Figure marked content inside Form XObjects.
        // Copy the already-remediated tag-tree Figure /Alt onto the matching inline /Figure property dictionary;
        // this does not create new semantics or call AI, it only makes the existing association complete.
        var content = Encoding.Latin1.GetString(xObject.GetBytes());
        var applied = 0;
        var updated = FigureMarkedContentDictionaryRegex.Replace(
            content,
            match =>
            {
                if (!int.TryParse(match.Groups["mcid"].Value, out var mcid)
                    || !altByMcid.TryGetValue(mcid, out var alt))
                {
                    return match.Value;
                }

                var dict = match.Groups["dict"].Value.TrimEnd();
                if (ContainsDictionaryKey(dict, "Alt"))
                {
                    return match.Value;
                }

                applied++;
                return $"/Figure <<{dict} /Alt <{ToPdfTextStringHex(alt)}> >> BDC";
            });

        if (applied == 0)
        {
            return 0;
        }

        xObject.SetData(Encoding.Latin1.GetBytes(updated));
        return applied;
    }

    private static Dictionary<int, string> BuildInlineFigureAltTextByMcid(IReadOnlyList<PdfDictionary> structElems)
    {
        var altByMcid = new Dictionary<int, string>();
        foreach (var structElem in structElems)
        {
            if (!PdfName.Figure.Equals(structElem.GetAsName(PdfName.S)))
            {
                continue;
            }

            var alt = structElem.GetAsString(PdfName.Alt)?.ToUnicodeString();
            if (string.IsNullOrWhiteSpace(alt))
            {
                continue;
            }

            var kids = structElem.Get(PdfName.K);
            if (kids is not null)
            {
                CollectInlineFigureAltTextKids(kids, alt.Trim(), altByMcid, new HashSet<(int objNum, int genNum)>());
            }
        }

        return altByMcid;
    }

    private static void CollectInlineFigureAltTextKids(
        PdfObject node,
        string alt,
        Dictionary<int, string> altByMcid,
        HashSet<(int objNum, int genNum)> visited)
    {
        node = Dereference(node, visited);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                CollectInlineFigureAltTextKids(item, alt, altByMcid, visited);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        var mcid = dict.GetAsNumber(PdfName.MCID)?.IntValue();
        if (mcid is not null)
        {
            altByMcid.TryAdd(mcid.Value, alt);
            return;
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            CollectInlineFigureAltTextKids(kids, alt, altByMcid, visited);
        }
    }

    private static bool ContainsDictionaryKey(string dictionaryContent, string key)
        => Regex.IsMatch(
            dictionaryContent,
            $@"/{Regex.Escape(key)}(?:\s|/|<|\(|\[|$)",
            RegexOptions.CultureInvariant);

    private static string ToPdfTextStringHex(string text)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(text);
        return "FEFF" + Convert.ToHexString(bytes);
    }

    private static void CollectParentTreeClaimStructElements(
        PdfObject value,
        List<PdfDictionary> structElems,
        HashSet<(int objNum, int genNum)> visited)
    {
        value = Dereference(value, visited);

        if (value is PdfArray array)
        {
            foreach (var item in array)
            {
                CollectParentTreeClaimStructElements(item, structElems, visited);
            }

            return;
        }

        if (value is PdfDictionary dict)
        {
            structElems.Add(dict);
        }
    }

    private static bool IsMeaningfulStructureElement(PdfDictionary dict)
    {
        var role = dict.GetAsName(PdfName.S);
        if (role is null)
        {
            return false;
        }

        if (PdfName.Figure.Equals(role)
            || PdfName.Link.Equals(role)
            || PdfName.Form.Equals(role)
            || RoleAnnot.Equals(role)
            || RoleFormula.Equals(role))
        {
            return true;
        }

        var alt = dict.GetAsString(PdfName.Alt)?.ToUnicodeString();
        return !string.IsNullOrWhiteSpace(alt);
    }

    private static PdfDictionary? TryGetParentTree(PdfDocument pdf)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        return structTreeRootDict?.GetAsDictionary(PdfName.ParentTree);
    }

    private static PdfObject? TryGetNumberTreeValue(PdfDictionary numberTree, int key)
    {
        return TryGetNumberTreeValueRecursive(numberTree, key, new HashSet<(int objNum, int genNum)>());
    }

    private static PdfObject? TryGetNumberTreeValueRecursive(
        PdfDictionary node,
        int key,
        HashSet<(int objNum, int genNum)> visited)
    {
        var nodeRef = node.GetIndirectReference();
        if (nodeRef is not null)
        {
            var nodeKey = (nodeRef.GetObjNumber(), nodeRef.GetGenNumber());
            if (!visited.Add(nodeKey))
            {
                return null;
            }
        }

        var nums = node.GetAsArray(PdfName.Nums);
        if (nums is not null)
        {
            for (var i = 0; i + 1 < nums.Size(); i += 2)
            {
                if (nums.GetAsNumber(i)?.IntValue() == key)
                {
                    return nums.Get(i + 1);
                }
            }

            return null;
        }

        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null)
        {
            return null;
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

            var value = TryGetNumberTreeValueRecursive(kidDict, key, visited);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static PdfObject Dereference(PdfObject obj)
    {
        return obj is PdfIndirectReference reference
            ? reference.GetRefersTo(true) ?? new PdfNull()
            : obj;
    }

    private static PdfObject Dereference(PdfObject obj, HashSet<(int objNum, int genNum)> visited)
    {
        if (obj is not PdfIndirectReference reference)
        {
            return obj;
        }

        var key = (reference.GetObjNumber(), reference.GetGenNumber());
        if (!visited.Add(key))
        {
            return new PdfNull();
        }

        return reference.GetRefersTo(true) ?? new PdfNull();
    }

    private sealed record ParentTreeClaimAnalysis(
        bool IsProtected,
        IReadOnlyList<PdfDictionary> StructElements)
    {
        public static ParentTreeClaimAnalysis Unclaimed { get; } = new(
            IsProtected: false,
            StructElements: Array.Empty<PdfDictionary>());

        public static ParentTreeClaimAnalysis ProtectedClaim(IReadOnlyList<PdfDictionary> structElements)
            => new(IsProtected: true, StructElements: structElements);
    }
}
