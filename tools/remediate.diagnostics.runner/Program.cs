using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;

internal static class Program
{
    public static int Main(string[] args)
    {
        var (inputPath, extractImagesDir) = ParseArgs(args);
        if (inputPath is null)
        {
            PrintUsage();
            return 2;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input PDF not found: {inputPath}");
            return 2;
        }

        using var pdf = new PdfDocument(new PdfReader(inputPath));

        Console.WriteLine($"Input: {inputPath}");
        Console.WriteLine($"Pages: {pdf.GetNumberOfPages()}");
        Console.WriteLine($"Tagged: {pdf.IsTagged()}");

        var catalog = pdf.GetCatalog().GetPdfObject();
        var structTreeRoot = catalog.GetAsDictionary(PdfName.StructTreeRoot);
        Console.WriteLine($"StructTreeRoot: {(structTreeRoot is null ? "no" : "yes")}");
        if (structTreeRoot is not null)
        {
            var parentTree = structTreeRoot.Get(PdfName.ParentTree);
            Console.WriteLine($"ParentTree: {(parentTree is null || parentTree is PdfNull ? "no" : "yes")}");
        }

        var pageObjNumToPageNumber = BuildPageObjectNumberToPageNumberMap(pdf);
        var figures = ListStructElementsByRole(pdf, PdfName.Figure);
        var figureAnalysis = AnalyzeFigures(figures, pageObjNumToPageNumber);

        Console.WriteLine();
        Console.WriteLine("Tag tree:");
        Console.WriteLine($"- Figures: {figures.Count}");
        Console.WriteLine($"- Figures with non-empty /Alt: {figureAnalysis.WithAlt}");
        Console.WriteLine($"- Figures missing /Alt: {figureAnalysis.MissingAlt}");
        Console.WriteLine($"- Figures with fallback /Alt (\"alt text for image\"): {figureAnalysis.FallbackAltCount}");
        Console.WriteLine($"- Figures with any MCID references: {figureAnalysis.WithMcidRefs}");
        Console.WriteLine($"- Figures with any OBJR references: {figureAnalysis.WithObjRefs}");
        Console.WriteLine($"- Figures with NO MCID/OBJR references: {figureAnalysis.WithNoContentRefs}");
        Console.WriteLine($"- Figures with resolvable page association: {figureAnalysis.PageResolved}");

        var imageOccurrences = new List<ImageOccurrence>();
        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            imageOccurrences.AddRange(ListImageOccurrences(pdf.GetPage(pageNumber), pageNumber));
        }

        var distinctImageXObjects = CountImageXObjects(pdf);

        var figureIndex = BuildStructTreeIndexForRole(figures, pageObjNumToPageNumber);
        var matchStats = MatchImageOccurrencesToFigures(imageOccurrences, figureIndex);
        var figureMcids = figureIndex.StructElemsByMcid.Keys.ToHashSet();
        var imageMcids = imageOccurrences
            .Where(o => o.Mcid >= 0)
            .Select(o => (o.PageNumber, o.Mcid))
            .ToHashSet();
        var figureMcidsWithoutImages = figureMcids.Except(imageMcids).Count();

        Console.WriteLine();
        Console.WriteLine("Rendered images:");
        Console.WriteLine($"- Image render occurrences (content stream): {imageOccurrences.Count}");
        Console.WriteLine($"- Distinct image XObjects (resources): {distinctImageXObjects}");
        Console.WriteLine($"- Unique /Figure MCIDs referenced: {figureMcids.Count}");
        Console.WriteLine($"- Unique MCIDs with image render occurrences: {imageMcids.Count}");
        Console.WriteLine($"- /Figure MCIDs with no image render occurrence: {figureMcidsWithoutImages}");
        Console.WriteLine($"- Occurrences with MCID >= 0: {matchStats.OccurrencesWithMcid}");
        Console.WriteLine($"- Occurrences with indirect image ref: {matchStats.OccurrencesWithObjRef}");
        Console.WriteLine($"- Occurrences matched to at least one /Figure: {matchStats.MatchedOccurrences}");
        Console.WriteLine($"- Occurrences NOT matched to a /Figure: {matchStats.UnmatchedOccurrences}");
        Console.WriteLine($"- Distinct matched /Figure elements: {matchStats.DistinctMatchedFigures}");

        if (extractImagesDir is not null)
        {
            Console.WriteLine();
            ExtractImages(imageOccurrences, extractImagesDir);
        }

        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools/remediate.diagnostics.runner -- --input <path> [--extract-images <dir>]");
    }

    private static (string? InputPath, string? ExtractImagesDir) ParseArgs(string[] args)
    {
        string? inputPath = null;
        string? extractImagesDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg is "--input" or "-i") && i + 1 < args.Length)
            {
                inputPath = args[++i];
                continue;
            }

            if (arg is "--extract-images" && i + 1 < args.Length)
            {
                extractImagesDir = args[++i];
                continue;
            }
        }

        return (inputPath, extractImagesDir);
    }

    private sealed record FigureAnalysis(
        int WithAlt,
        int MissingAlt,
        int FallbackAltCount,
        int WithMcidRefs,
        int WithObjRefs,
        int WithNoContentRefs,
        int PageResolved);

    private static FigureAnalysis AnalyzeFigures(IReadOnlyList<PdfDictionary> figures, Dictionary<int, int> pageObjNumToPageNumber)
    {
        var withAlt = 0;
        var missingAlt = 0;
        var fallbackAlt = 0;
        var withMcidRefs = 0;
        var withObjRefs = 0;
        var withNoRefs = 0;
        var pageResolved = 0;

        foreach (var figure in figures)
        {
            var alt = figure.GetAsString(PdfName.Alt)?.ToUnicodeString();
            if (string.IsNullOrWhiteSpace(alt))
            {
                missingAlt++;
            }
            else
            {
                withAlt++;
                if (string.Equals(alt.Trim(), "alt text for image", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackAlt++;
                }
            }

            var refs = AnalyzeFigureContentRefs(figure);
            if (refs.HasMcidRef)
            {
                withMcidRefs++;
            }

            if (refs.HasObjRef)
            {
                withObjRefs++;
            }

            if (!refs.HasMcidRef && !refs.HasObjRef)
            {
                withNoRefs++;
            }

            if (TryResolveStructElemPageNumber(figure, pageObjNumToPageNumber) is not null)
            {
                pageResolved++;
            }
        }

        return new FigureAnalysis(
            WithAlt: withAlt,
            MissingAlt: missingAlt,
            FallbackAltCount: fallbackAlt,
            WithMcidRefs: withMcidRefs,
            WithObjRefs: withObjRefs,
            WithNoContentRefs: withNoRefs,
            PageResolved: pageResolved);
    }

    private sealed record FigureContentRefs(bool HasMcidRef, bool HasObjRef);

    private static FigureContentRefs AnalyzeFigureContentRefs(PdfDictionary figure)
    {
        var hasMcid = false;
        var hasObj = false;

        var kids = figure.Get(PdfName.K);
        if (kids is null || kids is PdfNull)
        {
            return new FigureContentRefs(HasMcidRef: false, HasObjRef: false);
        }

        const int maxNodesToScan = 20_000;
        var visited = new HashSet<(int objNum, int genNum)>();
        var stack = new Stack<PdfObject>();
        stack.Push(kids);

        var scanned = 0;
        while (stack.Count > 0 && scanned < maxNodesToScan)
        {
            var node = stack.Pop();
            node = Dereference(node, visited);
            scanned++;

            if (node is PdfNull)
            {
                continue;
            }

            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    stack.Push(item);
                }

                continue;
            }

            if (node is PdfNumber)
            {
                hasMcid = true;
                continue;
            }

            if (node is not PdfDictionary dict)
            {
                continue;
            }

            if (dict.GetAsNumber(PdfName.MCID) is not null)
            {
                hasMcid = true;
            }

            if (dict.Get(PdfName.Obj) is not null)
            {
                hasObj = true;
            }

            if (hasMcid && hasObj)
            {
                break;
            }

            var nestedKids = dict.Get(PdfName.K);
            if (nestedKids is not null && nestedKids is not PdfNull)
            {
                stack.Push(nestedKids);
            }
        }

        return new FigureContentRefs(HasMcidRef: hasMcid, HasObjRef: hasObj);
    }

    private sealed record ImageOccurrence(
        int PageNumber,
        int Mcid,
        PdfImageXObject Image,
        PdfIndirectReference? ImageRef,
        int WidthPx,
        int HeightPx);

    private static IReadOnlyList<ImageOccurrence> ListImageOccurrences(PdfPage page, int pageNumber)
    {
        var listener = new ImageOccurrenceListener(pageNumber);
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        return listener.GetOccurrences();
    }

    private sealed class ImageOccurrenceListener : IEventListener
    {
        private readonly List<PendingImage> _pending = new();
        private readonly int _pageNumber;

        public ImageOccurrenceListener(int pageNumber)
        {
            _pageNumber = pageNumber;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo imageRenderInfo)
            {
                return;
            }

            var image = imageRenderInfo.GetImage();
            var imageRef = image.GetPdfObject().GetIndirectReference();

            var imageDict = image.GetPdfObject();
            var width = imageDict.GetAsNumber(PdfName.Width)?.IntValue() ?? 0;
            var height = imageDict.GetAsNumber(PdfName.Height)?.IntValue() ?? 0;

            _pending.Add(new PendingImage(imageRenderInfo.GetMcid(), image, imageRef, width, height));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE };

        public IReadOnlyList<ImageOccurrence> GetOccurrences()
            => _pending
                .Select(p =>
                    new ImageOccurrence(
                        _pageNumber,
                        p.Mcid,
                        p.Image,
                        p.ImageRef,
                        p.WidthPx,
                        p.HeightPx))
                .ToArray();

        private sealed record PendingImage(int Mcid, PdfImageXObject Image, PdfIndirectReference? ImageRef, int WidthPx, int HeightPx);
    }

    private sealed record StructTreeIndex(
        Dictionary<(int pageNumber, int mcid), List<PdfDictionary>> StructElemsByMcid,
        Dictionary<(int pageNumber, int objNum, int genNum), List<PdfDictionary>> StructElemsByObjRef);

    private static StructTreeIndex BuildStructTreeIndexForRole(
        IReadOnlyList<PdfDictionary> figures,
        Dictionary<int, int> pageObjNumToPageNumber)
    {
        var byMcid = new Dictionary<(int pageNumber, int mcid), List<PdfDictionary>>();
        var byObjRef = new Dictionary<(int pageNumber, int objNum, int genNum), List<PdfDictionary>>();

        foreach (var figure in figures)
        {
            IndexStructElemContent(figure, pageObjNumToPageNumber, byMcid, byObjRef);
        }

        return new StructTreeIndex(byMcid, byObjRef);
    }

    private static void IndexStructElemContent(
        PdfDictionary structElem,
        Dictionary<int, int> pageObjNumToPageNumber,
        Dictionary<(int pageNumber, int mcid), List<PdfDictionary>> structElemsByMcid,
        Dictionary<(int pageNumber, int objNum, int genNum), List<PdfDictionary>> structElemsByObjRef)
    {
        var defaultPageDict = structElem.GetAsDictionary(PdfName.Pg);
        var kids = structElem.Get(PdfName.K);
        if (kids is null || kids is PdfNull)
        {
            return;
        }

        const int maxNodesToScan = 200_000;
        var nodesScanned = 0;

        var visited = new HashSet<(int objNum, int genNum)>();
        var stack = new Stack<(PdfObject Node, PdfDictionary? InheritedPageDict)>();
        stack.Push((kids, defaultPageDict));

        while (stack.Count > 0 && nodesScanned < maxNodesToScan)
        {
            var (node, inheritedPageDict) = stack.Pop();
            node = Dereference(node, visited);
            nodesScanned++;

            if (node is PdfNull)
            {
                continue;
            }

            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    stack.Push((item, inheritedPageDict));
                }

                continue;
            }

            if (node is PdfNumber num)
            {
                var pageNumber = TryResolvePageNumber(inheritedPageDict, pageObjNumToPageNumber);
                if (pageNumber is not null)
                {
                    AddMapping(structElemsByMcid, (pageNumber.Value, num.IntValue()), structElem);
                }

                continue;
            }

            if (node is not PdfDictionary dict)
            {
                continue;
            }

            // Nested struct elem: recurse into its kids, but index mappings to the parent figure.
            if (dict.ContainsKey(PdfName.S))
            {
                var nestedPageDict = dict.GetAsDictionary(PdfName.Pg) ?? inheritedPageDict;
                var nestedKids = dict.Get(PdfName.K);
                if (nestedKids is not null && nestedKids is not PdfNull)
                {
                    stack.Push((nestedKids, nestedPageDict));
                }

                continue;
            }

            var pageDict = dict.GetAsDictionary(PdfName.Pg) ?? inheritedPageDict;

            var mcidNum = dict.GetAsNumber(PdfName.MCID);
            if (mcidNum is not null)
            {
                var pageNumber = TryResolvePageNumber(pageDict, pageObjNumToPageNumber);
                if (pageNumber is not null)
                {
                    AddMapping(structElemsByMcid, (pageNumber.Value, mcidNum.IntValue()), structElem);
                }
            }

            var obj = dict.Get(PdfName.Obj);
            if (obj is not null)
            {
                var objRef = obj.GetIndirectReference() ?? (obj as PdfIndirectReference);
                if (objRef is not null)
                {
                    var pageNumber = TryResolvePageNumber(pageDict, pageObjNumToPageNumber);
                    if (pageNumber is not null)
                    {
                        AddMapping(structElemsByObjRef, (pageNumber.Value, objRef.GetObjNumber(), objRef.GetGenNumber()), structElem);
                    }
                }
            }
        }
    }

    private static void AddMapping<TKey>(Dictionary<TKey, List<PdfDictionary>> map, TKey key, PdfDictionary structElem)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<PdfDictionary>(capacity: 1);
            map[key] = list;
        }

        if (!list.Contains(structElem))
        {
            list.Add(structElem);
        }
    }

    private sealed record MatchStats(
        int OccurrencesWithMcid,
        int OccurrencesWithObjRef,
        int MatchedOccurrences,
        int UnmatchedOccurrences,
        int DistinctMatchedFigures);

    private static MatchStats MatchImageOccurrencesToFigures(IReadOnlyList<ImageOccurrence> occurrences, StructTreeIndex index)
    {
        var occurrencesWithMcid = 0;
        var occurrencesWithObjRef = 0;
        var matched = 0;
        var unmatched = 0;

        var matchedFigures = new HashSet<(int objNum, int genNum)>();

        foreach (var occ in occurrences)
        {
            if (occ.Mcid >= 0)
            {
                occurrencesWithMcid++;
            }

            if (occ.ImageRef is not null)
            {
                occurrencesWithObjRef++;
            }

            var figures = ResolveFigures(index, occ.PageNumber, occ.Mcid, occ.ImageRef);
            if (figures.Count == 0)
            {
                unmatched++;
                continue;
            }

            matched++;
            foreach (var figure in figures)
            {
                var figureRef = figure.GetIndirectReference();
                if (figureRef is not null)
                {
                    matchedFigures.Add((figureRef.GetObjNumber(), figureRef.GetGenNumber()));
                }
            }
        }

        return new MatchStats(
            OccurrencesWithMcid: occurrencesWithMcid,
            OccurrencesWithObjRef: occurrencesWithObjRef,
            MatchedOccurrences: matched,
            UnmatchedOccurrences: unmatched,
            DistinctMatchedFigures: matchedFigures.Count);
    }

    private static IReadOnlyList<PdfDictionary> ResolveFigures(
        StructTreeIndex index,
        int pageNumber,
        int mcid,
        PdfIndirectReference? objRef)
    {
        if (objRef is not null
            && index.StructElemsByObjRef.TryGetValue((pageNumber, objRef.GetObjNumber(), objRef.GetGenNumber()), out var byObj))
        {
            return byObj;
        }

        if (mcid >= 0 && index.StructElemsByMcid.TryGetValue((pageNumber, mcid), out var byMcid))
        {
            return byMcid;
        }

        return Array.Empty<PdfDictionary>();
    }

    private static IReadOnlyList<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
    {
        var catalog = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalog.GetAsDictionary(PdfName.StructTreeRoot);
        if (structTreeRootDict is null)
        {
            return Array.Empty<PdfDictionary>();
        }

        var rootKids = structTreeRootDict.Get(PdfName.K);
        if (rootKids is null || rootKids is PdfNull)
        {
            return Array.Empty<PdfDictionary>();
        }

        const int maxNodesToScan = 200_000;
        var nodesScanned = 0;

        var visited = new HashSet<(int objNum, int genNum)>();
        var stack = new Stack<PdfObject>();
        stack.Push(rootKids);

        var results = new List<PdfDictionary>();

        while (stack.Count > 0 && nodesScanned < maxNodesToScan)
        {
            var node = stack.Pop();
            node = Dereference(node, visited);
            nodesScanned++;

            if (node is PdfNull)
            {
                continue;
            }

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

            var s = dict.GetAsName(PdfName.S);
            if (role.Equals(s))
            {
                results.Add(dict);
            }

            var kids = dict.Get(PdfName.K);
            if (kids is not null && kids is not PdfNull)
            {
                stack.Push(kids);
            }
        }

        return results;
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

    private static int? TryResolveStructElemPageNumber(PdfDictionary structElem, Dictionary<int, int> pageObjNumToPageNumber)
    {
        var direct = TryResolvePageNumber(structElem.GetAsDictionary(PdfName.Pg), pageObjNumToPageNumber);
        if (direct is not null)
        {
            return direct;
        }

        var kids = structElem.Get(PdfName.K);
        if (kids is null || kids is PdfNull)
        {
            return null;
        }

        const int maxNodesToScan = 20_000;
        var visited = new HashSet<(int objNum, int genNum)>();
        var stack = new Stack<PdfObject>();
        stack.Push(kids);

        var scanned = 0;
        while (stack.Count > 0 && scanned < maxNodesToScan)
        {
            var node = stack.Pop();
            node = Dereference(node, visited);
            scanned++;

            if (node is PdfNull)
            {
                continue;
            }

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

            var resolved = TryResolvePageNumber(dict.GetAsDictionary(PdfName.Pg), pageObjNumToPageNumber);
            if (resolved is not null)
            {
                return resolved;
            }

            var nestedKids = dict.Get(PdfName.K);
            if (nestedKids is not null && nestedKids is not PdfNull)
            {
                stack.Push(nestedKids);
            }
        }

        return null;
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

    private static int CountImageXObjects(PdfDocument pdf)
    {
        var visited = new HashSet<(int objNum, int genNum)>();
        var count = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            var page = pdf.GetPage(pageNumber);
            var resources = page.GetPdfObject().GetAsDictionary(PdfName.Resources);
            count += CountImageXObjectsInResources(resources, visited);
        }

        return count;
    }

    private static int CountImageXObjectsInResources(PdfDictionary? resources, HashSet<(int objNum, int genNum)> visited)
    {
        if (resources is null)
        {
            return 0;
        }

        var xObjects = resources.GetAsDictionary(PdfName.XObject);
        if (xObjects is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var key in xObjects.KeySet())
        {
            var xObject = xObjects.Get(key);
            if (xObject is null || xObject is PdfNull)
            {
                continue;
            }

            var deref = Dereference(xObject, visited);
            if (deref is not PdfStream stream)
            {
                continue;
            }

            var subtype = stream.GetAsName(PdfName.Subtype);
            if (PdfName.Image.Equals(subtype))
            {
                count++;
                continue;
            }

            if (PdfName.Form.Equals(subtype))
            {
                count += CountImageXObjectsInResources(stream.GetAsDictionary(PdfName.Resources), visited);
            }
        }

        return count;
    }

    private static void ExtractImages(IReadOnlyList<ImageOccurrence> occurrences, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var seen = new HashSet<(int objNum, int genNum)>();
        var inlineIndex = 0;
        var extracted = 0;

        foreach (var occ in occurrences)
        {
            var bytes = TryGetImageBytes(occ.Image);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            var ext = GuessImageExtension(bytes) ?? ".bin";

            string fileName;
            if (occ.ImageRef is not null)
            {
                var key = (objNum: occ.ImageRef.GetObjNumber(), genNum: occ.ImageRef.GetGenNumber());
                if (!seen.Add(key))
                {
                    continue;
                }

                fileName = $"img-obj{key.objNum}-{key.genNum}{ext}";
            }
            else
            {
                fileName = $"img-inline-{inlineIndex++}{ext}";
            }

            var outPath = Path.Combine(outputDir, fileName);
            File.WriteAllBytes(outPath, bytes);
            extracted++;
        }

        Console.WriteLine($"Extracted {extracted} image file(s) to: {outputDir}");
    }

    private static byte[]? TryGetImageBytes(PdfImageXObject image)
    {
        try
        {
            return image.GetImageBytes(true);
        }
        catch
        {
            try
            {
                return image.GetImageBytes(false);
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? GuessImageExtension(byte[] bytes)
    {
        if (LooksLikePng(bytes))
        {
            return ".png";
        }

        if (LooksLikeJpeg(bytes))
        {
            return ".jpg";
        }

        if (LooksLikeJpeg2000(bytes))
        {
            return ".jp2";
        }

        return null;
    }

    private static bool LooksLikePng(byte[] bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == 0x50
        && bytes[2] == 0x4E
        && bytes[3] == 0x47
        && bytes[4] == 0x0D
        && bytes[5] == 0x0A
        && bytes[6] == 0x1A
        && bytes[7] == 0x0A;

    private static bool LooksLikeJpeg(byte[] bytes) =>
        bytes.Length >= 3
        && bytes[0] == 0xFF
        && bytes[1] == 0xD8
        && bytes[2] == 0xFF;

    private static bool LooksLikeJpeg2000(byte[] bytes) =>
        bytes.Length >= 12
        && bytes[0] == 0x00
        && bytes[1] == 0x00
        && bytes[2] == 0x00
        && bytes[3] == 0x0C
        && bytes[4] == 0x6A
        && bytes[5] == 0x50
        && bytes[6] == 0x20
        && bytes[7] == 0x20
        && bytes[8] == 0x0D
        && bytes[9] == 0x0A
        && bytes[10] == 0x87
        && bytes[11] == 0x0A;
}
