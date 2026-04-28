using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging;

namespace server.core.Remediate;

internal static partial class PdfCharacterEncodingRemediator
{
    private const string Placeholder = "<?>";
    private const int MaxAiGroups = 40;
    private const int MaxContextsPerGroup = 6;
    private const int ContextCharsPerSide = 120;
    private static readonly Regex ExplicitBfCharRegex =
        new(@"<(?<src>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]*)>", RegexOptions.Compiled);
    private static readonly Regex TimestampPlaceholderRegex =
        new(@"\b\d<\?>\d{2}\s*(?:AM|PM)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<PdfCharacterEncodingRemediationResult> RemediateAsync(
        PdfDocument pdf,
        IPdfCharacterEncodingRepairService repairService,
        PdfRemediationOptions options,
        string? primaryLanguage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scan = Scan(pdf, cancellationToken);
        if (scan.Anomalies.Count == 0)
        {
            logger.LogInformation("PDF character encoding remediation: no rendered text anomalies detected.");
            return new PdfCharacterEncodingRemediationResult(0, 0, 0, 0);
        }

        var groups = GroupAnomalies(scan.Anomalies);
        var proposed = new List<RepairCandidate>();
        proposed.AddRange(BuildExistingMappingRepairs(groups, scan.FontsByObjectId));
        proposed.AddRange(BuildTimestampRepairs(groups));
        proposed.AddRange(BuildFillerRepairs(groups));

        var deterministicApplied = ApplyValidatedRepairs(pdf, groups, scan.FontsByObjectId, proposed, logger);

        var aiApplied = 0;
        if (options.UseAiCharacterEncodingRepair)
        {
            var afterDeterministic = deterministicApplied > 0 ? Scan(pdf, cancellationToken) : scan;
            var remainingGroups = GroupAnomalies(afterDeterministic.Anomalies)
                .Where(g => !HasAcceptedRepair(proposed, g.Key))
                .Take(MaxAiGroups)
                .ToArray();

            if (remainingGroups.Length > 0)
            {
                var request = new PdfCharacterEncodingRepairRequest(
                    remainingGroups.Select(ToRepairRequestGroup).ToArray(),
                    primaryLanguage);

                PdfCharacterEncodingRepairResponse response;
                try
                {
                    response = await repairService.ProposeRepairsAsync(request, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "PDF character encoding AI repair proposal failed.");
                    response = new PdfCharacterEncodingRepairResponse(Array.Empty<PdfCharacterEncodingRepairProposal>());
                }

                var aiCandidates = response.Repairs
                    .Select(r => new RepairCandidate(
                        new RepairKey(
                            RemediationHelpers.NormalizeWhitespace(r.FontObjectId),
                            RemediationHelpers.NormalizeWhitespace(r.SourceCode).ToUpperInvariant()),
                        r.Replacement,
                        r.Confidence,
                        "ai",
                        r.Reason))
                    .ToArray();

                aiApplied = ApplyValidatedRepairs(
                    pdf,
                    remainingGroups,
                    afterDeterministic.FontsByObjectId,
                    aiCandidates,
                    logger,
                    minimumConfidence: Math.Clamp(options.CharacterEncodingRepairConfidenceThreshold, 0, 1));
            }
        }

        var verification = Scan(pdf, cancellationToken);
        logger.LogInformation(
            "PDF character encoding remediation summary: detected={detected} groups={groups} deterministicApplied={deterministicApplied} aiApplied={aiApplied} remaining={remaining}",
            scan.Anomalies.Count,
            groups.Count,
            deterministicApplied,
            aiApplied,
            verification.Anomalies.Count);

        foreach (var anomaly in verification.Anomalies.Take(20))
        {
            logger.LogInformation(
                "Remaining PDF character encoding anomaly: page={page} mcid={mcid} fontObject={fontObject} font={font} sourceCode={sourceCode} kind={kind} line={line}",
                anomaly.PageNumber,
                anomaly.Mcid,
                anomaly.Key.FontObjectId,
                anomaly.FontName,
                anomaly.Key.SourceCode,
                anomaly.Kind,
                anomaly.LineWithPlaceholder);
        }

        return new PdfCharacterEncodingRemediationResult(
            scan.Anomalies.Count,
            groups.Count,
            deterministicApplied,
            aiApplied);
    }

    private static ScanResult Scan(PdfDocument pdf, CancellationToken cancellationToken)
    {
        var anomalies = new List<DetectedAnomaly>();
        var fontsByObjectId = new Dictionary<string, FontInfo>(StringComparer.Ordinal);

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listener = new CharacterEncodingListener(pageNumber, fontsByObjectId);
            new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(pageNumber));
            anomalies.AddRange(listener.GetAnomalies());
        }

        return new ScanResult(anomalies, fontsByObjectId);
    }

    private static IReadOnlyList<AnomalyGroup> GroupAnomalies(IReadOnlyList<DetectedAnomaly> anomalies)
        => anomalies
            .GroupBy(a => a.Key)
            .Select(g => new AnomalyGroup(g.Key, g.ToArray()))
            .ToArray();

    private static IEnumerable<RepairCandidate> BuildExistingMappingRepairs(
        IReadOnlyList<AnomalyGroup> groups,
        IReadOnlyDictionary<string, FontInfo> fontsByObjectId)
    {
        var validByEquivalentFont = new Dictionary<(string fontName, string sourceCode), HashSet<string>>();
        foreach (var font in fontsByObjectId.Values)
        {
            var equivalentName = NormalizeFontName(font.FontName);
            if (string.IsNullOrWhiteSpace(equivalentName))
            {
                continue;
            }

            foreach (var mapping in font.ToUnicode.ExplicitMappings)
            {
                if (!IsSafeReplacement(mapping.Value))
                {
                    continue;
                }

                var key = (equivalentName, mapping.Key);
                if (!validByEquivalentFont.TryGetValue(key, out var values))
                {
                    values = new HashSet<string>(StringComparer.Ordinal);
                    validByEquivalentFont[key] = values;
                }

                values.Add(mapping.Value);
            }
        }

        foreach (var group in groups)
        {
            if (!fontsByObjectId.TryGetValue(group.Key.FontObjectId, out var font))
            {
                continue;
            }

            var equivalentName = NormalizeFontName(font.FontName);
            if (!validByEquivalentFont.TryGetValue((equivalentName, group.Key.SourceCode), out var values) || values.Count != 1)
            {
                continue;
            }

            var replacement = values.Single();
            yield return new RepairCandidate(
                group.Key,
                replacement,
                Confidence: 1,
                Source: "existing_mapping",
                Reason: "Equivalent font contains one valid mapping for the same source code.");
        }
    }

    private static IEnumerable<RepairCandidate> BuildTimestampRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            if (group.Anomalies.Any(a => TimestampPlaceholderRegex.IsMatch(a.LineWithPlaceholder)))
            {
                yield return new RepairCandidate(
                    group.Key,
                    ":",
                    Confidence: 1,
                    Source: "timestamp_pattern",
                    Reason: "Anomaly appears between hour and minute digits before AM/PM.");
            }
        }
    }

    private static IEnumerable<RepairCandidate> BuildFillerRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            var allFillerOnly = true;

            foreach (var anomaly in group.Anomalies)
            {
                if (anomaly.EventText.Any(char.IsLetterOrDigit))
                {
                    allFillerOnly = false;
                    break;
                }

                foreach (var ch in anomaly.EventText)
                {
                    if (IsBadExtractedChar(ch) || char.IsWhiteSpace(ch) || char.IsLetterOrDigit(ch))
                    {
                        continue;
                    }

                    if (IsSafeFiller(ch))
                    {
                        candidates.Add(ch.ToString());
                    }
                }
            }

            if (allFillerOnly && candidates.Count == 1)
            {
                yield return new RepairCandidate(
                    group.Key,
                    candidates.Single(),
                    Confidence: 1,
                    Source: "filler_pattern",
                    Reason: "Anomaly is in a filler-only text event with one neighboring filler character.");
            }
        }
    }

    private static int ApplyValidatedRepairs(
        PdfDocument pdf,
        IReadOnlyList<AnomalyGroup> groups,
        IReadOnlyDictionary<string, FontInfo> fontsByObjectId,
        IReadOnlyList<RepairCandidate> candidates,
        ILogger logger,
        double minimumConfidence = 1)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var groupsByKey = groups.ToDictionary(g => g.Key, g => g);
        var applied = 0;

        foreach (var candidateGroup in candidates.GroupBy(c => c.Key))
        {
            var key = candidateGroup.Key;
            var alternatives = candidateGroup
                .Where(c => c.Confidence >= minimumConfidence)
                .Select(c => c with { Replacement = c.Replacement.Trim() })
                .Where(c => IsSafeReplacement(c.Replacement))
                .GroupBy(c => c.Replacement, StringComparer.Ordinal)
                .ToArray();

            if (alternatives.Length != 1)
            {
                logger.LogInformation(
                    "Skipped PDF character encoding repair due to missing or conflicting candidates: fontObject={fontObject} sourceCode={sourceCode}",
                    key.FontObjectId,
                    key.SourceCode);
                continue;
            }

            if (!groupsByKey.ContainsKey(key) || !fontsByObjectId.TryGetValue(key.FontObjectId, out var font))
            {
                continue;
            }

            var replacement = alternatives[0].Key;
            var existing = font.ToUnicode.Lookup(key.SourceCode);
            if (IsSafeReplacement(existing) && !string.Equals(existing, replacement, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Skipped PDF character encoding repair because a valid mapping already exists: fontObject={fontObject} sourceCode={sourceCode} existing={existing} replacement={replacement}",
                    key.FontObjectId,
                    key.SourceCode,
                    existing,
                    replacement);
                continue;
            }

            if (font.ToUnicode.Stream is null)
            {
                logger.LogInformation(
                    "Skipped PDF character encoding repair because the font has no editable /ToUnicode stream: fontObject={fontObject} sourceCode={sourceCode}",
                    key.FontObjectId,
                    key.SourceCode);
                continue;
            }

            if (!TryPatchToUnicodeStream(font.ToUnicode.Stream, key.SourceCode, replacement, out var action))
            {
                logger.LogInformation(
                    "Skipped PDF character encoding repair because the /ToUnicode stream could not be patched: fontObject={fontObject} sourceCode={sourceCode}",
                    key.FontObjectId,
                    key.SourceCode);
                continue;
            }

            font.ToUnicode.ExplicitMappings[key.SourceCode] = replacement;
            applied++;

            var chosen = alternatives[0].OrderByDescending(c => c.Confidence).First();
            logger.LogInformation(
                "Applied PDF character encoding repair: fontObject={fontObject} sourceCode={sourceCode} replacement={replacement} action={action} source={source} confidence={confidence} reason={reason}",
                key.FontObjectId,
                key.SourceCode,
                replacement,
                action,
                chosen.Source,
                chosen.Confidence,
                chosen.Reason);
        }

        return applied;
    }

    private static bool HasAcceptedRepair(IEnumerable<RepairCandidate> candidates, RepairKey key)
        => candidates.Any(c => c.Key.Equals(key) && IsSafeReplacement(c.Replacement));

    private static PdfCharacterEncodingAnomalyGroup ToRepairRequestGroup(AnomalyGroup group)
        => new(
            group.Key.FontObjectId,
            group.Anomalies[0].FontName,
            group.Key.SourceCode,
            group.Anomalies[0].Kind,
            group.Anomalies
                .Take(MaxContextsPerGroup)
                .Select(a => new PdfCharacterEncodingAnomalyContext(
                    a.PageNumber,
                    a.Mcid,
                    a.LineWithPlaceholder,
                    a.ContextBefore,
                    a.ContextAfter))
                .ToArray());

    private static bool TryPatchToUnicodeStream(
        PdfStream toUnicodeStream,
        string sourceCode,
        string replacement,
        out string action)
    {
        action = string.Empty;
        var bytes = toUnicodeStream.GetBytes();
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        var cmap = Encoding.ASCII.GetString(bytes);
        var destinationHex = ToUtf16BeHex(replacement);
        var replaced = false;

        var lines = cmap.Replace("\r\n", "\n").Split('\n');
        var inBfChar = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("beginbfchar", StringComparison.Ordinal))
            {
                inBfChar = true;
                continue;
            }

            if (line.Contains("endbfchar", StringComparison.Ordinal))
            {
                inBfChar = false;
                continue;
            }

            if (!inBfChar)
            {
                continue;
            }

            var match = ExplicitBfCharRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var src = match.Groups["src"].Value.ToUpperInvariant();
            if (!string.Equals(src, sourceCode, StringComparison.Ordinal))
            {
                continue;
            }

            lines[i] = ExplicitBfCharRegex.Replace(line, $"<{sourceCode}> <{destinationHex}>", count: 1);
            replaced = true;
        }

        if (replaced)
        {
            cmap = string.Join('\n', lines);
            toUnicodeStream.SetData(Encoding.ASCII.GetBytes(cmap));
            action = "replace";
            return true;
        }

        var insertion = $"{Environment.NewLine}1 beginbfchar{Environment.NewLine}<{sourceCode}> <{destinationHex}>{Environment.NewLine}endbfchar{Environment.NewLine}";
        var endCmapIndex = cmap.LastIndexOf("endcmap", StringComparison.Ordinal);
        cmap = endCmapIndex >= 0
            ? cmap.Insert(endCmapIndex, insertion)
            : cmap + insertion;

        toUnicodeStream.SetData(Encoding.ASCII.GetBytes(cmap));
        action = "append";
        return true;
    }

    private static ToUnicodeInfo ReadToUnicodeInfo(PdfDictionary fontDict)
    {
        var toUnicodeObj = fontDict.Get(PdfName.ToUnicode);
        var toUnicodeStream = Dereference(toUnicodeObj) as PdfStream;
        if (toUnicodeStream is null)
        {
            return ToUnicodeInfo.Empty;
        }

        var bytes = toUnicodeStream.GetBytes();
        if (bytes is null || bytes.Length == 0)
        {
            return new ToUnicodeInfo(toUnicodeStream, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var cmap = Encoding.ASCII.GetString(bytes);
        var mappings = ParseExplicitBfCharMappings(cmap);
        return new ToUnicodeInfo(toUnicodeStream, mappings);
    }

    private static Dictionary<string, string> ParseExplicitBfCharMappings(string cmap)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var inBfChar = false;
        foreach (var line in cmap.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Contains("beginbfchar", StringComparison.Ordinal))
            {
                inBfChar = true;
                continue;
            }

            if (line.Contains("endbfchar", StringComparison.Ordinal))
            {
                inBfChar = false;
                continue;
            }

            if (!inBfChar)
            {
                continue;
            }

            var match = ExplicitBfCharRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var source = match.Groups["src"].Value.ToUpperInvariant();
            var destination = DecodeUtf16BeHex(match.Groups["dst"].Value);
            if (destination is not null)
            {
                mappings[source] = destination;
            }
        }

        return mappings;
    }

    private static PdfObject? Dereference(PdfObject? obj)
        => obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;

    private static string? DecodeUtf16BeHex(string hex)
    {
        hex = RemediationHelpers.NormalizeWhitespace(hex).ToUpperInvariant();
        if (hex.Length == 0 || hex.Length % 4 != 0 || !IsHex(hex))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromHexString(hex);
            return Encoding.BigEndianUnicode.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string ToUtf16BeHex(string text)
        => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    private static bool IsHex(string value)
        => value.All(ch => ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static bool IsSafeReplacement(string? replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement) || replacement.Length > 4)
        {
            return false;
        }

        foreach (var ch in replacement)
        {
            if (IsBadExtractedChar(ch) || char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBadExtractedChar(char ch)
        => ch == '\uFFFD' || IsInvalidControl(ch) || IsPrivateUse(ch);

    private static bool IsInvalidControl(char ch)
        => char.IsControl(ch) && ch is not '\t' and not '\r' and not '\n';

    private static bool IsPrivateUse(char ch)
        => ch is >= '\uE000' and <= '\uF8FF';

    private static bool IsSafeFiller(char ch)
        => ch is '-' or '_' or '.' or ':' or '/' or '\\' or '=' or '*';

    private static string NormalizeFontName(string fontName)
    {
        fontName = RemediationHelpers.NormalizeWhitespace(fontName);
        var plus = fontName.IndexOf('+');
        if (plus == 6 && fontName[..plus].All(ch => ch is >= 'A' and <= 'Z'))
        {
            fontName = fontName[(plus + 1)..];
        }

        return fontName.ToUpperInvariant();
    }

    private sealed class CharacterEncodingListener : IEventListener
    {
        private readonly Dictionary<string, FontInfo> _fontsByObjectId;
        private readonly List<TextEventCapture> _events = new();
        private readonly StringBuilder _pageText = new();
        private readonly int _pageNumber;

        public CharacterEncodingListener(int pageNumber, Dictionary<string, FontInfo> fontsByObjectId)
        {
            _pageNumber = pageNumber;
            _fontsByObjectId = fontsByObjectId;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo textRenderInfo)
            {
                return;
            }

            var eventText = textRenderInfo.GetActualText() ?? textRenderInfo.GetText() ?? string.Empty;
            if (string.IsNullOrEmpty(eventText))
            {
                return;
            }

            if (_pageText.Length > 0 && !char.IsWhiteSpace(_pageText[^1]) && !char.IsWhiteSpace(eventText[0]))
            {
                _pageText.Append(' ');
            }

            var eventStart = _pageText.Length;
            _pageText.Append(eventText);
            var eventEnd = _pageText.Length;

            var chars = new List<CharCapture>();
            var runningOffset = 0;
            foreach (var charInfo in textRenderInfo.GetCharacterRenderInfos())
            {
                var charText = charInfo.GetActualText() ?? charInfo.GetText() ?? string.Empty;
                var sourceCode = ToSourceCodeHex(charInfo.GetPdfString());
                var fontInfo = GetFontInfo(charInfo.GetFont());
                var charStart = eventStart + Math.Min(runningOffset, eventText.Length);
                runningOffset += Math.Max(1, charText.Length);
                var charEnd = eventStart + Math.Min(runningOffset, eventText.Length);

                chars.Add(new CharCapture(charText, sourceCode, fontInfo, charStart, Math.Max(charStart, charEnd)));
            }

            _events.Add(
                new TextEventCapture(
                    _pageNumber,
                    textRenderInfo.GetMcid() >= 0 ? textRenderInfo.GetMcid() : null,
                    eventText,
                    eventStart,
                    eventEnd,
                    chars));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public IReadOnlyList<DetectedAnomaly> GetAnomalies()
        {
            var pageText = _pageText.ToString();
            var anomalies = new List<DetectedAnomaly>();

            foreach (var textEvent in _events)
            {
                foreach (var ch in textEvent.Chars)
                {
                    var kind = GetAnomalyKind(ch);
                    if (kind is null)
                    {
                        continue;
                    }

                    var (before, after) = GetContext(pageText, ch.StartIndex, ch.EndIndex);
                    anomalies.Add(
                        new DetectedAnomaly(
                            new RepairKey(ch.Font.FontObjectId, ch.SourceCode),
                            ch.Font.FontName,
                            kind,
                            textEvent.PageNumber,
                            textEvent.Mcid,
                            textEvent.EventText,
                            BuildLineWithPlaceholder(textEvent.EventText, ch.StartIndex - textEvent.EventStart, ch.EndIndex - textEvent.EventStart),
                            before,
                            after));
                }
            }

            return anomalies;
        }

        private string? GetAnomalyKind(CharCapture ch)
        {
            if (string.IsNullOrWhiteSpace(ch.SourceCode))
            {
                return null;
            }

            if (ch.Text.Any(c => c == '\uFFFD'))
            {
                return "replacement_character";
            }

            if (ch.Text.Any(IsInvalidControl))
            {
                return "invalid_control";
            }

            if (ch.Text.Any(IsPrivateUse))
            {
                return "private_use";
            }

            if (ch.Font.ToUnicode.Stream is not null && !ch.Font.ToUnicode.ExplicitMappings.ContainsKey(ch.SourceCode))
            {
                return "missing_tounicode";
            }

            return null;
        }

        private FontInfo GetFontInfo(PdfFont font)
        {
            var fontDict = font.GetPdfObject();
            var fontRef = fontDict.GetIndirectReference();
            var fontObjectId = fontRef is null
                ? $"direct:{fontDict.GetHashCode():X}"
                : $"{fontRef.GetObjNumber()} {fontRef.GetGenNumber()} R";

            if (_fontsByObjectId.TryGetValue(fontObjectId, out var existing))
            {
                return existing;
            }

            var fontName = fontDict.GetAsName(PdfName.BaseFont)?.GetValue() ?? "(unknown)";
            var info = new FontInfo(fontObjectId, fontName, fontDict, ReadToUnicodeInfo(fontDict));
            _fontsByObjectId[fontObjectId] = info;
            return info;
        }

        private static string ToSourceCodeHex(PdfString pdfString)
            => Convert.ToHexString(pdfString.GetValueBytes());

        private static (string Before, string After) GetContext(string pageText, int start, int end)
        {
            start = Math.Clamp(start, 0, pageText.Length);
            end = Math.Clamp(end, start, pageText.Length);

            var beforeStart = Math.Max(0, start - ContextCharsPerSide);
            var afterEnd = Math.Min(pageText.Length, end + ContextCharsPerSide);

            return (
                RemediationHelpers.NormalizeWhitespace(pageText[beforeStart..start]),
                RemediationHelpers.NormalizeWhitespace(pageText[end..afterEnd]));
        }

        private static string BuildLineWithPlaceholder(string eventText, int start, int end)
        {
            start = Math.Clamp(start, 0, eventText.Length);
            end = Math.Clamp(end, start, eventText.Length);
            return RemediationHelpers.NormalizeWhitespace(eventText[..start] + Placeholder + eventText[end..]);
        }
    }

    private sealed record ScanResult(
        IReadOnlyList<DetectedAnomaly> Anomalies,
        Dictionary<string, FontInfo> FontsByObjectId);

    private sealed record FontInfo(
        string FontObjectId,
        string FontName,
        PdfDictionary FontDictionary,
        ToUnicodeInfo ToUnicode);

    private sealed record ToUnicodeInfo(
        PdfStream? Stream,
        Dictionary<string, string> ExplicitMappings)
    {
        public static ToUnicodeInfo Empty { get; } =
            new(null, new Dictionary<string, string>(StringComparer.Ordinal));

        public string? Lookup(string sourceCode)
            => ExplicitMappings.TryGetValue(sourceCode, out var value) ? value : null;
    }

    private sealed record TextEventCapture(
        int PageNumber,
        int? Mcid,
        string EventText,
        int EventStart,
        int EventEnd,
        IReadOnlyList<CharCapture> Chars);

    private sealed record CharCapture(
        string Text,
        string SourceCode,
        FontInfo Font,
        int StartIndex,
        int EndIndex);

    private sealed record DetectedAnomaly(
        RepairKey Key,
        string FontName,
        string Kind,
        int PageNumber,
        int? Mcid,
        string EventText,
        string LineWithPlaceholder,
        string ContextBefore,
        string ContextAfter);

    private sealed record AnomalyGroup(RepairKey Key, IReadOnlyList<DetectedAnomaly> Anomalies);

    private sealed record RepairKey(string FontObjectId, string SourceCode);

    private sealed record RepairCandidate(
        RepairKey Key,
        string Replacement,
        double Confidence,
        string Source,
        string Reason);
}

internal sealed record PdfCharacterEncodingRemediationResult(
    int DetectedAnomalies,
    int Groups,
    int DeterministicRepairsApplied,
    int AiRepairsApplied);
