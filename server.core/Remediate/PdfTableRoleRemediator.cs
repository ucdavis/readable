using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using server.core.Remediate.Table;

namespace server.core.Remediate;

internal enum PdfNoHeaderTableRemediationAction
{
    PromotedFirstRow,
    DemotedLayoutTable,
    LeftUnchanged,
}

internal sealed record PdfNoHeaderTableRemediation(
    PdfNoHeaderTableRemediationAction Action,
    int RowCount,
    int MaxColumnCount,
    string FirstRowSnippet,
    string Reason);

internal static class PdfTableRoleRemediator
{
    private const int MaxCellTextChars = 120;
    private const double MinClassifierConfidence = 0.7;
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTHead = new("THead");
    private static readonly PdfName RoleTBody = new("TBody");
    private static readonly PdfName RoleTFoot = new("TFoot");
    private static readonly PdfName RoleTr = new("TR");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    private static readonly PdfName RoleDiv = new("Div");
    private static readonly PdfName AttrOwnerKey = new("O");
    private static readonly PdfName AttrOwnerTable = new("Table");
    private static readonly PdfName AttrScopeKey = new("Scope");
    private static readonly PdfName AttrScopeColumn = new("Column");

    public static int DemoteLikelyLayoutTables(
        PdfDocument pdf,
        bool demoteSmallTablesWithoutHeaders,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return 0;
        }

        var tables = ListStructElementsByRole(pdf, RoleTable, cancellationToken);
        if (tables.Count == 0)
        {
            return 0;
        }

        var demoted = 0;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldDemoteTable(table, demoteSmallTablesWithoutHeaders, cancellationToken))
            {
                continue;
            }

            DemoteTableAndDescendants(table, cancellationToken);
            demoted++;
        }

        return demoted;
    }

    public static async Task<IReadOnlyList<PdfNoHeaderTableRemediation>> RemediateNoHeaderTablesAsync(
        PdfDocument pdf,
        IPdfTableClassificationService tableClassificationService,
        string? primaryLanguage,
        bool promoteFirstRowHeadersForNoHeaderTables,
        bool demoteLikelyFormLayoutTables,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!pdf.IsTagged())
        {
            return [];
        }

        if (!promoteFirstRowHeadersForNoHeaderTables && !demoteLikelyFormLayoutTables)
        {
            return [];
        }

        var tables = ListStructElementsByRole(pdf, RoleTable, cancellationToken);
        if (tables.Count == 0)
        {
            return [];
        }

        var pageObjNumToPageNumber = BuildPageObjectNumberToPageNumberMap(pdf);
        var pageMcidTextCache = new Dictionary<int, Dictionary<int, string>>();
        var results = new List<PdfNoHeaderTableRemediation>();

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inventory = BuildTableInventory(
                pdf,
                table,
                pageObjNumToPageNumber,
                pageMcidTextCache,
                cancellationToken);

            if (inventory.HeaderCellCount > 0)
            {
                continue;
            }

            if (inventory.HasNestedTable)
            {
                results.Add(CreateUnchangedResult(inventory, "left unchanged; nested table structure"));
                continue;
            }

            if (inventory.RowCount < 2 || inventory.MaxColumnCount < 2)
            {
                results.Add(CreateUnchangedResult(inventory, "left unchanged; fewer than 2 rows or 2 columns"));
                continue;
            }

            var request = inventory.ToClassificationRequest(primaryLanguage);
            var classification = await tableClassificationService.ClassifyAsync(request, cancellationToken);
            var confidence = Math.Clamp(classification.Confidence, 0, 1);
            if (confidence < MinClassifierConfidence)
            {
                results.Add(
                    CreateUnchangedResult(
                        inventory,
                        $"left unchanged; table classifier confidence {confidence:0.00} was below {MinClassifierConfidence:0.00}"));
                continue;
            }

            if (classification.Kind == PdfTableKind.DataTable && promoteFirstRowHeadersForNoHeaderTables)
            {
                var promoted = PromoteFirstRowCellsToHeaders(inventory, cancellationToken);
                results.Add(
                    new PdfNoHeaderTableRemediation(
                        promoted > 0
                            ? PdfNoHeaderTableRemediationAction.PromotedFirstRow
                            : PdfNoHeaderTableRemediationAction.LeftUnchanged,
                        inventory.RowCount,
                        inventory.MaxColumnCount,
                        inventory.FirstRowSnippet,
                        promoted > 0
                            ? "classified as data table; promoted first row cells to TH"
                            : "classified as data table but no first-row TD cells were available to promote"));
                continue;
            }

            if (classification.Kind == PdfTableKind.LayoutOrFormTable && demoteLikelyFormLayoutTables)
            {
                DemoteTableAndDescendants(table, cancellationToken);
                results.Add(
                    new PdfNoHeaderTableRemediation(
                        PdfNoHeaderTableRemediationAction.DemotedLayoutTable,
                        inventory.RowCount,
                        inventory.MaxColumnCount,
                        inventory.FirstRowSnippet,
                        "classified as layout/form table; demoted table roles"));
                continue;
            }

            results.Add(
                CreateUnchangedResult(
                    inventory,
                    classification.Kind switch
                    {
                        PdfTableKind.DataTable => "classified as data table but first-row header promotion is disabled",
                        PdfTableKind.LayoutOrFormTable => "classified as layout/form table but form-layout demotion is disabled",
                        _ => "left unchanged; table classifier returned uncertain",
                    }));
        }

        return results;
    }

    private static PdfNoHeaderTableRemediation CreateUnchangedResult(TableInventory inventory, string reason) =>
        new(
            PdfNoHeaderTableRemediationAction.LeftUnchanged,
            inventory.RowCount,
            inventory.MaxColumnCount,
            inventory.FirstRowSnippet,
            reason);

    private static bool ShouldDemoteTable(
        PdfDictionary table,
        bool demoteSmallTablesWithoutHeaders,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If any header cells exist, treat it as a real data table.
        if (ListDescendantStructElementsByRole(table, RoleTh, cancellationToken).Count > 0)
        {
            return false;
        }

        var rows = ListDescendantStructElementsByRole(table, RoleTr, cancellationToken);
        if (rows.Count == 0)
        {
            // A /Table with no /TR is almost certainly a tagging mistake.
            return true;
        }

        var rowCount = rows.Count;
        var maxCols = 0;
        var cellCountTotal = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellCountInRow = 0;
            foreach (var child in ListDirectStructElementChildren(row))
            {
                var role = child.GetAsName(PdfName.S);
                if (RoleTd.Equals(role))
                {
                    cellCountTotal++;
                    cellCountInRow++;
                }
                else if (RoleTh.Equals(role))
                {
                    return false;
                }
                else if (RoleTable.Equals(role) || RoleTr.Equals(role))
                {
                    // ignore
                }
            }

            maxCols = Math.Max(maxCols, cellCountInRow);
        }

        if (cellCountTotal == 0)
        {
            return true;
        }

        if (!demoteSmallTablesWithoutHeaders)
        {
            return false;
        }

        if (rowCount <= 1 || maxCols <= 1)
        {
            return true;
        }

        return false;
    }

    private static TableInventory BuildTableInventory(
        PdfDocument pdf,
        PdfDictionary table,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<int, Dictionary<int, string>> pageMcidTextCache,
        CancellationToken cancellationToken)
    {
        var rowElements = new List<PdfDictionary>();
        var hasNestedTable = CollectTableRows(table, rowElements, cancellationToken);
        var rows = new List<TableRowInventory>();
        var headerCellCount = 0;
        var maxColumnCount = 0;

        foreach (var row in rowElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = new List<TableCellInventory>();
            foreach (var child in ListDirectStructElementChildren(row))
            {
                var role = child.GetAsName(PdfName.S);
                if (!RoleTh.Equals(role) && !RoleTd.Equals(role))
                {
                    continue;
                }

                if (RoleTh.Equals(role))
                {
                    headerCellCount++;
                }

                var text = TryExtractCellText(
                    pdf,
                    child,
                    pageObjNumToPageNumber,
                    pageMcidTextCache,
                    cancellationToken);

                text = RemediationHelpers.NormalizeWhitespace(text ?? string.Empty);
                if (text.Length > MaxCellTextChars)
                {
                    text = text[..MaxCellTextChars].Trim();
                }

                cells.Add(new TableCellInventory(child, role, text));
            }

            maxColumnCount = Math.Max(maxColumnCount, cells.Count);
            rows.Add(new TableRowInventory(row, cells));
        }

        var firstRowSnippet = BuildFirstRowSnippet(rows);
        return new TableInventory(table, rows, headerCellCount, maxColumnCount, hasNestedTable, firstRowSnippet);
    }

    private static int PromoteFirstRowCellsToHeaders(TableInventory inventory, CancellationToken cancellationToken)
    {
        if (inventory.Rows.Count == 0)
        {
            return 0;
        }

        var promoted = 0;
        foreach (var cell in inventory.Rows[0].Cells)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!RoleTd.Equals(cell.Role))
            {
                continue;
            }

            cell.Element.Put(PdfName.S, RoleTh);
            EnsureColumnScope(cell.Element);
            promoted++;
        }

        return promoted;
    }

    private static void EnsureColumnScope(PdfDictionary cell)
    {
        var attrsObj = cell.Get(PdfName.A);
        var attrs = Dereference(attrsObj);

        if (attrs is null)
        {
            cell.Put(PdfName.A, CreateColumnScopeAttributeDict());
            return;
        }

        if (attrs is PdfDictionary dict)
        {
            if (TrySetColumnScope(dict))
            {
                return;
            }

            var array = new PdfArray();
            array.Add(dict);
            array.Add(CreateColumnScopeAttributeDict());
            cell.Put(PdfName.A, array);
            return;
        }

        if (attrs is PdfArray attrArray)
        {
            foreach (var item in attrArray)
            {
                if (Dereference(item) is PdfDictionary itemDict && TrySetColumnScope(itemDict))
                {
                    return;
                }
            }

            attrArray.Add(CreateColumnScopeAttributeDict());
            return;
        }

        var fallback = new PdfArray();
        fallback.Add(attrsObj);
        fallback.Add(CreateColumnScopeAttributeDict());
        cell.Put(PdfName.A, fallback);
    }

    private static bool TrySetColumnScope(PdfDictionary dict)
    {
        var owner = dict.GetAsName(AttrOwnerKey);
        if (owner is not null && !AttrOwnerTable.Equals(owner))
        {
            return false;
        }

        dict.Put(AttrOwnerKey, AttrOwnerTable);
        dict.Put(AttrScopeKey, AttrScopeColumn);
        return true;
    }

    private static PdfDictionary CreateColumnScopeAttributeDict()
    {
        var dict = new PdfDictionary();
        dict.Put(AttrOwnerKey, AttrOwnerTable);
        dict.Put(AttrScopeKey, AttrScopeColumn);
        return dict;
    }

    private static bool CollectTableRows(
        PdfDictionary table,
        List<PdfDictionary> rows,
        CancellationToken cancellationToken)
    {
        var hasNestedTable = false;
        foreach (var child in ListDirectStructElementChildren(table))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var role = child.GetAsName(PdfName.S);
            if (RoleTr.Equals(role))
            {
                rows.Add(child);
                continue;
            }

            if (RoleTable.Equals(role))
            {
                hasNestedTable = true;
                continue;
            }

            if (RoleTHead.Equals(role) || RoleTBody.Equals(role) || RoleTFoot.Equals(role))
            {
                hasNestedTable |= CollectRowsFromTableSection(child, rows, cancellationToken);
            }
        }

        return hasNestedTable;
    }

    private static bool CollectRowsFromTableSection(
        PdfDictionary section,
        List<PdfDictionary> rows,
        CancellationToken cancellationToken)
    {
        var hasNestedTable = false;
        foreach (var child in ListDirectStructElementChildren(section))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var role = child.GetAsName(PdfName.S);
            if (RoleTr.Equals(role))
            {
                rows.Add(child);
            }
            else if (RoleTable.Equals(role))
            {
                hasNestedTable = true;
            }
        }

        return hasNestedTable;
    }

    private static string BuildFirstRowSnippet(IReadOnlyList<TableRowInventory> rows)
    {
        if (rows.Count == 0 || rows[0].Cells.Count == 0)
        {
            return string.Empty;
        }

        var snippet = string.Join(" | ", rows[0].Cells.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (snippet.Length > 160)
        {
            snippet = snippet[..160].Trim();
        }

        return snippet;
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
        var dereferenced = Dereference(node);
        if (dereferenced is null)
        {
            return;
        }

        node = dereferenced;

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

    private static void DemoteTableAndDescendants(PdfDictionary table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        table.Put(PdfName.S, RoleDiv);

        var stack = new Stack<PdfObject?>();
        stack.Push(table.Get(PdfName.K));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = stack.Pop();
            if (node is null)
            {
                continue;
            }

            var dereferenced = Dereference(node);
            if (dereferenced is null)
            {
                continue;
            }

            node = dereferenced;
            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    stack.Push(item);
                }

                continue;
            }

            if (node is not PdfDictionary dict)
            {
                continue;
            }

            if (dict.ContainsKey(PdfName.S))
            {
                var role = dict.GetAsName(PdfName.S);
                if (RoleTr.Equals(role) || RoleTd.Equals(role) || RoleTh.Equals(role) || RoleTable.Equals(role))
                {
                    dict.Put(PdfName.S, RoleDiv);
                }
            }

            var kids = dict.Get(PdfName.K);
            if (kids is not null)
            {
                stack.Push(kids);
            }
        }
    }

    private static List<PdfDictionary> ListStructElementsByRole(
        PdfDocument pdf,
        PdfName role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
        TraverseTagTreeForList(rootKids, role, results, cancellationToken);
        return results;
    }

    private static List<PdfDictionary> ListDescendantStructElementsByRole(
        PdfDictionary root,
        PdfName role,
        CancellationToken cancellationToken)
    {
        var results = new List<PdfDictionary>();
        var stack = new Stack<PdfObject?>();
        stack.Push(root.Get(PdfName.K));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = stack.Pop();
            if (node is null)
            {
                continue;
            }

            var dereferenced = Dereference(node);
            if (dereferenced is null)
            {
                continue;
            }

            node = dereferenced;
            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    stack.Push(item);
                }

                continue;
            }

            if (node is not PdfDictionary dict)
            {
                continue;
            }

            if (dict.ContainsKey(PdfName.S) && role.Equals(dict.GetAsName(PdfName.S)))
            {
                results.Add(dict);
            }

            var kids = dict.Get(PdfName.K);
            if (kids is not null)
            {
                stack.Push(kids);
            }
        }

        return results;
    }

    private static void TraverseTagTreeForList(
        PdfObject node,
        PdfName role,
        List<PdfDictionary> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dereferenced = Dereference(node);
        if (dereferenced is null)
        {
            return;
        }

        node = dereferenced;

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                TraverseTagTreeForList(item, role, results, cancellationToken);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        if (dict.ContainsKey(PdfName.S) && role.Equals(dict.GetAsName(PdfName.S)))
        {
            results.Add(dict);
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            TraverseTagTreeForList(kids, role, results, cancellationToken);
        }
    }

    private static List<PdfDictionary> ListDirectStructElementChildren(PdfDictionary structElem)
    {
        var kids = structElem.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var dereferenced = Dereference(kids);
        if (dereferenced is PdfDictionary kidDict)
        {
            return kidDict.ContainsKey(PdfName.S) ? [kidDict] : [];
        }

        if (dereferenced is not PdfArray array)
        {
            return [];
        }

        var results = new List<PdfDictionary>(array.Size());
        foreach (var item in array)
        {
            var itemDeref = Dereference(item);
            if (itemDeref is PdfDictionary itemDict && itemDict.ContainsKey(PdfName.S))
            {
                results.Add(itemDict);
            }
        }

        return results;
    }

    private static bool IsStructElemDictionary(PdfDictionary dict) => dict.ContainsKey(PdfName.S);

    private sealed record TableInventory(
        PdfDictionary Element,
        IReadOnlyList<TableRowInventory> Rows,
        int HeaderCellCount,
        int MaxColumnCount,
        bool HasNestedTable,
        string FirstRowSnippet)
    {
        public int RowCount => Rows.Count;

        public PdfTableClassificationRequest ToClassificationRequest(string? primaryLanguage) =>
            new(
                RowCount,
                MaxColumnCount,
                HasNestedTable,
                Rows
                    .Select(r => (IReadOnlyList<string>)r.Cells.Select(c => c.Text).ToList())
                    .ToList(),
                primaryLanguage);
    }

    private sealed record TableRowInventory(PdfDictionary Element, IReadOnlyList<TableCellInventory> Cells);

    private sealed record TableCellInventory(PdfDictionary Element, PdfName Role, string Text);

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

    private static PdfObject? Dereference(PdfObject? obj)
    {
        if (obj is null)
        {
            return null;
        }

        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }
}
