using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Navigation;
using Microsoft.Extensions.Logging;

namespace server.core.Remediate.Bookmarks;

public sealed class PdfBookmarkService : IPdfBookmarkService
{
    private const int MaxBookmarkTitleChars = 200;
    private const int MaxBookmarks = 2000;

    private readonly ILogger<PdfBookmarkService> _logger;

    public PdfBookmarkService(ILogger<PdfBookmarkService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task EnsureBookmarksAsync(PdfDocument pdf, CancellationToken cancellationToken)
    {
        try
        {
            if (pdf is null)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!pdf.IsTagged())
            {
                return Task.CompletedTask;
            }

            if (HasOutlines(pdf))
            {
                return Task.CompletedTask;
            }

            var headings = ExtractHeadings(pdf, cancellationToken);
            if (headings.Count == 0)
            {
                return Task.CompletedTask;
            }

            pdf.InitializeOutlines();
            var outlineRoot = pdf.GetOutlines(updateOutlines: false);

            var outlineStack = new List<(int Level, PdfOutline Outline)>
            {
                (Level: 0, Outline: outlineRoot),
            };

            foreach (var heading in headings.Take(MaxBookmarks))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var level = Math.Clamp(heading.Level, 1, 6);
                while (outlineStack.Count > 0 && outlineStack[^1].Level >= level)
                {
                    outlineStack.RemoveAt(outlineStack.Count - 1);
                }

                var parent = outlineStack.Count > 0 ? outlineStack[^1].Outline : outlineRoot;
                var outline = parent.AddOutline(heading.Title);

                var page = pdf.GetPage(heading.PageNumber);
                var destination = heading.TopY is null
                    ? PdfExplicitDestination.CreateFit(page)
                    : PdfExplicitDestination.CreateFitH(page, heading.TopY.Value);

                outline.AddDestination(destination);

                outlineStack.Add((level, outline));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate PDF bookmarks; leaving document unchanged.");
        }

        return Task.CompletedTask;
    }

    private sealed record BookmarkHeading(int Level, int PageNumber, float? TopY, string Title);

    private static List<BookmarkHeading> ExtractHeadings(PdfDocument pdf, CancellationToken cancellationToken)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        if (structTreeRootDict is null)
        {
            return [];
        }

        var roleMap = structTreeRootDict.GetAsDictionary(PdfName.RoleMap);
        var rootKids = structTreeRootDict.Get(PdfName.K);
        if (rootKids is null)
        {
            return [];
        }

        var pageObjNumToPageNumber = BuildPageObjectNumberToPageNumberMap(pdf);

        var headingNodes = new List<HeadingNode>();
        var order = 0;
        TraverseTagTreeForHeadings(rootKids, roleMap, pageObjNumToPageNumber, headingNodes, ref order, cancellationToken);

        if (headingNodes.Count == 0)
        {
            return [];
        }

        var pagesToScan = headingNodes
            .SelectMany(h => h.McidRefs.Select(m => m.PageNumber))
            .Distinct()
            .Where(p => p >= 1 && p <= pdf.GetNumberOfPages())
            .ToArray();

        var pageMcidTextMaps = new Dictionary<int, IReadOnlyDictionary<int, McidText>>(pagesToScan.Length);
        foreach (var pageNumber in pagesToScan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                pageMcidTextMaps[pageNumber] = CollectMcidText(pdf.GetPage(pageNumber));
            }
            catch
            {
                pageMcidTextMaps[pageNumber] = new Dictionary<int, McidText>();
            }
        }

        var results = new List<BookmarkHeading>(headingNodes.Count);

        foreach (var node in headingNodes.OrderBy(h => h.DocumentOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageNumber = node.McidRefs.Count > 0 ? node.McidRefs[0].PageNumber : node.PageNumberFromPg;
            if (pageNumber is null || pageNumber <= 0 || pageNumber > pdf.GetNumberOfPages())
            {
                continue;
            }

            var (titleFromMcids, topY) = BuildHeadingTextAndTopY(node, pageMcidTextMaps);
            var title = RemediationHelpers.NormalizeWhitespace(titleFromMcids);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = RemediationHelpers.NormalizeWhitespace(node.TitleFallback ?? string.Empty);
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (title.Length > MaxBookmarkTitleChars)
            {
                title = title[..MaxBookmarkTitleChars].Trim();
            }

            results.Add(new BookmarkHeading(node.Level, pageNumber.Value, topY, title));
        }

        return results;
    }

    private static bool HasOutlines(PdfDocument pdf)
    {
        try
        {
            var catalog = pdf.GetCatalog().GetPdfObject();
            var outlinesRoot = catalog.GetAsDictionary(PdfName.Outlines);
            if (outlinesRoot is null)
            {
                return false;
            }

            var first = outlinesRoot.Get(PdfName.First);
            return first is not null && first is not PdfNull;
        }
        catch
        {
            return false;
        }
    }

    private sealed record HeadingNode(
        int Level,
        IReadOnlyList<McidRef> McidRefs,
        int? PageNumberFromPg,
        string? TitleFallback,
        int DocumentOrder);

    private sealed record McidRef(int PageNumber, int Mcid);

    private sealed record McidText(string Text, Rectangle? Bounds);

    private static (string Text, float? TopY) BuildHeadingTextAndTopY(
        HeadingNode node,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, McidText>> pageMcidTextMaps)
    {
        var seen = new HashSet<(int Page, int Mcid)>();
        var sb = new StringBuilder();
        float? topY = null;

        foreach (var mcidRef in node.McidRefs)
        {
            if (!seen.Add((mcidRef.PageNumber, mcidRef.Mcid)))
            {
                continue;
            }

            if (!pageMcidTextMaps.TryGetValue(mcidRef.PageNumber, out var mcidMap))
            {
                continue;
            }

            if (!mcidMap.TryGetValue(mcidRef.Mcid, out var mcidText))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mcidText.Text))
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(mcidText.Text);
            }

            if (mcidText.Bounds is not null)
            {
                var candidateTop = mcidText.Bounds.GetY() + mcidText.Bounds.GetHeight();
                topY = topY is null ? candidateTop : Math.Max(topY.Value, candidateTop);
            }
        }

        return (sb.ToString(), topY);
    }

    private static IReadOnlyDictionary<int, McidText> CollectMcidText(PdfPage page)
    {
        var listener = new McidTextListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        return listener.GetResult();
    }

    private sealed class McidTextListener : IEventListener
    {
        private readonly Dictionary<int, McidTextAccumulator> _byMcid = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo textRenderInfo)
            {
                return;
            }

            var mcid = textRenderInfo.GetMcid();
            if (mcid < 0)
            {
                return;
            }

            var text = textRenderInfo.GetActualText() ?? textRenderInfo.GetText() ?? string.Empty;
            text = RemediationHelpers.NormalizeWhitespace(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!_byMcid.TryGetValue(mcid, out var acc))
            {
                acc = new McidTextAccumulator();
                _byMcid[mcid] = acc;
            }

            acc.Append(text, GetTextBounds(textRenderInfo));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public IReadOnlyDictionary<int, McidText> GetResult()
        {
            var results = new Dictionary<int, McidText>(_byMcid.Count);
            foreach (var (mcid, acc) in _byMcid)
            {
                results[mcid] = new McidText(acc.GetText(), acc.Bounds);
            }

            return results;
        }
    }

    private sealed class McidTextAccumulator
    {
        private readonly StringBuilder _text = new();
        private bool _hasText;

        public Rectangle? Bounds { get; private set; }

        public void Append(string text, Rectangle bounds)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (_hasText && _text.Length > 0 && !char.IsWhiteSpace(_text[^1]) && !char.IsWhiteSpace(text[0]))
            {
                _text.Append(' ');
            }

            _text.Append(text);
            _hasText = true;

            Bounds = Bounds is null ? bounds : Union(Bounds, bounds);
        }

        public string GetText() => RemediationHelpers.NormalizeWhitespace(_text.ToString());
    }

    private static Rectangle Union(Rectangle a, Rectangle b)
    {
        var minX = Math.Min(a.GetX(), b.GetX());
        var minY = Math.Min(a.GetY(), b.GetY());
        var maxX = Math.Max(a.GetX() + a.GetWidth(), b.GetX() + b.GetWidth());
        var maxY = Math.Max(a.GetY() + a.GetHeight(), b.GetY() + b.GetHeight());
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rectangle GetTextBounds(TextRenderInfo textRenderInfo)
    {
        var ascent = textRenderInfo.GetAscentLine();
        var descent = textRenderInfo.GetDescentLine();

        var a0 = ascent.GetStartPoint();
        var a1 = ascent.GetEndPoint();
        var d0 = descent.GetStartPoint();
        var d1 = descent.GetEndPoint();

        var minX = Math.Min(Math.Min(a0.Get(0), a1.Get(0)), Math.Min(d0.Get(0), d1.Get(0)));
        var minY = Math.Min(Math.Min(a0.Get(1), a1.Get(1)), Math.Min(d0.Get(1), d1.Get(1)));
        var maxX = Math.Max(Math.Max(a0.Get(0), a1.Get(0)), Math.Max(d0.Get(0), d1.Get(0)));
        var maxY = Math.Max(Math.Max(a0.Get(1), a1.Get(1)), Math.Max(d0.Get(1), d1.Get(1)));

        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
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

    private static void TraverseTagTreeForHeadings(
        PdfObject node,
        PdfDictionary? roleMap,
        Dictionary<int, int> pageObjNumToPageNumber,
        List<HeadingNode> results,
        ref int order,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        node = Dereference(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                TraverseTagTreeForHeadings(item, roleMap, pageObjNumToPageNumber, results, ref order, cancellationToken);
            }

            return;
        }

        if (node is not PdfDictionary dict || !IsStructElemDictionary(dict))
        {
            return;
        }

        var role = dict.GetAsName(PdfName.S);
        if (role is not null)
        {
            var resolvedRole = ResolveRole(role, roleMap);
            if (TryGetHeadingLevel(resolvedRole, out var level))
            {
                var mcidRefs = CollectMcidRefs(dict, pageObjNumToPageNumber);
                var pageNumberFromPg = TryResolvePageNumber(dict.GetAsDictionary(PdfName.Pg), pageObjNumToPageNumber);
                var titleFallback = GetTitleFallback(dict);

                results.Add(new HeadingNode(level, mcidRefs, pageNumberFromPg, titleFallback, DocumentOrder: order));
                order++;
            }
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            TraverseTagTreeForHeadings(kids, roleMap, pageObjNumToPageNumber, results, ref order, cancellationToken);
        }
    }

    private static bool TryGetHeadingLevel(PdfName role, out int level)
    {
        level = 0;
        var value = role.GetValue();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "H", StringComparison.OrdinalIgnoreCase))
        {
            level = 1;
            return true;
        }

        if (value.Length == 2 && (value[0] == 'H' || value[0] == 'h') && char.IsDigit(value[1]))
        {
            var parsed = value[1] - '0';
            if (parsed is >= 1 and <= 6)
            {
                level = parsed;
                return true;
            }
        }

        return false;
    }

    private static PdfName ResolveRole(PdfName role, PdfDictionary? roleMap)
    {
        if (roleMap is null)
        {
            return role;
        }

        var resolved = role;
        for (var i = 0; i < 5; i++)
        {
            var mapped = roleMap.GetAsName(resolved);
            if (mapped is null || mapped.Equals(resolved))
            {
                break;
            }

            resolved = mapped;
        }

        return resolved;
    }

    private static string? GetTitleFallback(PdfDictionary structElem)
    {
        var title = GetDictionaryText(structElem, PdfName.T);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        title = GetDictionaryText(structElem, PdfName.ActualText);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        title = GetDictionaryText(structElem, PdfName.Alt);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return null;
    }

    private static string? GetDictionaryText(PdfDictionary dict, PdfName key)
    {
        try
        {
            return dict.GetAsString(key)?.ToUnicodeString();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<McidRef> CollectMcidRefs(PdfDictionary structElem, Dictionary<int, int> pageObjNumToPageNumber)
    {
        var defaultPageDict = structElem.GetAsDictionary(PdfName.Pg);
        var kids = structElem.Get(PdfName.K);
        if (kids is null)
        {
            return [];
        }

        var mcids = new List<McidRef>();
        CollectMcidRefsRecursive(kids, defaultPageDict, pageObjNumToPageNumber, mcids);
        return mcids;
    }

    private static void CollectMcidRefsRecursive(
        PdfObject node,
        PdfDictionary? inheritedPageDict,
        Dictionary<int, int> pageObjNumToPageNumber,
        List<McidRef> results)
    {
        node = Dereference(node);

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                CollectMcidRefsRecursive(item, inheritedPageDict, pageObjNumToPageNumber, results);
            }

            return;
        }

        if (node is PdfNumber num)
        {
            var pageNumber = TryResolvePageNumber(inheritedPageDict, pageObjNumToPageNumber);
            if (pageNumber is not null)
            {
                results.Add(new McidRef(pageNumber.Value, num.IntValue()));
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
                CollectMcidRefsRecursive(kids, nestedPageDict, pageObjNumToPageNumber, results);
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
                results.Add(new McidRef(pageNumber.Value, mcidNum.IntValue()));
            }
        }
    }

    private static bool IsStructElemDictionary(PdfDictionary dict) => dict.ContainsKey(PdfName.S);

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

    private static PdfObject Dereference(PdfObject obj)
    {
        if (obj is PdfIndirectReference reference)
        {
            return reference.GetRefersTo(true);
        }

        return obj;
    }
}
