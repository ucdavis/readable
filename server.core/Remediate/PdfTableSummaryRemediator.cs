using System.Text;
using iText.IO.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace server.core.Remediate;

internal static class PdfTableSummaryRemediator
{
    private const string PlaceholderSummary = "Data table.";
    private const int MaxHeaderLabels = 6;
    private const int MaxHeaderLabelChars = 80;
    private const int MaxSummaryChars = 300;

    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTr = new("TR");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    private static readonly PdfName AttrOwnerKey = new("O");
    private static readonly PdfName AttrOwnerTable = new("Table");
    private static readonly PdfName AttrSummaryKey = new("Summary");

    public static int EnsureTablesHaveSummary(PdfDocument pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return 0;
        }

        var tables = ListStructElementsByRole(pdf, RoleTable);
        if (tables.Count == 0)
        {
            return 0;
        }

        var pageObjNumToPageNumber = BuildPageObjectNumberToPageNumberMap(pdf);
        var pageMcidTextCache = new Dictionary<int, Dictionary<int, string>>();

        var updated = 0;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryGetTableSummary(table) is not null)
            {
                continue;
            }

            var summary =
                TryGetAnySummary(table)
                ?? GenerateTableSummary(pdf, table, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken)
                ?? PlaceholderSummary;

            summary = RemediationHelpers.NormalizeWhitespace(summary);
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = PlaceholderSummary;
            }

            if (summary.Length > MaxSummaryChars)
            {
                summary = summary[..MaxSummaryChars].Trim();
            }

            if (TrySetTableSummary(table, summary))
            {
                updated++;
            }
        }

        return updated;
    }

    private static string? GenerateTableSummary(
        PdfDocument pdf,
        PdfDictionary table,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken)
    {
        var rows = ListDescendantStructElementsByRole(table, RoleTr);
        if (rows.Count == 0)
        {
            return null;
        }

        var rowCount = rows.Count;
        var colCount = 0;

        var headerCells = new List<PdfDictionary>();
        List<PdfDictionary>? firstRowCells = null;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = ListDirectStructElementChildren(row);
            if (firstRowCells is null)
            {
                firstRowCells = cells
                    .Where(c =>
                    {
                        var role = c.GetAsName(PdfName.S);
                        return RoleTh.Equals(role) || RoleTd.Equals(role);
                    })
                    .ToList();
            }
            var cellCount = 0;

            foreach (var cell in cells)
            {
                var role = cell.GetAsName(PdfName.S);
                if (RoleTh.Equals(role) || RoleTd.Equals(role))
                {
                    cellCount++;
                }
            }

            colCount = Math.Max(colCount, cellCount);

            if (headerCells.Count == 0)
            {
                var headersInRow = cells
                    .Where(c => RoleTh.Equals(c.GetAsName(PdfName.S)))
                    .ToList();
                if (headersInRow.Count > 0)
                {
                    headerCells = headersInRow;
                }
            }
        }

        if (rowCount <= 0 || colCount <= 0)
        {
            return null;
        }

        if (headerCells.Count == 0)
        {
            headerCells = ListDescendantStructElementsByRole(table, RoleTh);
            if (headerCells.Count == 0 && firstRowCells is not null)
            {
                headerCells = firstRowCells;
            }
        }

        var headers = ExtractHeaderLabels(pdf, headerCells, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken);
        if (headers.Count == 0 && firstRowCells is not null && !ReferenceEquals(headerCells, firstRowCells))
        {
            headers = ExtractHeaderLabels(pdf, firstRowCells, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken);
        }

        var sb = new StringBuilder();
        sb.Append("Table with ");
        sb.Append(rowCount);
        sb.Append(rowCount == 1 ? " row" : " rows");
        sb.Append(" and ");
        sb.Append(colCount);
        sb.Append(colCount == 1 ? " column." : " columns.");

        if (headers.Count > 0)
        {
            sb.Append(" Column headers: ");
            sb.Append(string.Join(", ", headers));
            sb.Append('.');
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ExtractHeaderLabels(
        PdfDocument pdf,
        List<PdfDictionary> headerCells,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerCells)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Count >= MaxHeaderLabels)
            {
                break;
            }

            var label = TryExtractCellText(pdf, cell, pageObjNumToPageNumber, pageMcidTextCache, cancellationToken);
            label = RemediationHelpers.NormalizeWhitespace(label ?? string.Empty);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            if (label.Length > MaxHeaderLabelChars)
            {
                label = label[..MaxHeaderLabelChars].Trim();
            }

            if (seen.Add(label))
            {
                results.Add(label);
            }
        }

        return results;
    }

    private static string? TryExtractCellText(
        PdfDocument pdf,
        PdfDictionary cell,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken)
    {
        var alt = cell.GetAsString(PdfName.Alt)?.ToUnicodeString();
        alt = RemediationHelpers.NormalizeWhitespace(alt ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(alt))
        {
            return alt;
        }

        var refs = ListMarkedContentRefs(cell, pageObjNumToPageNumber);
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

        var page = pdf.GetPage(pageNumber);
        var listener = new McidTextListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        var textByMcid = listener.GetTextByMcid();

        cache[pageNumber] = textByMcid;
        return textByMcid;
    }

    private sealed class McidTextListener : IEventListener
    {
        private readonly Dictionary<int, StringBuilder> _byMcid = new();

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

            if (!_byMcid.TryGetValue(mcid, out var sb))
            {
                sb = new StringBuilder();
                _byMcid[mcid] = sb;
            }

            AppendWithWordBoundary(sb, text);
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public Dictionary<int, string> GetTextByMcid()
        {
            var result = new Dictionary<int, string>();
            foreach (var (mcid, sb) in _byMcid)
            {
                var text = RemediationHelpers.NormalizeWhitespace(sb.ToString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result[mcid] = text;
                }
            }

            return result;
        }
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

    private static string? TryGetTableSummary(PdfDictionary table)
    {
        var attrs = Dereference(table.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            return TryGetTableSummaryFromAttributeDict(dict);
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict)
                {
                    var summary = TryGetTableSummaryFromAttributeDict(itemDict);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        return summary;
                    }
                }
            }
        }

        return null;
    }

    private static string? TryGetAnySummary(PdfDictionary table)
    {
        var attrs = Dereference(table.Get(PdfName.A));
        if (attrs is PdfDictionary dict)
        {
            return TryGetAnySummaryFromAttributeDict(dict);
        }

        if (attrs is PdfArray array)
        {
            foreach (var item in array)
            {
                if (Dereference(item) is PdfDictionary itemDict)
                {
                    var summary = TryGetAnySummaryFromAttributeDict(itemDict);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        return summary;
                    }
                }
            }
        }

        return null;
    }

    private static string? TryGetTableSummaryFromAttributeDict(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        if (owner is not null && AttrOwnerTable.Equals(owner))
        {
            var summary = dict.GetAsString(AttrSummaryKey)?.ToUnicodeString();
            summary = RemediationHelpers.NormalizeWhitespace(summary ?? string.Empty);
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }

        return null;
    }

    private static string? TryGetAnySummaryFromAttributeDict(PdfDictionary dict)
    {
        var tableOwned = TryGetTableSummaryFromAttributeDict(dict);
        if (!string.IsNullOrWhiteSpace(tableOwned))
        {
            return tableOwned;
        }

        var owner = dict.GetAsName(AttrOwnerKey);
        if (owner is not null && !AttrOwnerTable.Equals(owner))
        {
            return null;
        }

        var summary = dict.GetAsString(AttrSummaryKey)?.ToUnicodeString();
        summary = RemediationHelpers.NormalizeWhitespace(summary ?? string.Empty);
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static bool TrySetTableSummary(PdfDictionary table, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        var summaryString = new PdfString(summary, PdfEncodings.UNICODE_BIG);

        var attrsObj = table.Get(PdfName.A);
        var attrs = Dereference(attrsObj);

        if (attrs is null)
        {
            table.Put(PdfName.A, CreateTableAttributeDict(summaryString));
            return true;
        }

        if (attrs is PdfDictionary dict)
        {
            var owner = dict.GetAsName(AttrOwnerKey);
            if (owner is not null && AttrOwnerTable.Equals(owner))
            {
                dict.Put(AttrSummaryKey, summaryString);
                return true;
            }

            var array = new PdfArray();
            array.Add(dict);
            array.Add(CreateTableAttributeDict(summaryString));
            table.Put(PdfName.A, array);
            return true;
        }

        if (attrs is PdfArray attrArray)
        {
            foreach (var item in attrArray)
            {
                if (Dereference(item) is not PdfDictionary itemDict)
                {
                    continue;
                }

                var owner = itemDict.GetAsName(AttrOwnerKey);
                if (owner is not null && AttrOwnerTable.Equals(owner))
                {
                    itemDict.Put(AttrSummaryKey, summaryString);
                    return true;
                }
            }

            attrArray.Add(CreateTableAttributeDict(summaryString));
            return true;
        }

        var fallback = new PdfArray();
        fallback.Add(attrsObj);
        fallback.Add(CreateTableAttributeDict(summaryString));
        table.Put(PdfName.A, fallback);
        return true;
    }

    private static PdfDictionary CreateTableAttributeDict(PdfString summary)
    {
        var dict = new PdfDictionary();
        dict.Put(AttrOwnerKey, AttrOwnerTable);
        dict.Put(AttrSummaryKey, summary);
        return dict;
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
            if (pageRef is null)
            {
                continue;
            }

            map[pageRef.GetObjNumber()] = pageNumber;
        }

        return map;
    }

    private static List<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
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

        var results = new List<PdfDictionary>();
        TraverseTagTreeForList(rootKids, role, results);
        return results;
    }

    private static List<PdfDictionary> ListDescendantStructElementsByRole(PdfDictionary parent, PdfName role)
    {
        var kids = parent.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var results = new List<PdfDictionary>();
        TraverseTagTreeForList(kids, role, results);
        return results;
    }

    private static List<PdfDictionary> ListDirectStructElementChildren(PdfDictionary parent)
    {
        var kids = parent.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var results = new List<PdfDictionary>();
        CollectDirectStructElementChildren(kids, results);
        return results;
    }

    private static void CollectDirectStructElementChildren(PdfObject node, List<PdfDictionary> results)
    {
        node = DereferenceNonNull(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                CollectDirectStructElementChildren(item, results);
            }

            return;
        }

        if (node is PdfDictionary dict && IsStructElemDictionary(dict))
        {
            results.Add(dict);
        }
    }

    private static void TraverseTagTreeForList(PdfObject node, PdfName role, List<PdfDictionary> results)
    {
        node = DereferenceNonNull(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                TraverseTagTreeForList(item, role, results);
            }

            return;
        }

        if (node is not PdfDictionary dict || !IsStructElemDictionary(dict))
        {
            return;
        }

        var nodeRole = dict.GetAsName(PdfName.S);
        if (role.Equals(nodeRole))
        {
            results.Add(dict);
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            TraverseTagTreeForList(kids, role, results);
        }
    }

    private static bool IsStructElemDictionary(PdfDictionary dict) => dict.ContainsKey(PdfName.S);

    private static PdfObject? Dereference(PdfObject? obj)
    {
        if (obj is null)
        {
            return null;
        }

        return DereferenceNonNull(obj);
    }

    private static PdfObject DereferenceNonNull(PdfObject obj)
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true) ?? reference;
        }

        return obj;
    }
}
