using System.Text;
using iText.IO.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using IOPath = System.IO.Path;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.core.Remediate;

public interface IPdfRemediationProcessor
{
    /// <summary>
    /// Applies PDF remediation steps and writes a remediated PDF to <paramref name="outputPdfPath" />.
    /// </summary>
    /// <remarks>
    /// This processor may modify metadata (such as the document title) even when the PDF is untagged.
    /// </remarks>
    Task<PdfRemediationResult> ProcessAsync(
        string fileId,
        string inputPdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken);
}

public sealed record PdfRemediationResult(string OutputPdfPath);

public sealed class NoopPdfRemediationProcessor : IPdfRemediationProcessor
{
    public async Task<PdfRemediationResult> ProcessAsync(
        string fileId,
        string inputPdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken)
    {
        _ = fileId;
        Directory.CreateDirectory(IOPath.GetDirectoryName(outputPdfPath)!);
        await using var input = File.OpenRead(inputPdfPath);
        await using var output = File.Open(outputPdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        return new PdfRemediationResult(outputPdfPath);
    }
}

public sealed class PdfRemediationProcessor : IPdfRemediationProcessor
{
    private const int ContextMaxCharsPerSide = 800;
    private const int TitleContextMinWords = 100;
    private const int TitleContextMaxPages = 5;
    private const int TitleMaxChars = 200;
    private const string TitlePlaceholder = "Untitled PDF document";
    private readonly IAltTextService _altTextService;
    private readonly IPdfTitleService _pdfTitleService;

    public PdfRemediationProcessor(IAltTextService altTextService, IPdfTitleService pdfTitleService)
    {
        _altTextService = altTextService ?? throw new ArgumentNullException(nameof(altTextService));
        _pdfTitleService = pdfTitleService ?? throw new ArgumentNullException(nameof(pdfTitleService));
    }

    /// <summary>
    /// Remediates a PDF by ensuring it has a title and (when tagged) adding missing alt text for figures and links.
    /// </summary>
    /// <remarks>
    /// Alt-text remediation relies on the PDF tag tree, so it only runs for tagged PDFs. A fallback pass ensures
    /// any remaining <c>/Figure</c> and <c>/Link</c> structure elements have some <c>/Alt</c> value even when exact
    /// content-to-tag matching is imperfect.
    /// </remarks>
    public async Task<PdfRemediationResult> ProcessAsync(
        string fileId,
        string inputPdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken)
    {
        _ = fileId; // TODO: not sure if we'll need it inside here

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(IOPath.GetDirectoryName(outputPdfPath)!);

        using var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath));

        await EnsurePdfHasTitleAsync(pdf, cancellationToken);

        if (!pdf.IsTagged())
        {
            return new PdfRemediationResult(outputPdfPath);
        }

        var pageObjNumToPageNumber = PdfStructTreeIndex.BuildPageObjectNumberToPageNumberMap(pdf);
        var figureIndex = PdfStructTreeIndex.BuildForRole(pdf, pageObjNumToPageNumber, PdfName.Figure);
        var linkIndex = PdfStructTreeIndex.BuildForRole(pdf, pageObjNumToPageNumber, PdfName.Link);

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pdf.GetPage(pageNumber);

            foreach (var occ in PdfContentScanner.ListImageOccurrences(page, pageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var figure = ResolveStructElem(figureIndex, pageNumber, occ.Mcid, occ.ObjectRef);
                if (figure is null || HasNonEmptyAlt(figure))
                {
                    continue;
                }

                var (bytes, mimeType) = ExtractImageBytes(occ.Image);
                var altText = await _altTextService.GetAltTextForImageAsync(
                    new ImageAltTextRequest(bytes, mimeType, occ.ContextBefore, occ.ContextAfter),
                    cancellationToken);
                SetAlt(figure, altText);
            }

            foreach (var occ in PdfContentScanner.ListLinkOccurrences(page, pageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (occ.AnnotationRef is null)
                {
                    continue;
                }

                var link = ResolveStructElem(linkIndex, pageNumber, mcid: null, occ.AnnotationRef);
                if (link is null || HasNonEmptyAlt(link))
                {
                    continue;
                }

                var target = TryGetLinkTarget(occ.LinkAnnotation);
                var altText = await _altTextService.GetAltTextForLinkAsync(
                    new LinkAltTextRequest(target, occ.LinkText, occ.ContextBefore, occ.ContextAfter),
                    cancellationToken);
                SetAlt(link, altText);
            }
        }

        // Fallback safety-net: ensure any remaining tagged Figures/Links get *some* alt text.
        // This keeps remediation robust even when we can't reliably match content-stream occurrences to tag-tree elements.
        foreach (var figure in PdfStructTreeIndex.ListStructElementsByRole(pdf, PdfName.Figure))
        {
            if (!HasNonEmptyAlt(figure))
            {
                SetAlt(figure, _altTextService.GetFallbackAltTextForImage());
            }
        }

        foreach (var link in PdfStructTreeIndex.ListStructElementsByRole(pdf, PdfName.Link))
        {
            if (!HasNonEmptyAlt(link))
            {
                SetAlt(link, _altTextService.GetFallbackAltTextForLink());
            }
        }

        return new PdfRemediationResult(outputPdfPath);
    }

    /// <summary>
    /// Ensures the PDF metadata has a reasonable title, generating one from early-page text when possible.
    /// </summary>
    private async Task EnsurePdfHasTitleAsync(PdfDocument pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = pdf.GetDocumentInfo();
        var currentTitle = TextContext.NormalizeWhitespace(info.GetTitle() ?? string.Empty);

        var (extractedText, wordCount) = ExtractTitleContext(pdf);
        if (wordCount < TitleContextMinWords)
        {
            if (string.IsNullOrWhiteSpace(currentTitle))
            {
                info.SetTitle(TitlePlaceholder);
            }

            return;
        }

        var suggestedTitle = await _pdfTitleService.GenerateTitleAsync(
            new PdfTitleRequest(currentTitle, extractedText),
            cancellationToken);

        suggestedTitle = NormalizeTitle(suggestedTitle, fallback: currentTitle);

        if (string.IsNullOrWhiteSpace(suggestedTitle))
        {
            suggestedTitle = TitlePlaceholder;
        }

        info.SetTitle(suggestedTitle);
    }

    /// <summary>
    /// Extracts text content from the initial pages of a PDF document to establish title context.
    /// </summary>
    /// <remarks>
    /// Scans up to <c>TitleContextMaxPages</c> pages and continues until at least
    /// <c>TitleContextMinWords</c> words are collected. Whitespace is normalized in the extracted text.
    /// Empty or whitespace-only pages are skipped during extraction.
    /// </remarks>
    private static (string ExtractedText, int WordCount) ExtractTitleContext(PdfDocument pdf)
    {
        var pagesToScan = Math.Min(pdf.GetNumberOfPages(), TitleContextMaxPages);
        if (pagesToScan <= 0)
        {
            return (string.Empty, 0);
        }

        var sb = new StringBuilder();
        var wordCount = 0;

        for (var pageNumber = 1; pageNumber <= pagesToScan; pageNumber++)
        {
            var page = pdf.GetPage(pageNumber);
            var pageText = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());
            pageText = TextContext.NormalizeWhitespace(pageText);
            if (string.IsNullOrWhiteSpace(pageText))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(pageText);
            wordCount = CountWords(sb.ToString());

            if (wordCount >= TitleContextMinWords)
            {
                break;
            }
        }

        var extracted = TextContext.NormalizeWhitespace(sb.ToString());
        return (extracted, wordCount);
    }

    /// <summary>
    /// Counts whitespace-delimited words in a string after normalizing whitespace.
    /// </summary>
    private static int CountWords(string text)
    {
        text = TextContext.NormalizeWhitespace(text);
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

    /// <summary>
    /// Normalizes and length-limits a title, falling back to the existing title when needed.
    /// </summary>
    private static string NormalizeTitle(string title, string fallback)
    {
        title = TextContext.NormalizeWhitespace(title);
        fallback = TextContext.NormalizeWhitespace(fallback);

        if (string.IsNullOrWhiteSpace(title))
        {
            return fallback;
        }

        if (title.Length > TitleMaxChars)
        {
            title = title[..TitleMaxChars].Trim();
        }

        return title;
    }

    /// <summary>
    /// Extracts raw image bytes from an iText image object and best-effort infers a MIME type.
    /// </summary>
    private static (byte[] Bytes, string MimeType) ExtractImageBytes(PdfImageXObject image)
    {
        byte[] bytes;
        try
        {
            bytes = image.GetImageBytes(true);
        }
        catch
        {
            bytes = image.GetImageBytes(false);
        }

        return (bytes, GuessImageMimeType(bytes) ?? "application/octet-stream");
    }

    /// <summary>
    /// Attempts to identify a common image MIME type from file signatures.
    /// </summary>
    private static string? GuessImageMimeType(byte[] bytes)
    {
        if (LooksLikePng(bytes))
        {
            return "image/png";
        }

        if (LooksLikeJpeg(bytes))
        {
            return "image/jpeg";
        }

        if (LooksLikeJpeg2000(bytes))
        {
            return "image/jp2";
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

    /// <summary>
    /// Best-effort extracts a link target from an annotation action.
    /// </summary>
    /// <remarks>
    /// Some link annotations do not use a simple <c>/URI</c> action; in those cases this may return a destination
    /// object string or <see langword="null" />.
    /// </remarks>
    private static string? TryGetLinkTarget(PdfLinkAnnotation linkAnnotation)
    {
        var action = linkAnnotation.GetAction();
        if (action is null)
        {
            return null;
        }

        try
        {
            var uri = action.GetAsString(PdfName.URI)?.ToUnicodeString();
            if (!string.IsNullOrWhiteSpace(uri))
            {
                return uri;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var dest = action.Get(PdfName.D);
            return dest?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a structure element for a page by object reference or MCID.
    /// </summary>
    /// <remarks>
    /// PDFs can associate tagged content via either MCIDs in marked content sequences or via explicit object
    /// references; object references take precedence when both are present.
    /// </remarks>
    private static PdfDictionary? ResolveStructElem(
        PdfStructTreeIndex index,
        int pageNumber,
        int? mcid,
        PdfIndirectReference? objRef)
    {
        if (objRef is not null && index.StructElemByObjRef.TryGetValue(
                (pageNumber, objRef.GetObjNumber(), objRef.GetGenNumber()),
                out var byObjRef))
        {
            return byObjRef;
        }

        if (mcid is not null && mcid.Value >= 0 && index.StructElemByMcid.TryGetValue((pageNumber, mcid.Value), out var byMcid))
        {
            return byMcid;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a structure element already has a non-empty <c>/Alt</c> entry.
    /// </summary>
    private static bool HasNonEmptyAlt(PdfDictionary structElem)
    {
        var alt = structElem.GetAsString(PdfName.Alt)?.ToUnicodeString();
        return !string.IsNullOrWhiteSpace(alt);
    }

    /// <summary>
    /// Writes an <c>/Alt</c> entry to a structure element if the provided text is non-empty.
    /// </summary>
    private static void SetAlt(PdfDictionary structElem, string altText)
    {
        if (string.IsNullOrWhiteSpace(altText))
        {
            return;
        }

        structElem.Put(PdfName.Alt, new PdfString(altText, PdfEncodings.UNICODE_BIG));
    }

    private sealed record ImageOccurrence(
        int PageNumber,
        int Mcid,
        PdfImageXObject Image,
        PdfIndirectReference? ObjectRef,
        string ContextBefore,
        string ContextAfter);

    private sealed record LinkOccurrence(
        int PageNumber,
        PdfLinkAnnotation LinkAnnotation,
        PdfIndirectReference? AnnotationRef,
        Rectangle? Rect,
        string LinkText,
        string ContextBefore,
        string ContextAfter);

    private static class PdfContentScanner
    {
        /// <summary>
        /// Scans a page content stream to locate rendered images and capture nearby text context.
        /// </summary>
        public static IReadOnlyList<ImageOccurrence> ListImageOccurrences(PdfPage page, int pageNumber)
        {
            var listener = new ImageOccurrenceListener(pageNumber);
            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            return listener.GetOccurrences();
        }

        /// <summary>
        /// Scans a page for link annotations and heuristically assigns link text and nearby context.
        /// </summary>
        /// <remarks>
        /// PDF link annotations do not reliably carry "visible text". This method uses annotation rectangles and
        /// text chunk bounds to approximate what a user sees, and falls back to nearest text when overlap is unknown.
        /// </remarks>
        public static IReadOnlyList<LinkOccurrence> ListLinkOccurrences(PdfPage page, int pageNumber)
        {
            var pageLinks = new List<(PdfLinkAnnotation Link, PdfIndirectReference? Ref, Rectangle? Rect)>();

            foreach (var annotation in page.GetAnnotations())
            {
                if (!PdfName.Link.Equals(annotation.GetSubtype()))
                {
                    continue;
                }

                var linkAnnotation = annotation as PdfLinkAnnotation
                    ?? PdfAnnotation.MakeAnnotation(annotation.GetPdfObject()) as PdfLinkAnnotation;

                if (linkAnnotation is null)
                {
                    continue;
                }

                Rectangle? rect = null;
                var rectArray = annotation.GetRectangle();
                if (rectArray is not null)
                {
                    try
                    {
                        rect = rectArray.ToRectangle();
                    }
                    catch
                    {
                        rect = null;
                    }
                }

                pageLinks.Add((linkAnnotation, annotation.GetPdfObject().GetIndirectReference(), rect));
            }

            if (pageLinks.Count == 0)
            {
                return Array.Empty<LinkOccurrence>();
            }

            var pageText = CollectPageText(page);
            var occurrences = new List<LinkOccurrence>(pageLinks.Count);

            foreach (var (linkAnnotation, linkRef, rect) in pageLinks)
            {
                var match = FindLinkTextMatch(pageText, rect);
                var (before, after) = TextContext.GetContextAroundCharRange(
                    pageText.Text,
                    match.StartIndex,
                    match.EndIndex,
                    maxCharsPerSide: ContextMaxCharsPerSide);

                occurrences.Add(
                    new LinkOccurrence(
                        pageNumber,
                        linkAnnotation,
                        linkRef,
                        rect,
                        LinkText: match.LinkText,
                        ContextBefore: before,
                        ContextAfter: after));
            }

            return occurrences;
        }

        private sealed class ImageOccurrenceListener : IEventListener
        {
            private readonly List<PendingImageOccurrence> _pending = new();
            private readonly TextAccumulator _pageText = new();
            private readonly int _pageNumber;

            public ImageOccurrenceListener(int pageNumber)
            {
                _pageNumber = pageNumber;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type == EventType.RENDER_TEXT && data is TextRenderInfo textRenderInfo)
                {
                    _pageText.Append(textRenderInfo);
                    return;
                }

                if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo imageRenderInfo)
                {
                    return;
                }

                var image = imageRenderInfo.GetImage();
                var imageRef = image.GetPdfObject().GetIndirectReference();

                _pending.Add(
                    new PendingImageOccurrence(
                        imageRenderInfo.GetMcid(),
                        image,
                        imageRef,
                        TextCharIndex: _pageText.Length));
            }

            public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE, EventType.RENDER_TEXT };

            public IReadOnlyList<ImageOccurrence> GetOccurrences()
            {
                var pageText = _pageText.GetText();
                var occurrences = new List<ImageOccurrence>(_pending.Count);

                foreach (var pending in _pending)
                {
                    var (before, after) = TextContext.GetContextAroundCharIndex(
                        pageText,
                        pending.TextCharIndex,
                        maxCharsPerSide: ContextMaxCharsPerSide);

                    occurrences.Add(
                        new ImageOccurrence(
                            _pageNumber,
                            pending.Mcid,
                            pending.Image,
                            pending.ImageRef,
                            ContextBefore: before,
                            ContextAfter: after));
                }

                return occurrences;
            }
        }

        private sealed record PendingImageOccurrence(int Mcid, PdfImageXObject Image, PdfIndirectReference? ImageRef, int TextCharIndex);

        private sealed record TextChunk(Rectangle Bounds, int StartIndex, int EndIndex, string Text);

        private sealed record PageTextResult(string Text, IReadOnlyList<TextChunk> Chunks);

        private sealed record LinkTextMatch(string LinkText, int StartIndex, int EndIndex);

        /// <summary>
        /// Collects page text while preserving per-chunk bounds and char ranges for spatial matching.
        /// </summary>
        private static PageTextResult CollectPageText(PdfPage page)
        {
            var listener = new PageTextListener();
            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            return listener.GetResult();
        }

        private sealed class PageTextListener : IEventListener
        {
            private readonly List<TextChunk> _chunks = new();
            private readonly TextAccumulator _pageText = new();

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT || data is not TextRenderInfo textRenderInfo)
                {
                    return;
                }

                if (!_pageText.TryAppend(textRenderInfo, out var startIndex, out var endIndex, out var text))
                {
                    return;
                }

                _chunks.Add(new TextChunk(GetTextBounds(textRenderInfo), startIndex, endIndex, text));
            }

            public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

            public PageTextResult GetResult() => new PageTextResult(_pageText.GetText(), _chunks);
        }

        /// <summary>
        /// Computes a conservative bounding rectangle for a rendered text run.
        /// </summary>
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

        /// <summary>
        /// Selects the most likely "visible text" for a link by matching text chunks to the link rectangle.
        /// </summary>
        /// <remarks>
        /// If no text overlaps the annotation rectangle, this falls back to the nearest text chunk by distance.
        /// </remarks>
        private static LinkTextMatch FindLinkTextMatch(PageTextResult pageText, Rectangle? linkRect)
        {
            if (pageText.Chunks.Count == 0)
            {
                return new LinkTextMatch(string.Empty, 0, 0);
            }

            if (linkRect is not null)
            {
                var overlapping = pageText.Chunks
                    .Where(c => c.Bounds.Overlaps(linkRect, 1f))
                    .ToArray();

                if (overlapping.Length > 0)
                {
                    var linkText = TextContext.NormalizeWhitespace(string.Join(' ', overlapping.Select(c => c.Text)));
                    var startIndex = overlapping.Min(c => c.StartIndex);
                    var endIndex = overlapping.Max(c => c.EndIndex);
                    return new LinkTextMatch(linkText, startIndex, endIndex);
                }
            }

            var referenceX = linkRect is null ? 0 : linkRect.GetX() + (linkRect.GetWidth() / 2f);
            var referenceY = linkRect is null ? 0 : linkRect.GetY() + (linkRect.GetHeight() / 2f);

            TextChunk? nearest = null;
            var bestDistanceSquared = float.PositiveInfinity;

            foreach (var chunk in pageText.Chunks)
            {
                var chunkX = chunk.Bounds.GetX() + (chunk.Bounds.GetWidth() / 2f);
                var chunkY = chunk.Bounds.GetY() + (chunk.Bounds.GetHeight() / 2f);
                var dx = chunkX - referenceX;
                var dy = chunkY - referenceY;
                var distanceSquared = (dx * dx) + (dy * dy);

                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    nearest = chunk;
                }
            }

            if (nearest is null)
            {
                return new LinkTextMatch(string.Empty, 0, 0);
            }

            return new LinkTextMatch(TextContext.NormalizeWhitespace(nearest.Text), nearest.StartIndex, nearest.EndIndex);
        }

        private sealed class TextAccumulator
        {
            private readonly StringBuilder _text = new();
            private bool _hasText;

            public int Length => _text.Length;

            public string GetText() => _text.ToString();

            public void Append(TextRenderInfo textRenderInfo) => _ = TryAppend(textRenderInfo, out _, out _, out _);

            public bool TryAppend(TextRenderInfo textRenderInfo, out int startIndex, out int endIndex, out string appendedText)
            {
                startIndex = 0;
                endIndex = 0;
                appendedText = textRenderInfo.GetActualText() ?? textRenderInfo.GetText() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(appendedText))
                {
                    return false;
                }

                if (_hasText && _text.Length > 0 && !char.IsWhiteSpace(_text[^1]) && !char.IsWhiteSpace(appendedText[0]))
                {
                    _text.Append(' ');
                }

                startIndex = _text.Length;
                _text.Append(appendedText);
                endIndex = _text.Length;
                _hasText = true;
                return true;
            }
        }
    }

    private static class TextContext
    {
        public static (string Before, string After) GetContextAroundCharIndex(string pageText, int charIndex, int maxCharsPerSide) =>
            GetContextAroundCharRange(pageText, charIndex, charIndex, maxCharsPerSide);

        public static (string Before, string After) GetContextAroundCharRange(
            string pageText,
            int rangeStart,
            int rangeEnd,
            int maxCharsPerSide)
        {
            if (string.IsNullOrWhiteSpace(pageText))
            {
                return (string.Empty, string.Empty);
            }

            rangeStart = Math.Clamp(rangeStart, 0, pageText.Length);
            rangeEnd = Math.Clamp(rangeEnd, 0, pageText.Length);
            if (rangeEnd < rangeStart)
            {
                (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
            }

            var beforeSource = pageText[..rangeStart];
            var afterSource = pageText[rangeEnd..];

            var before = KeepLastChars(beforeSource, maxCharsPerSide);
            var after = KeepFirstChars(afterSource, maxCharsPerSide);

            return (NormalizeWhitespace(before), NormalizeWhitespace(after));
        }

        public static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            var inWhitespace = true;

            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    inWhitespace = true;
                    continue;
                }

                if (inWhitespace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(ch);
                inWhitespace = false;
            }

            return sb.ToString().Trim();
        }

        private static string KeepFirstChars(string text, int maxChars)
        {
            if (maxChars <= 0 || text.Length <= maxChars)
            {
                return text;
            }

            return text[..maxChars].Trim();
        }

        private static string KeepLastChars(string text, int maxChars)
        {
            if (maxChars <= 0 || text.Length <= maxChars)
            {
                return text;
            }

            return text[^maxChars..].Trim();
        }
    }

    private sealed record PdfStructTreeIndex(
        Dictionary<(int pageNumber, int mcid), PdfDictionary> StructElemByMcid,
        Dictionary<(int pageNumber, int objNum, int genNum), PdfDictionary> StructElemByObjRef)
    {
        /// <summary>
        /// Builds a lookup from a page's indirect object number to its 1-based page number.
        /// </summary>
        public static Dictionary<int, int> BuildPageObjectNumberToPageNumberMap(PdfDocument pdf)
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

        /// <summary>
        /// Builds indices to resolve tag-tree structure elements for a specific role by MCID and object reference.
        /// </summary>
        /// <remarks>
        /// These indices are used to map rendered content occurrences (images/links) back to their corresponding
        /// <c>StructElem</c> nodes so <c>/Alt</c> can be set on the tag tree.
        /// </remarks>
        public static PdfStructTreeIndex BuildForRole(PdfDocument pdf, Dictionary<int, int> pageObjNumToPageNumber, PdfName targetRole)
        {
            var catalogDict = pdf.GetCatalog().GetPdfObject();
            var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
            if (structTreeRootDict is null)
            {
                return new PdfStructTreeIndex(new(), new());
            }

            var byMcid = new Dictionary<(int pageNumber, int mcid), PdfDictionary>();
            var byObjRef = new Dictionary<(int pageNumber, int objNum, int genNum), PdfDictionary>();

            var rootKids = structTreeRootDict.Get(PdfName.K);
            if (rootKids is null)
            {
                return new PdfStructTreeIndex(byMcid, byObjRef);
            }

            TraverseTagTree(rootKids, pageObjNumToPageNumber, targetRole, byMcid, byObjRef);
            return new PdfStructTreeIndex(byMcid, byObjRef);
        }

        /// <summary>
        /// Lists all structure elements in the tag tree that match a given role.
        /// </summary>
        public static IReadOnlyList<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
        {
            var catalogDict = pdf.GetCatalog().GetPdfObject();
            var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
            if (structTreeRootDict is null)
            {
                return Array.Empty<PdfDictionary>();
            }

            var rootKids = structTreeRootDict.Get(PdfName.K);
            if (rootKids is null)
            {
                return Array.Empty<PdfDictionary>();
            }

            var results = new List<PdfDictionary>();
            TraverseTagTreeForList(rootKids, role, results);
            return results;
        }

        private static void TraverseTagTreeForList(PdfObject node, PdfName role, List<PdfDictionary> results)
        {
            node = Dereference(node);

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

        private static void TraverseTagTree(
            PdfObject node,
            Dictionary<int, int> pageObjNumToPageNumber,
            PdfName targetRole,
            Dictionary<(int pageNumber, int mcid), PdfDictionary> structElemByMcid,
            Dictionary<(int pageNumber, int objNum, int genNum), PdfDictionary> structElemByObjRef)
        {
            node = Dereference(node);

            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    TraverseTagTree(item, pageObjNumToPageNumber, targetRole, structElemByMcid, structElemByObjRef);
                }

                return;
            }

            if (node is not PdfDictionary dict || !IsStructElemDictionary(dict))
            {
                return;
            }

            var role = dict.GetAsName(PdfName.S);
            if (targetRole.Equals(role))
            {
                IndexStructElemContent(dict, pageObjNumToPageNumber, structElemByMcid, structElemByObjRef);
            }

            var kids = dict.Get(PdfName.K);
            if (kids is not null)
            {
                TraverseTagTree(kids, pageObjNumToPageNumber, targetRole, structElemByMcid, structElemByObjRef);
            }
        }

        private static bool IsStructElemDictionary(PdfDictionary dict) => dict.ContainsKey(PdfName.S);

        private static void IndexStructElemContent(
            PdfDictionary structElem,
            Dictionary<int, int> pageObjNumToPageNumber,
            Dictionary<(int pageNumber, int mcid), PdfDictionary> structElemByMcid,
            Dictionary<(int pageNumber, int objNum, int genNum), PdfDictionary> structElemByObjRef)
        {
            var defaultPageDict = structElem.GetAsDictionary(PdfName.Pg);
            var kids = structElem.Get(PdfName.K);
            if (kids is null)
            {
                return;
            }

            IndexContentRecursive(kids, structElem, defaultPageDict, pageObjNumToPageNumber, structElemByMcid, structElemByObjRef);
        }

        private static void IndexContentRecursive(
            PdfObject node,
            PdfDictionary structElem,
            PdfDictionary? inheritedPageDict,
            Dictionary<int, int> pageObjNumToPageNumber,
            Dictionary<(int pageNumber, int mcid), PdfDictionary> structElemByMcid,
            Dictionary<(int pageNumber, int objNum, int genNum), PdfDictionary> structElemByObjRef)
        {
            node = Dereference(node);

            if (node is PdfArray array)
            {
                foreach (var item in array)
                {
                    IndexContentRecursive(item, structElem, inheritedPageDict, pageObjNumToPageNumber, structElemByMcid, structElemByObjRef);
                }

                return;
            }

            if (node is PdfNumber num)
            {
                var pageNumber = TryResolvePageNumber(inheritedPageDict, pageObjNumToPageNumber);
                if (pageNumber is not null)
                {
                    structElemByMcid.TryAdd((pageNumber.Value, num.IntValue()), structElem);
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
                    IndexContentRecursive(kids, structElem, nestedPageDict, pageObjNumToPageNumber, structElemByMcid, structElemByObjRef);
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
                    structElemByMcid.TryAdd((pageNumber.Value, mcidNum.IntValue()), structElem);
                }
            }

            var obj = dict.Get(PdfName.Obj);
            if (obj is null)
            {
                return;
            }

            var objRef = obj.GetIndirectReference() ?? (obj as PdfIndirectReference);
            if (objRef is null)
            {
                return;
            }

            var pageNumberObj = TryResolvePageNumber(pageDict, pageObjNumToPageNumber);
            if (pageNumberObj is not null)
            {
                structElemByObjRef.TryAdd((pageNumberObj.Value, objRef.GetObjNumber(), objRef.GetGenNumber()), structElem);
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

        private static PdfObject Dereference(PdfObject obj)
        {
            if (obj is PdfIndirectReference reference)
            {
                return reference.GetRefersTo(true);
            }

            return obj;
        }
    }
}
