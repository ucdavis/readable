using iText.Kernel.Pdf;

namespace server.core.Remediate;

internal static class PdfTableRoleRemediator
{
    private static readonly PdfName RoleTable = new("Table");
    private static readonly PdfName RoleTr = new("TR");
    private static readonly PdfName RoleTh = new("TH");
    private static readonly PdfName RoleTd = new("TD");
    private static readonly PdfName RoleDiv = new("Div");

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

    private static void TraverseTagTreeForList(
        PdfObject node,
        PdfName role,
        List<PdfDictionary> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        node = Dereference(node);

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
        var tdCount = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cellCount = 0;
            foreach (var child in ListDirectStructElementChildren(row))
            {
                var role = child.GetAsName(PdfName.S);
                if (RoleTd.Equals(role))
                {
                    tdCount++;
                    cellCount++;
                }
                else if (RoleTh.Equals(role))
                {
                    // If a TH exists under a TR but wasn't found in the descendant scan for any reason, treat as data.
                    return false;
                }
            }

            maxCols = Math.Max(maxCols, cellCount);
        }

        // A /Table with no /TD (even with /TR) is likely a tagging mistake.
        if (tdCount == 0)
        {
            return true;
        }

        if (!demoteSmallTablesWithoutHeaders)
        {
            return false;
        }

        // Heuristic: small /Table elements without headers are very often layout tables.
        var cellUpperBound = rowCount * Math.Max(1, maxCols);
        if (rowCount <= 1 || maxCols <= 1)
        {
            return true;
        }

        return rowCount <= 2 && maxCols <= 2 && cellUpperBound <= 4;
    }

    private static void DemoteTableAndDescendants(PdfDictionary table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        table.Put(PdfName.S, RoleDiv);

        var stack = new Stack<PdfObject>();
        stack.Push(table.Get(PdfName.K));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = stack.Pop();
            if (node is null)
            {
                continue;
            }

            node = Dereference(node);
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

    private static List<PdfDictionary> ListDescendantStructElementsByRole(
        PdfDictionary root,
        PdfName role,
        CancellationToken cancellationToken)
    {
        var results = new List<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        stack.Push(root.Get(PdfName.K));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = stack.Pop();
            if (node is null)
            {
                continue;
            }

            node = Dereference(node);

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

    private static List<PdfDictionary> ListDirectStructElementChildren(PdfDictionary structElem)
    {
        var kids = structElem.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        kids = Dereference(kids);

        if (kids is PdfDictionary kidDict)
        {
            return kidDict.ContainsKey(PdfName.S) ? [kidDict] : [];
        }

        if (kids is not PdfArray array)
        {
            return [];
        }

        var results = new List<PdfDictionary>(array.Size());
        foreach (var item in array)
        {
            var deref = Dereference(item);
            if (deref is PdfDictionary itemDict && itemDict.ContainsKey(PdfName.S))
            {
                results.Add(itemDict);
            }
        }

        return results;
    }

    private static PdfObject Dereference(PdfObject obj)
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }
}
