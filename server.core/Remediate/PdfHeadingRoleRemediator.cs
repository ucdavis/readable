using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Geom;

namespace server.core.Remediate;

internal sealed record PdfHeadingDemotion(string OriginalRole, string Text, string StructurePath);

internal static class PdfHeadingRoleRemediator
{
    private const int MaxLikelyLabelWords = 10;

    private static readonly PdfName RoleP = new("P");
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTr = new("TR");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    private static readonly PdfName RoleFigure = new("Figure");
    private static readonly PdfName RoleL = new("L");
    private static readonly PdfName RoleLi = new("LI");
    private static readonly PdfName RoleLBody = new("LBody");
    private static readonly PdfName RoleLbl = new("Lbl");

    public static IReadOnlyList<PdfHeadingDemotion> DemoteSkippedTableLabelHeadings(
        PdfDocument pdf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return [];
        }

        var headings = ListHeadingsInStructureOrder(pdf, cancellationToken);
        if (headings.Count == 0)
        {
            return [];
        }

        var pageObjNumToPageNumber = BuildPageObjectNumberToPageNumberMap(pdf);
        var pageMcidTextCache = new Dictionary<int, Dictionary<int, string>>();
        var demotions = new List<PdfHeadingDemotion>();
        int? previousHeadingLevel = null;

        foreach (var heading in headings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (previousHeadingLevel is not null
                && heading.Level > previousHeadingLevel.Value + 1
                && ShouldDemoteSkippedHeading(heading, pdf, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken, out var text))
            {
                heading.Element.Put(PdfName.S, RoleP);
                demotions.Add(new PdfHeadingDemotion(heading.Role.GetValue(), text, heading.StructurePath));
                continue;
            }

            previousHeadingLevel = heading.Level;
        }

        return demotions;
    }

    private static bool ShouldDemoteSkippedHeading(
        HeadingCandidate heading,
        PdfDocument pdf,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken,
        out string text)
    {
        text = string.Empty;

        if (heading.Level <= 1 || !IsInsideTableCellPath(heading.AncestorRoles))
        {
            return false;
        }

        if (HasStructuralDescendant(heading.Element, cancellationToken))
        {
            return false;
        }

        text = TryExtractStructElementText(pdf, heading.Element, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken)
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return CountWords(text) <= MaxLikelyLabelWords;
    }

    private static List<HeadingCandidate> ListHeadingsInStructureOrder(
        PdfDocument pdf,
        CancellationToken cancellationToken)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        if (structTreeRootDict is null)
        {
            return [];
        }

        var rootKids = structTreeRootDict.Get(PdfName.K);
        if (rootKids is null)
        {
            return [];
        }

        var results = new List<HeadingCandidate>();
        TraverseForHeadings(rootKids, [], results, cancellationToken);
        return results;
    }

    private static void TraverseForHeadings(
        PdfObject node,
        IReadOnlyList<PdfName> path,
        List<HeadingCandidate> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        node = DereferenceNonNull(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                TraverseForHeadings(item, path, results, cancellationToken);
            }

            return;
        }

        if (node is not PdfDictionary dict || !IsStructElemDictionary(dict))
        {
            return;
        }

        var role = dict.GetAsName(PdfName.S);
        if (role is null)
        {
            return;
        }

        var currentPath = path.Concat([role]).ToList();
        if (TryGetHeadingLevel(role, out var headingLevel))
        {
            results.Add(new HeadingCandidate(dict, role, headingLevel, path.ToList(), FormatPath(currentPath)));
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            TraverseForHeadings(kids, currentPath, results, cancellationToken);
        }
    }

    private static bool TryGetHeadingLevel(PdfName role, out int level)
    {
        var value = role.GetValue();
        if (value.Length == 2 && value[0] == 'H' && value[1] is >= '1' and <= '6')
        {
            level = value[1] - '0';
            return true;
        }

        level = 0;
        return false;
    }

    private static bool IsInsideTableCellPath(IReadOnlyList<PdfName> ancestorRoles)
    {
        var tableIndex = IndexOfRole(ancestorRoles, RoleTable, startAt: 0);
        if (tableIndex < 0)
        {
            return false;
        }

        var rowIndex = IndexOfRole(ancestorRoles, RoleTr, startAt: tableIndex + 1);
        if (rowIndex < 0)
        {
            return false;
        }

        for (var i = rowIndex + 1; i < ancestorRoles.Count; i++)
        {
            if (RoleTd.Equals(ancestorRoles[i]) || RoleTh.Equals(ancestorRoles[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfRole(IReadOnlyList<PdfName> roles, PdfName role, int startAt)
    {
        for (var i = startAt; i < roles.Count; i++)
        {
            if (role.Equals(roles[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasStructuralDescendant(PdfDictionary heading, CancellationToken cancellationToken)
    {
        var kids = heading.Get(PdfName.K);
        if (kids is null)
        {
            return false;
        }

        return HasStructuralDescendantRecursive(kids, cancellationToken);
    }

    private static bool HasStructuralDescendantRecursive(PdfObject node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        node = DereferenceNonNull(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                if (HasStructuralDescendantRecursive(item, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (node is not PdfDictionary dict || !IsStructElemDictionary(dict))
        {
            return false;
        }

        var role = dict.GetAsName(PdfName.S);
        if (role is not null && IsDisallowedDescendantRole(role))
        {
            return true;
        }

        var kids = dict.Get(PdfName.K);
        return kids is not null && HasStructuralDescendantRecursive(kids, cancellationToken);
    }

    private static bool IsDisallowedDescendantRole(PdfName role) =>
        TryGetHeadingLevel(role, out _)
        || RoleTable.Equals(role)
        || RoleTr.Equals(role)
        || RoleTh.Equals(role)
        || RoleTd.Equals(role)
        || RoleL.Equals(role)
        || RoleLi.Equals(role)
        || RoleLBody.Equals(role)
        || RoleLbl.Equals(role)
        || RoleFigure.Equals(role);

    private static string? TryExtractStructElementText(
        PdfDocument pdf,
        PdfDictionary structElem,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken)
    {
        var refs = ListMarkedContentRefs(structElem, pageObjNumToPageNumber);
        if (refs.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var (pageNumber, mcid) in refs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mcid < 0)
            {
                continue;
            }

            var textByMcid = GetOrScanPageMcidText(pdf, pageNumber, pageMcidTextCache, cancellationToken);
            if (textByMcid.TryGetValue(mcid, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                AppendWithWordBoundary(sb, text);
            }
        }

        var combined = RemediationHelpers.NormalizeWhitespace(sb.ToString());
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static Dictionary<int, string> GetOrScanPageMcidText(
        PdfDocument pdf,
        int pageNumber,
        Dictionary<int, Dictionary<int, string>> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(pageNumber, out var existing))
        {
            return existing;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var listener = new McidTextListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(pageNumber));
        var textByMcid = listener.GetTextByMcid();

        cache[pageNumber] = textByMcid;
        return textByMcid;
    }

    private static List<(int pageNumber, int mcid)> ListMarkedContentRefs(
        PdfDictionary structElem,
        Dictionary<int, int> pageObjNumToPageNumber)
    {
        var defaultPageDict = structElem.GetAsDictionary(PdfName.Pg);
        var kids = structElem.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var results = new List<(int pageNumber, int mcid)>();
        CollectMarkedContentRefsRecursive(kids, defaultPageDict, pageObjNumToPageNumber, results);
        return results;
    }

    private static void CollectMarkedContentRefsRecursive(
        PdfObject node,
        PdfDictionary? inheritedPageDict,
        Dictionary<int, int> pageObjNumToPageNumber,
        List<(int pageNumber, int mcid)> results)
    {
        node = DereferenceNonNull(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                CollectMarkedContentRefsRecursive(item, inheritedPageDict, pageObjNumToPageNumber, results);
            }

            return;
        }

        if (node is PdfNumber num)
        {
            var pageNumber = TryResolvePageNumber(inheritedPageDict, pageObjNumToPageNumber);
            if (pageNumber is not null)
            {
                results.Add((pageNumber.Value, num.IntValue()));
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        if (IsStructElemDictionary(dict))
        {
            var nestedPageDict = dict.GetAsDictionary(PdfName.Pg) ?? inheritedPageDict;
            var kids = dict.Get(PdfName.K);
            if (kids is not null)
            {
                CollectMarkedContentRefsRecursive(kids, nestedPageDict, pageObjNumToPageNumber, results);
            }

            return;
        }

        var pageDict = dict.GetAsDictionary(PdfName.Pg) ?? inheritedPageDict;
        var mcidNum = dict.GetAsNumber(PdfName.MCID);
        if (mcidNum is not null)
        {
            var pageNumber = TryResolvePageNumber(pageDict, pageObjNumToPageNumber);
            if (pageNumber is not null)
            {
                results.Add((pageNumber.Value, mcidNum.IntValue()));
            }
        }
    }

    private static int? TryResolvePageNumber(PdfDictionary? pageDict, Dictionary<int, int> pageObjNumToPageNumber)
    {
        if (pageDict is null)
        {
            return null;
        }

        var pageRef = pageDict.GetIndirectReference();
        if (pageRef is null)
        {
            return null;
        }

        return pageObjNumToPageNumber.TryGetValue(pageRef.GetObjNumber(), out var pageNumber) ? pageNumber : null;
    }

    private static Dictionary<int, int> BuildPageObjectNumberToPageNumberMap(PdfDocument pdf)
    {
        var map = new Dictionary<int, int>();
        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            var pageRef = pdf.GetPage(pageNumber).GetPdfObject().GetIndirectReference();
            if (pageRef is not null)
            {
                map[pageRef.GetObjNumber()] = pageNumber;
            }
        }

        return map;
    }

    private static int CountWords(string text)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    private static void AppendWithWordBoundary(StringBuilder sb, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = text.Trim();

        if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && text.Length > 0 && !char.IsWhiteSpace(text[0]))
        {
            sb.Append(' ');
        }

        sb.Append(text);
    }

    private static string FormatPath(IEnumerable<PdfName> path) => string.Join("/", path.Select(r => r.GetValue()));

    private static bool IsStructElemDictionary(PdfDictionary dict) => dict.ContainsKey(PdfName.S);

    private static PdfObject DereferenceNonNull(PdfObject obj)
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true) ?? reference;
        }

        return obj;
    }

    private sealed record HeadingCandidate(
        PdfDictionary Element,
        PdfName Role,
        int Level,
        IReadOnlyList<PdfName> AncestorRoles,
        string StructurePath);

    private sealed class McidTextListener : IEventListener
    {
        private readonly Dictionary<int, McidTextState> _byMcid = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri)
            {
                return;
            }

            var mcid = tri.GetMcid();
            if (mcid < 0)
            {
                return;
            }

            var text = tri.GetActualText() ?? tri.GetText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!_byMcid.TryGetValue(mcid, out var state))
            {
                state = new McidTextState();
                _byMcid[mcid] = state;
            }

            state.Append(tri, text);
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public Dictionary<int, string> GetTextByMcid()
        {
            var result = new Dictionary<int, string>();
            foreach (var (mcid, state) in _byMcid)
            {
                var text = RemediationHelpers.NormalizeWhitespace(state.Text.ToString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result[mcid] = text;
                }
            }

            return result;
        }

        private sealed class McidTextState
        {
            public StringBuilder Text { get; } = new();

            private Vector? _lastBaselineEnd;

            public void Append(TextRenderInfo tri, string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                var baseline = tri.GetBaseline();
                var start = baseline.GetStartPoint();
                var end = baseline.GetEndPoint();

                TryAppendSpaceIfGapIndicatesWordBoundary(tri, start, text);
                Text.Append(text);
                _lastBaselineEnd = end;
            }

            private void TryAppendSpaceIfGapIndicatesWordBoundary(TextRenderInfo tri, Vector start, string text)
            {
                if (Text.Length == 0 || _lastBaselineEnd is null)
                {
                    return;
                }

                var lastChar = Text[^1];
                var firstChar = text[0];
                if (char.IsWhiteSpace(lastChar) || char.IsWhiteSpace(firstChar))
                {
                    return;
                }

                var spaceWidth = tri.GetSingleSpaceWidth();
                if (spaceWidth <= 0)
                {
                    spaceWidth = 3f;
                }

                var dx = start.Get(0) - _lastBaselineEnd.Get(0);
                var dy = start.Get(1) - _lastBaselineEnd.Get(1);
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));

                if (distance > (spaceWidth * 0.5f))
                {
                    Text.Append(' ');
                }
            }
        }
    }
}
