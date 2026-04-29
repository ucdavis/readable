using System.Globalization;
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
    private const int MaxAiActualTextIssues = 40;
    private const int MaxContextsPerGroup = 6;
    private const int ContextCharsPerSide = 120;
    private const int MaxBfRangeEntries = 4096;
    private static readonly Regex BfCharBlockRegex =
        new(@"\b\d+\s+beginbfchar(?<body>.*?)endbfchar", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex BfRangeBlockRegex =
        new(@"\b\d+\s+beginbfrange(?<body>.*?)endbfrange", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ExplicitBfCharRegex =
        new(@"<(?<src>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]*)>", RegexOptions.Compiled);
    private static readonly Regex ExplicitBfRangeRegex =
        new(@"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*<(?<dst>[0-9A-Fa-f]*)>", RegexOptions.Compiled);
    private static readonly Regex ExplicitBfRangeArrayRegex =
        new(@"<(?<start>[0-9A-Fa-f]+)>\s*<(?<end>[0-9A-Fa-f]+)>\s*\[(?<dsts>(?:\s*<[0-9A-Fa-f]*>\s*)+)\]", RegexOptions.Compiled);
    private static readonly Regex ExplicitBfRangeDestinationRegex =
        new(@"<(?<dst>[0-9A-Fa-f]*)>", RegexOptions.Compiled);
    private static readonly Regex TimestampPlaceholderRegex =
        new(@"\b\d<\?>\d{2}\s*(?:AM|PM)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkedContentDictionaryRegex =
        new(@"/(?<role>[A-Za-z0-9]+)\s*<<(?<dict>(?:(?:<[^>]*>)|[^<>])*?/MCID\s+(?<mcid>\d+)(?:(?:<[^>]*>)|[^<>])*?)>>\s*BDC", RegexOptions.Compiled);
    private static readonly Regex MarkedContentBlockRegex =
        new(@"/(?<role>[A-Za-z0-9]+)\s*<<(?<dict>(?:(?:<[^>]*>)|[^<>])*?/MCID\s+(?<mcid>\d+)(?:(?:<[^>]*>)|[^<>])*?)>>\s*BDC(?<body>.*?)EMC", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TextFontOperatorRegex =
        new(@"/(?<font>[A-Za-z0-9_.-]+)\s+[-+]?(?:\d+|\d*\.\d+)\s+Tf", RegexOptions.Compiled);
    private static readonly Regex HexTextShowRegex =
        new(@"<(?<hex>[0-9A-Fa-f]+)>\s*Tj", RegexOptions.Compiled);

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
            var earlyActualTextApplied = options.UseAiCharacterEncodingRepair
                ? await ApplyMarkedContentActualTextRepairsAsync(
                    pdf,
                    repairService,
                    options,
                    primaryLanguage,
                    logger,
                    cancellationToken)
                : 0;
            var earlyFontEncodingsApplied = ApplyObservedSimpleFontEncodings(scan.FontsByObjectId, logger);
            var earlyCidToGidMapsApplied = ApplyIdentityCidToGidMaps(scan.FontsByObjectId, logger);
            logger.LogInformation(
                "PDF character encoding remediation: no rendered text anomalies detected; actualTextApplied={actualTextApplied} fontEncodingsApplied={fontEncodingsApplied} cidToGidMapsApplied={cidToGidMapsApplied}.",
                earlyActualTextApplied,
                earlyFontEncodingsApplied,
                earlyCidToGidMapsApplied);
            return new PdfCharacterEncodingRemediationResult(
                0,
                0,
                0,
                0,
                ActualTextRepairsApplied: earlyActualTextApplied,
                FontEncodingRepairsApplied: earlyFontEncodingsApplied,
                CidToGidMapRepairsApplied: earlyCidToGidMapsApplied);
        }

        var groups = GroupAnomalies(scan.Anomalies);
        var repairableGroups = groups.Where(HasRepairableTextAnomaly).ToArray();
        var coverageGroups = groups.Where(IsMissingToUnicodeCoverageGroup).ToArray();
        var proposed = new List<RepairCandidate>();
        proposed.AddRange(BuildObservedTextCoverageRepairs(coverageGroups));
        proposed.AddRange(BuildSymbolPrivateUseRepairs(repairableGroups));
        proposed.AddRange(BuildNullPaddingRepairs(repairableGroups));
        proposed.AddRange(BuildKnownControlCodeRepairs(repairableGroups));
        proposed.AddRange(BuildExistingMappingRepairs(repairableGroups, scan.FontsByObjectId));
        proposed.AddRange(BuildTimestampRepairs(repairableGroups));
        proposed.AddRange(BuildFillerRepairs(repairableGroups));

        var deterministicApplied = ApplyValidatedRepairs(pdf, groups, scan.FontsByObjectId, proposed, logger);

        var aiApplied = 0;
        if (options.UseAiCharacterEncodingRepair)
        {
            var afterDeterministic = deterministicApplied > 0 ? Scan(pdf, cancellationToken) : scan;
            var remainingGroups = GroupAnomalies(afterDeterministic.Anomalies)
                .Where(HasRepairableTextAnomaly)
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
        if (verification.Anomalies.Count > 0 && deterministicApplied + aiApplied > 0)
        {
            var secondPassGroups = GroupAnomalies(verification.Anomalies);
            var secondPassRepairableGroups = secondPassGroups.Where(HasRepairableTextAnomaly).ToArray();
            var secondPassCoverageGroups = secondPassGroups.Where(IsMissingToUnicodeCoverageGroup).ToArray();
            var secondPassProposed = new List<RepairCandidate>();
            secondPassProposed.AddRange(BuildObservedTextCoverageRepairs(secondPassCoverageGroups));
            secondPassProposed.AddRange(BuildSymbolPrivateUseRepairs(secondPassRepairableGroups));
            secondPassProposed.AddRange(BuildNullPaddingRepairs(secondPassRepairableGroups));
            secondPassProposed.AddRange(BuildKnownControlCodeRepairs(secondPassRepairableGroups));
            secondPassProposed.AddRange(BuildExistingMappingRepairs(secondPassRepairableGroups, verification.FontsByObjectId));
            secondPassProposed.AddRange(BuildTimestampRepairs(secondPassRepairableGroups));
            secondPassProposed.AddRange(BuildFillerRepairs(secondPassRepairableGroups));

            var secondPassApplied = ApplyValidatedRepairs(
                pdf,
                secondPassGroups,
                verification.FontsByObjectId,
                secondPassProposed,
                logger);

            if (secondPassApplied > 0)
            {
                deterministicApplied += secondPassApplied;
                verification = Scan(pdf, cancellationToken);
            }
        }

        var actualTextApplied = options.UseAiCharacterEncodingRepair
            ? await ApplyMarkedContentActualTextRepairsAsync(
                pdf,
                repairService,
                options,
                primaryLanguage,
                logger,
                cancellationToken)
            : 0;
        var fontEncodingsApplied = ApplyObservedSimpleFontEncodings(verification.FontsByObjectId, logger);
        var cidToGidMapsApplied = ApplyIdentityCidToGidMaps(verification.FontsByObjectId, logger);
        if (actualTextApplied + fontEncodingsApplied + cidToGidMapsApplied > 0)
        {
            verification = Scan(pdf, cancellationToken);
        }

        logger.LogInformation(
            "PDF character encoding remediation summary: detected={detected} groups={groups} repairableGroups={repairableGroups} deterministicApplied={deterministicApplied} aiApplied={aiApplied} actualTextApplied={actualTextApplied} fontEncodingsApplied={fontEncodingsApplied} cidToGidMapsApplied={cidToGidMapsApplied} remaining={remaining}",
            scan.Anomalies.Count,
            groups.Count,
            repairableGroups.Length,
            deterministicApplied,
            aiApplied,
            actualTextApplied,
            fontEncodingsApplied,
            cidToGidMapsApplied,
            verification.Anomalies.Count);

        var remainingIssueGroups = BuildRemainingIssueGroups(verification.Anomalies);
        logger.LogInformation(
            "PDF character encoding remaining issue groups: count={count} visible={visible} coverage={coverage}",
            remainingIssueGroups.Count,
            remainingIssueGroups.Count(g => g.Category == "visible_text_anomaly"),
            remainingIssueGroups.Count(g => g.Category == "missing_tounicode_coverage"));

        foreach (var group in remainingIssueGroups.Take(20))
        {
            logger.LogInformation(
                "Remaining PDF character encoding issue group: category={category} fontObject={fontObject} font={font} pages={pages} mcids={mcids} sourceCodeCount={sourceCodeCount} sourceCodes={sourceCodes} occurrences={occurrences} visibleOccurrences={visibleOccurrences} sample={sample}",
                group.Category,
                group.FontObjectId,
                group.FontName,
                group.Pages,
                group.Mcids,
                group.SourceCodeCount,
                group.SourceCodes,
                group.Occurrences,
                group.VisibleOccurrences,
                group.Sample);
        }

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
            aiApplied,
            actualTextApplied,
            fontEncodingsApplied,
            cidToGidMapsApplied);
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

    private static bool HasRepairableTextAnomaly(AnomalyGroup group)
        => group.Anomalies.Any(a => a.Kind is not "missing_tounicode");

    private static bool IsMissingToUnicodeCoverageGroup(AnomalyGroup group)
        => group.Anomalies.All(a => a.Kind is "missing_tounicode");

    private static IReadOnlyList<RemainingIssueGroup> BuildRemainingIssueGroups(IReadOnlyList<DetectedAnomaly> anomalies)
        => anomalies
            .GroupBy(a => new
            {
                a.Key.FontObjectId,
                a.FontName,
                Category = a.Kind == "missing_tounicode" ? "missing_tounicode_coverage" : "visible_text_anomaly",
            })
            .Select(g =>
            {
                var sourceCodes = g
                    .Select(a => a.Key.SourceCode)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToArray();

                return new RemainingIssueGroup(
                    g.Key.Category,
                    g.Key.FontObjectId,
                    g.Key.FontName,
                    FormatRanges(g.Select(a => a.PageNumber)),
                    FormatRanges(g.Select(a => a.Mcid).Where(m => m is not null).Select(m => m!.Value)),
                    sourceCodes.Length,
                    JoinSample(sourceCodes, 40),
                    g.Count(),
                    g.Count(a => a.Kind is not "missing_tounicode"),
                    g.Select(a => a.LineWithPlaceholder).FirstOrDefault() ?? string.Empty);
            })
            .OrderByDescending(g => g.VisibleOccurrences > 0)
            .ThenByDescending(g => g.Occurrences)
            .ToArray();

    private static string JoinSample(IReadOnlyList<string> values, int maxItems)
    {
        if (values.Count == 0)
        {
            return "(none)";
        }

        var sample = string.Join(",", values.Take(maxItems));
        return values.Count <= maxItems ? sample : $"{sample},...";
    }

    private static string FormatRanges(IEnumerable<int> values)
    {
        var sorted = values.Distinct().OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return "(none)";
        }

        var parts = new List<string>();
        var start = sorted[0];
        var previous = sorted[0];
        foreach (var value in sorted.Skip(1))
        {
            if (value == previous + 1)
            {
                previous = value;
                continue;
            }

            parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = value;
            previous = value;
        }

        parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(",", parts);
    }

    private static IEnumerable<RepairCandidate> BuildNullPaddingRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            if (!group.Key.SourceCode.All(ch => ch == '0'))
            {
                continue;
            }

            if (group.Anomalies.Any(a => a.Kind is not "replacement_character"))
            {
                continue;
            }

            yield return new RepairCandidate(
                group.Key,
                " ",
                Confidence: 1,
                Source: "null_padding",
                Reason: "Used all-zero source code decodes to replacement characters; mapping to a space avoids invalid Unicode output.");
        }
    }

    private static IEnumerable<RepairCandidate> BuildObservedTextCoverageRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            var replacements = group.Anomalies
                .Select(a => NormalizeCoverageReplacement(a.ExtractedText))
                .Where(r => r is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (replacements.Length != 1)
            {
                continue;
            }

            yield return new RepairCandidate(
                group.Key,
                replacements[0]!,
                Confidence: 1,
                Source: "observed_text_coverage",
                Reason: "Source code is missing from /ToUnicode, but all observed rendered text resolves to the same safe Unicode value.");
        }
    }

    private static IEnumerable<RepairCandidate> BuildSymbolPrivateUseRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            if (group.Anomalies.Any(a => a.Kind is not "private_use"))
            {
                continue;
            }

            var fontName = group.Anomalies[0].FontName;
            if (!fontName.Contains("Wingdings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extractedValues = group.Anomalies
                .Select(a => a.ExtractedText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (extractedValues.Length != 1)
            {
                continue;
            }

            var replacement = extractedValues[0] switch
            {
                "\uF076" => "\u2756",
                _ => null,
            };

            if (replacement is null)
            {
                continue;
            }

            yield return new RepairCandidate(
                group.Key,
                replacement,
                Confidence: 1,
                Source: "symbol_private_use",
                Reason: "Known Wingdings private-use character maps to a printable Unicode symbol.");
        }
    }

    private static IEnumerable<RepairCandidate> BuildKnownControlCodeRepairs(IReadOnlyList<AnomalyGroup> groups)
    {
        foreach (var group in groups)
        {
            if (group.Anomalies.Any(a => a.Kind is not "replacement_character" and not "invalid_control"))
            {
                continue;
            }

            if (string.Equals(group.Key.SourceCode, "19", StringComparison.OrdinalIgnoreCase))
            {
                yield return new RepairCandidate(
                    group.Key,
                    ":",
                    Confidence: 1,
                    Source: "known_control_code",
                    Reason: "Used source code 0x19 appears as an invalid timestamp separator; mapping to colon.");
            }
            else if (string.Equals(group.Key.SourceCode, "0A", StringComparison.OrdinalIgnoreCase))
            {
                yield return new RepairCandidate(
                    group.Key,
                    " ",
                    Confidence: 1,
                    Source: "known_control_code",
                    Reason: "Used source code 0x0A decodes to replacement characters; mapping to space avoids invalid Unicode output.");
            }
        }
    }

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
            if (group.Anomalies.Any(IsTimestampSeparatorAnomaly))
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

    private static bool IsTimestampSeparatorAnomaly(DetectedAnomaly anomaly)
    {
        if (TimestampPlaceholderRegex.IsMatch(anomaly.LineWithPlaceholder))
        {
            return true;
        }

        var combined = RemediationHelpers.NormalizeWhitespace(
            KeepLastChars(anomaly.ContextBefore, 24) + Placeholder + KeepFirstChars(anomaly.ContextAfter, 24));
        return TimestampPlaceholderRegex.IsMatch(combined);
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
            var candidateItems = candidateGroup
                .Where(c => c.Confidence >= minimumConfidence)
                .Select(c => PreserveReplacementWhitespace(c.Source) ? c : c with { Replacement = c.Replacement.Trim() })
                .Where(IsSafeRepairCandidate)
                .ToArray();

            if (candidateItems.Length == 0)
            {
                continue;
            }

            var bestPriority = candidateItems.Max(c => GetRepairPriority(c.Source));
            var alternatives = candidateItems
                .Where(c => GetRepairPriority(c.Source) == bestPriority)
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
            var chosen = alternatives[0].OrderByDescending(c => c.Confidence).First();
            var existing = font.ToUnicode.Lookup(key.SourceCode);
            var occurrences = groupsByKey[key].Anomalies
                .Select(a => $"p{a.PageNumber}/mcid{(a.Mcid is null ? "-" : a.Mcid.Value.ToString(CultureInfo.InvariantCulture))}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (GetRepairPriority(chosen.Source) < GetRepairPriority("known_control_code")
                && IsSafeReplacement(existing)
                && !string.Equals(existing, replacement, StringComparison.Ordinal))
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

            logger.LogInformation(
                "Applied PDF character encoding repair: occurrences={occurrences} fontObject={fontObject} font={font} sourceCode={sourceCode} previous={previous} replacement={replacement} action={action} source={source} confidence={confidence} reason={reason}",
                string.Join(",", occurrences),
                key.FontObjectId,
                font.FontName,
                key.SourceCode,
                existing ?? "(none)",
                replacement,
                action,
                chosen.Source,
                chosen.Confidence,
                chosen.Reason);
        }

        return applied;
    }

    private static int GetRepairPriority(string source) => source switch
    {
        "null_padding" => 100,
        "symbol_private_use" => 95,
        "known_control_code" => 90,
        "observed_text_coverage" => 85,
        "timestamp_pattern" => 80,
        "filler_pattern" => 70,
        "existing_mapping" => 60,
        "ai" => 50,
        _ => 0,
    };

    private static int ApplyObservedSimpleFontEncodings(
        IReadOnlyDictionary<string, FontInfo> fontsByObjectId,
        ILogger logger)
    {
        var applied = 0;
        foreach (var font in fontsByObjectId.Values)
        {
            if (!PdfName.TrueType.Equals(font.FontDictionary.GetAsName(PdfName.Subtype))
                || font.FontDictionary.Get(PdfName.Encoding) is not null)
            {
                continue;
            }

            var firstChar = font.FontDictionary.GetAsNumber(PdfName.FirstChar)?.IntValue();
            var lastChar = font.FontDictionary.GetAsNumber(PdfName.LastChar)?.IntValue();
            if (firstChar is null || lastChar is null || firstChar > lastChar)
            {
                continue;
            }

            var differences = BuildSimpleFontDifferences(font.ToUnicode.ExplicitMappings, firstChar.Value, lastChar.Value);
            if (differences is null || differences.Size() == 0)
            {
                continue;
            }

            var encoding = new PdfDictionary();
            encoding.Put(PdfName.Type, PdfName.Encoding);
            encoding.Put(new PdfName("BaseEncoding"), PdfName.WinAnsiEncoding);
            encoding.Put(new PdfName("Differences"), differences);
            font.FontDictionary.Put(PdfName.Encoding, encoding);
            applied++;

            logger.LogInformation(
                "Applied PDF character encoding font dictionary: fontObject={fontObject} font={font} entries={entries}",
                font.FontObjectId,
                font.FontName,
                CountDifferenceNames(differences));
        }

        return applied;
    }

    private static int ApplyIdentityCidToGidMaps(
        IReadOnlyDictionary<string, FontInfo> fontsByObjectId,
        ILogger logger)
    {
        var applied = 0;
        var cidToGidMapName = new PdfName("CIDToGIDMap");

        foreach (var font in fontsByObjectId.Values)
        {
            if (!PdfName.Type0.Equals(font.FontDictionary.GetAsName(PdfName.Subtype)))
            {
                continue;
            }

            var descendants = font.FontDictionary.GetAsArray(PdfName.DescendantFonts);
            if (descendants is null)
            {
                continue;
            }

            for (var i = 0; i < descendants.Size(); i++)
            {
                if (Dereference(descendants.Get(i)) is not PdfDictionary descendant
                    || !new PdfName("CIDFontType2").Equals(descendant.GetAsName(PdfName.Subtype))
                    || descendant.Get(cidToGidMapName) is not null)
                {
                    continue;
                }

                descendant.Put(cidToGidMapName, PdfName.Identity);
                applied++;

                var descendantRef = descendant.GetIndirectReference();
                var descendantId = descendantRef is null
                    ? $"direct:{descendant.GetHashCode():X}"
                    : $"{descendantRef.GetObjNumber()} {descendantRef.GetGenNumber()} R";

                logger.LogInformation(
                    "Applied PDF character encoding CIDToGIDMap repair: fontObject={fontObject} descendantFont={descendantFont} font={font}",
                    font.FontObjectId,
                    descendantId,
                    font.FontName);
            }
        }

        return applied;
    }

    private static async Task<int> ApplyMarkedContentActualTextRepairsAsync(
        PdfDocument pdf,
        IPdfCharacterEncodingRepairService repairService,
        PdfRemediationOptions options,
        string? primaryLanguage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var requestIssues = new List<PdfCharacterEncodingActualTextIssue>();

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listener = new ActualTextIssueListener(pageNumber);
            var page = pdf.GetPage(pageNumber);
            new PdfCanvasProcessor(listener).ProcessPageContent(page);
            var pageIssues = listener.GetIssues()
                .Concat(CollectActualTextIssuesFromContentStream(page, pageNumber))
                .GroupBy(i => new ActualTextRepairKey(i.PageNumber, i.Mcid))
                .Select(g => g.First())
                .ToArray();

            if (pageIssues.Length == 0)
            {
                continue;
            }

            requestIssues.AddRange(pageIssues.Take(Math.Max(0, MaxAiActualTextIssues - requestIssues.Count)));
            if (requestIssues.Count >= MaxAiActualTextIssues)
            {
                break;
            }
        }

        if (requestIssues.Count == 0)
        {
            return 0;
        }

        PdfCharacterEncodingActualTextRepairResponse response;
        try
        {
            response = await repairService.ProposeActualTextRepairsAsync(
                new PdfCharacterEncodingActualTextRepairRequest(requestIssues, primaryLanguage),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PDF character encoding ActualText AI repair proposal failed.");
            return 0;
        }

        var threshold = Math.Clamp(options.CharacterEncodingRepairConfidenceThreshold, 0, 1);
        var proposals = ValidateActualTextRepairProposals(response.Repairs, requestIssues, threshold, logger);
        var missingProposalIssues = GetIssuesWithoutAcceptedActualTextRepair(requestIssues, proposals);
        if (missingProposalIssues.Count > 0)
        {
            try
            {
                response = await repairService.ProposeActualTextRepairsAsync(
                    new PdfCharacterEncodingActualTextRepairRequest(missingProposalIssues, primaryLanguage),
                    cancellationToken);
                var retryProposals = ValidateActualTextRepairProposals(response.Repairs, missingProposalIssues, threshold, logger);
                if (retryProposals.Count > 0)
                {
                    proposals = proposals.Concat(retryProposals).ToArray();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PDF character encoding ActualText AI repair retry failed.");
            }
        }

        var fallbackIssues = GetIssuesWithoutAcceptedActualTextRepair(requestIssues, proposals);
        if (fallbackIssues.Count > 0)
        {
            var fallbackRepairs = BuildSanitizedActualTextFallbacks(fallbackIssues);
            if (fallbackRepairs.Count > 0)
            {
                proposals = proposals.Concat(fallbackRepairs).ToArray();
            }
        }

        if (proposals.Count == 0)
        {
            return 0;
        }

        var applied = 0;
        foreach (var pageGroup in proposals.GroupBy(p => p.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pdf.GetPage(pageGroup.Key);
            var content = Encoding.Latin1.GetString(page.GetContentBytes());
            var changed = false;

            foreach (var repair in pageGroup)
            {
                var before = content;
                content = AddOrReplaceActualTextForMcid(content, repair.Mcid, repair.ActualText);
                if (!string.Equals(before, content, StringComparison.Ordinal))
                {
                    applied++;
                    changed = true;
                    logger.LogInformation(
                        "Applied PDF character encoding ActualText repair: page={page} mcid={mcid} actualText={actualText} confidence={confidence} reason={reason}",
                        pageGroup.Key,
                        repair.Mcid,
                        repair.ActualText,
                        repair.Confidence,
                        repair.Reason);
                }
            }

            if (changed)
            {
                var stream = new PdfStream(Encoding.Latin1.GetBytes(content));
                stream.MakeIndirect(pdf);
                page.GetPdfObject().Put(PdfName.Contents, stream);
            }
        }

        return applied;
    }

    private static IReadOnlyList<ActualTextRepair> BuildSanitizedActualTextFallbacks(
        IReadOnlyList<PdfCharacterEncodingActualTextIssue> issues)
        => issues
            .Select(i => new
            {
                Issue = i,
                ActualText = SanitizeActualText(i.RawText),
            })
            .Where(i => i.ActualText is not null)
            .Select(i => new ActualTextRepair(
                i.Issue.PageNumber,
                i.Issue.Mcid,
                i.ActualText!,
                1,
                "No AI proposal was accepted; removed invalid Unicode from the marked-content text to provide valid /ActualText."))
            .ToArray();

    private static string? SanitizeActualText(string rawText)
    {
        var sanitized = RemediationHelpers.NormalizeWhitespace(
            new string(rawText
                .Where(ch => !IsBadExtractedChar(ch))
                .ToArray()));
        if (string.IsNullOrWhiteSpace(sanitized)
            || sanitized.Count(char.IsLetterOrDigit) < 3
            || !IsSafeActualTextReplacement(sanitized)
            || !IsPlausibleActualTextLength(rawText, sanitized))
        {
            return null;
        }

        return sanitized;
    }

    private static IReadOnlyList<PdfCharacterEncodingActualTextIssue> GetIssuesWithoutAcceptedActualTextRepair(
        IReadOnlyList<PdfCharacterEncodingActualTextIssue> issues,
        IReadOnlyList<ActualTextRepair> acceptedRepairs)
    {
        var accepted = acceptedRepairs
            .Select(r => new ActualTextRepairKey(r.PageNumber, r.Mcid))
            .ToHashSet();

        return issues
            .Where(i => !accepted.Contains(new ActualTextRepairKey(i.PageNumber, i.Mcid)))
            .ToArray();
    }

    private static IReadOnlyList<PdfCharacterEncodingActualTextIssue> CollectActualTextIssuesFromContentStream(
        PdfPage page,
        int pageNumber)
    {
        var content = Encoding.Latin1.GetString(page.GetContentBytes());
        var pageFonts = ReadPageFontToUnicodeMaps(page);
        if (pageFonts.Count == 0)
        {
            return Array.Empty<PdfCharacterEncodingActualTextIssue>();
        }

        var issues = new List<PdfCharacterEncodingActualTextIssue>();
        foreach (Match block in MarkedContentBlockRegex.Matches(content))
        {
            if (!int.TryParse(block.Groups["mcid"].Value, out var mcid)
                || block.Groups["dict"].Value.Contains("/ActualText", StringComparison.Ordinal))
            {
                continue;
            }

            var body = block.Groups["body"].Value;
            var fontMatch = TextFontOperatorRegex.Match(body);
            if (!fontMatch.Success || !pageFonts.TryGetValue(fontMatch.Groups["font"].Value, out var toUnicode))
            {
                continue;
            }

            var decoded = DecodeMarkedContentHexText(body, toUnicode);
            decoded = RemediationHelpers.NormalizeWhitespace(decoded);
            if (string.IsNullOrWhiteSpace(decoded) || !ContainsBadActualText(decoded))
            {
                continue;
            }

            var beforeStart = Math.Max(0, block.Index - ContextCharsPerSide);
            var afterEnd = Math.Min(content.Length, block.Index + block.Length + ContextCharsPerSide);
            issues.Add(new PdfCharacterEncodingActualTextIssue(
                pageNumber,
                mcid,
                decoded,
                RemediationHelpers.NormalizeWhitespace(content[beforeStart..block.Index]),
                RemediationHelpers.NormalizeWhitespace(content[(block.Index + block.Length)..afterEnd])));
        }

        return issues;
    }

    private static IReadOnlyDictionary<string, ToUnicodeInfo> ReadPageFontToUnicodeMaps(PdfPage page)
    {
        var resources = page.GetPdfObject().GetAsDictionary(PdfName.Resources);
        var fonts = resources?.GetAsDictionary(PdfName.Font);
        if (fonts is null)
        {
            return new Dictionary<string, ToUnicodeInfo>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, ToUnicodeInfo>(StringComparer.Ordinal);
        foreach (var fontResourceName in fonts.KeySet())
        {
            if (Dereference(fonts.Get(fontResourceName)) is not PdfDictionary fontDict)
            {
                continue;
            }

            var toUnicode = ReadToUnicodeInfo(fontDict);
            if (toUnicode.Stream is not null && toUnicode.ExplicitMappings.Count > 0)
            {
                result[fontResourceName.GetValue()] = toUnicode;
            }
        }

        return result;
    }

    private static string DecodeMarkedContentHexText(string body, ToUnicodeInfo toUnicode)
    {
        var sb = new StringBuilder();
        foreach (Match textShow in HexTextShowRegex.Matches(body))
        {
            var hex = textShow.Groups["hex"].Value.ToUpperInvariant();
            sb.Append(DecodeHexText(hex, toUnicode));
        }

        return sb.ToString();
    }

    private static string DecodeHexText(string hex, ToUnicodeInfo toUnicode)
    {
        var codeLengths = toUnicode.ExplicitMappings.Keys
            .Select(k => k.Length)
            .Distinct()
            .OrderByDescending(length => length)
            .ToArray();
        if (codeLengths.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < hex.Length;)
        {
            var matched = false;
            foreach (var length in codeLengths)
            {
                if (i + length > hex.Length)
                {
                    continue;
                }

                var code = hex.Substring(i, length);
                if (!toUnicode.ExplicitMappings.TryGetValue(code, out var value))
                {
                    continue;
                }

                sb.Append(value);
                i += length;
                matched = true;
                break;
            }

            if (!matched)
            {
                i += 2;
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<ActualTextRepair> ValidateActualTextRepairProposals(
        IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal> proposals,
        IReadOnlyList<PdfCharacterEncodingActualTextIssue> requestedIssues,
        double minimumConfidence,
        ILogger logger)
    {
        var requested = requestedIssues
            .GroupBy(i => new ActualTextRepairKey(i.PageNumber, i.Mcid))
            .ToDictionary(g => g.Key, g => g.First());

        var accepted = new List<ActualTextRepair>();
        foreach (var group in proposals.GroupBy(p => new ActualTextRepairKey(p.PageNumber, p.Mcid)))
        {
            if (!requested.TryGetValue(group.Key, out var issue))
            {
                logger.LogInformation(
                    "Skipped PDF character encoding ActualText repair proposal for unrequested marked content: page={page} mcid={mcid}",
                    group.Key.PageNumber,
                    group.Key.Mcid);
                continue;
            }

            var normalized = group
                .Select(p => p with { ActualText = RemediationHelpers.NormalizeWhitespace(p.ActualText) })
                .Where(p => p.Confidence >= minimumConfidence
                    && IsSafeActualTextReplacement(p.ActualText)
                    && IsPlausibleActualTextLength(issue.RawText, p.ActualText))
                .ToArray();
            if (normalized.Length == 0)
            {
                logger.LogInformation(
                    "Skipped PDF character encoding ActualText repair proposal: page={page} mcid={mcid} reason=no valid proposal above threshold",
                    group.Key.PageNumber,
                    group.Key.Mcid);
                continue;
            }

            var replacements = normalized
                .Select(p => p.ActualText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (replacements.Length != 1)
            {
                logger.LogInformation(
                    "Skipped PDF character encoding ActualText repair proposal: page={page} mcid={mcid} reason=conflicting replacements",
                    group.Key.PageNumber,
                    group.Key.Mcid);
                continue;
            }

            var best = normalized.OrderByDescending(p => p.Confidence).First();
            accepted.Add(new ActualTextRepair(
                best.PageNumber,
                best.Mcid,
                best.ActualText,
                best.Confidence,
                best.Reason));
        }

        return accepted;
    }

    private static bool IsSafeActualTextReplacement(string actualText)
    {
        if (string.IsNullOrWhiteSpace(actualText) || actualText.Length > 80)
        {
            return false;
        }

        return actualText.All(ch => !IsBadExtractedChar(ch) && !IsInvalidControl(ch));
    }

    private static bool IsPlausibleActualTextLength(string rawText, string actualText)
    {
        var rawVisibleLength = rawText.Count(ch => !char.IsWhiteSpace(ch));
        var actualVisibleLength = actualText.Count(ch => !char.IsWhiteSpace(ch));
        if (rawVisibleLength == 0 || actualVisibleLength == 0)
        {
            return false;
        }

        if (rawText.Any(char.IsWhiteSpace))
        {
            return actualVisibleLength <= rawVisibleLength + 8;
        }

        return actualVisibleLength <= Math.Max(rawVisibleLength + 4, rawVisibleLength * 2);
    }

    private static bool ContainsBadActualText(string text)
        => text.Any(ch => ch == '\uFFFD' || IsPrivateUse(ch) || IsInvalidControl(ch));

    private static string AddOrReplaceActualTextForMcid(string content, int mcid, string actualText)
        => MarkedContentDictionaryRegex.Replace(
            content,
            match =>
            {
                if (!int.TryParse(match.Groups["mcid"].Value, out var matchedMcid)
                    || matchedMcid != mcid)
                {
                    return match.Value;
                }

                var role = match.Groups["role"].Value;
                var (dict, replaced) = ReplaceSimpleActualText(match.Groups["dict"].Value.TrimEnd(), actualText);
                if (!replaced)
                {
                    dict = $"{dict} /ActualText <{ToPdfTextStringHex(actualText)}>";
                }

                return $"/{role} <<{dict} >> BDC";
            });

    private static (string DictionaryContent, bool Replaced) ReplaceSimpleActualText(string dictionaryContent, string actualText)
    {
        var replacement = $"/ActualText <{ToPdfTextStringHex(actualText)}>";
        var hexActualText = Regex.Replace(
            dictionaryContent,
            @"/ActualText\s*<[^<>]*>",
            replacement,
            RegexOptions.CultureInvariant);
        if (!string.Equals(hexActualText, dictionaryContent, StringComparison.Ordinal))
        {
            return (hexActualText, true);
        }

        var literalActualText = Regex.Replace(
            dictionaryContent,
            @"/ActualText\s*\([^()]*\)",
            replacement,
            RegexOptions.CultureInvariant);
        return string.Equals(literalActualText, dictionaryContent, StringComparison.Ordinal)
            ? (dictionaryContent, false)
            : (literalActualText, true);
    }

    private static string ToPdfTextStringHex(string text)
        => "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    private sealed class ActualTextIssueListener : IEventListener
    {
        private readonly List<ActualTextEvent> _events = new();
        private readonly int _pageNumber;

        public ActualTextIssueListener(int pageNumber)
        {
            _pageNumber = pageNumber;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri || tri.GetMcid() < 0)
            {
                return;
            }

            var raw = tri.GetText() ?? string.Empty;
            var actual = tri.GetActualText() ?? string.Empty;
            _events.Add(new ActualTextEvent(tri.GetMcid(), raw, actual));
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

        public IReadOnlyList<PdfCharacterEncodingActualTextIssue> GetIssues()
        {
            var pageText = RemediationHelpers.NormalizeWhitespace(string.Join(" ", _events.Select(e => e.RawText)));
            var issues = new List<PdfCharacterEncodingActualTextIssue>();

            foreach (var mcidGroup in _events.GroupBy(e => e.Mcid))
            {
                var raw = RemediationHelpers.NormalizeWhitespace(string.Concat(mcidGroup.Select(e => e.RawText)));
                var actual = RemediationHelpers.NormalizeWhitespace(string.Concat(mcidGroup.Select(e => e.ActualText)));
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string issueText;
                if (ContainsBadActualText(raw)
                    && (string.IsNullOrWhiteSpace(actual) || ContainsBadActualText(actual)))
                {
                    issueText = raw;
                }
                else if (!string.IsNullOrWhiteSpace(actual) && ContainsBadActualText(actual))
                {
                    issueText = actual;
                }
                else
                {
                    continue;
                }

                var (contextBefore, contextAfter) = GetContextAroundFirst(pageText, raw);
                issues.Add(new PdfCharacterEncodingActualTextIssue(
                    _pageNumber,
                    mcidGroup.Key,
                    issueText,
                    contextBefore,
                    contextAfter));
            }

            return issues;
        }

        private static (string Before, string After) GetContextAroundFirst(string pageText, string raw)
        {
            var index = pageText.IndexOf(raw, StringComparison.Ordinal);
            if (index < 0)
            {
                return (KeepFirstChars(pageText, ContextCharsPerSide), KeepLastChars(pageText, ContextCharsPerSide));
            }

            return (
                KeepLastChars(pageText[..index], ContextCharsPerSide),
                KeepFirstChars(pageText[(index + raw.Length)..], ContextCharsPerSide));
        }
    }

    private sealed record ActualTextEvent(int Mcid, string RawText, string ActualText);

    private sealed record ActualTextRepairKey(int PageNumber, int Mcid);

    private sealed record ActualTextRepair(
        int PageNumber,
        int Mcid,
        string ActualText,
        double Confidence,
        string Reason);

    private static PdfArray? BuildSimpleFontDifferences(
        IReadOnlyDictionary<string, string> mappings,
        int firstChar,
        int lastChar)
    {
        var entries = mappings
            .Select(kvp => new
            {
                Code = TryParseSingleByteCode(kvp.Key),
                GlyphName = TryGetGlyphName(kvp.Value),
            })
            .Where(e => e.Code is not null
                && e.Code >= firstChar
                && e.Code <= lastChar
                && e.GlyphName is not null)
            .Select(e => (Code: e.Code!.Value, GlyphName: e.GlyphName!))
            .OrderBy(e => e.Code)
            .ToArray();

        if (entries.Length == 0)
        {
            return null;
        }

        var differences = new PdfArray();
        int? previous = null;
        foreach (var (code, glyphName) in entries)
        {
            if (previous is null || code != previous + 1)
            {
                differences.Add(new PdfNumber(code));
            }

            differences.Add(new PdfName(glyphName));
            previous = code;
        }

        return differences;
    }

    private static int CountDifferenceNames(PdfArray differences)
    {
        var count = 0;
        for (var i = 0; i < differences.Size(); i++)
        {
            if (differences.Get(i) is PdfName)
            {
                count++;
            }
        }

        return count;
    }

    private static int? TryParseSingleByteCode(string sourceCode)
    {
        if (sourceCode.Length != 2 || !IsHex(sourceCode))
        {
            return null;
        }

        return Convert.ToInt32(sourceCode, 16);
    }

    private static string? TryGetGlyphName(string replacement)
    {
        if (replacement.Length != 1)
        {
            return null;
        }

        var ch = replacement[0];
        if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            return ch.ToString();
        }

        return ch switch
        {
            ' ' => "space",
            '0' => "zero",
            '1' => "one",
            '2' => "two",
            '3' => "three",
            '4' => "four",
            '5' => "five",
            '6' => "six",
            '7' => "seven",
            '8' => "eight",
            '9' => "nine",
            '#' => "numbersign",
            '&' => "ampersand",
            '(' => "parenleft",
            ')' => "parenright",
            '*' => "asterisk",
            ',' => "comma",
            '-' => "hyphen",
            '.' => "period",
            '/' => "slash",
            ':' => "colon",
            ';' => "semicolon",
            '=' => "equal",
            '?' => "question",
            '[' => "bracketleft",
            ']' => "bracketright",
            '\u2013' => "endash",
            _ => null,
        };
    }

    private static bool HasAcceptedRepair(IEnumerable<RepairCandidate> candidates, RepairKey key)
        => candidates.Any(c => c.Key.Equals(key) && IsSafeRepairCandidate(c));

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

        cmap = BfCharBlockRegex.Replace(
            cmap,
            blockMatch =>
            {
                if (replaced)
                {
                    return blockMatch.Value;
                }

                var body = blockMatch.Groups["body"].Value;
                var bodyReplaced = false;
                var updatedBody = ExplicitBfCharRegex.Replace(
                    body,
                    mappingMatch =>
                    {
                        if (bodyReplaced)
                        {
                            return mappingMatch.Value;
                        }

                        var src = mappingMatch.Groups["src"].Value.ToUpperInvariant();
                        if (!string.Equals(src, sourceCode, StringComparison.Ordinal))
                        {
                            return mappingMatch.Value;
                        }

                        bodyReplaced = true;
                        return $"<{sourceCode}> <{destinationHex}>";
                    });

                if (!bodyReplaced)
                {
                    return blockMatch.Value;
                }

                replaced = true;
                var bodyStart = blockMatch.Groups["body"].Index - blockMatch.Index;
                var bodyEnd = bodyStart + body.Length;
                return blockMatch.Value[..bodyStart]
                    + updatedBody
                    + blockMatch.Value[bodyEnd..];
            });

        if (replaced)
        {
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
        foreach (Match block in BfCharBlockRegex.Matches(cmap))
        {
            AddBfCharMappings(block.Groups["body"].Value, mappings);
        }

        foreach (Match block in BfRangeBlockRegex.Matches(cmap))
        {
            AddBfRangeMappings(block.Groups["body"].Value, mappings);
        }

        return mappings;
    }

    private static void AddBfCharMappings(string block, Dictionary<string, string> mappings)
    {
        foreach (Match match in ExplicitBfCharRegex.Matches(block))
        {
            var source = match.Groups["src"].Value.ToUpperInvariant();
            var destination = DecodeUtf16BeHex(match.Groups["dst"].Value);
            if (destination is not null)
            {
                mappings[source] = destination;
            }

        }
    }

    private static void AddBfRangeMappings(string block, Dictionary<string, string> mappings)
    {
        foreach (Match arrayMatch in ExplicitBfRangeArrayRegex.Matches(block))
        {
            AddBfRangeArrayMappings(arrayMatch, mappings);
        }

        var blockWithoutArrays = ExplicitBfRangeArrayRegex.Replace(block, string.Empty);
        foreach (Match rangeMatch in ExplicitBfRangeRegex.Matches(blockWithoutArrays))
        {
            AddBfRangeIncrementalMappings(rangeMatch, mappings);
        }
    }

    private static void AddBfRangeArrayMappings(Match match, Dictionary<string, string> mappings)
    {
        if (!TryReadBfRange(match, out var start, out var end, out var sourceWidth, out var count))
        {
            return;
        }

        var destinations = ExplicitBfRangeDestinationRegex
            .Matches(match.Groups["dsts"].Value)
            .Select(m => m.Groups["dst"].Value)
            .ToArray();
        for (ulong offset = 0; offset < count && offset < (ulong)destinations.Length; offset++)
        {
            var destination = DecodeUtf16BeHex(destinations[(int)offset]);
            if (destination is null)
            {
                continue;
            }

            mappings[(start + offset).ToString($"X{sourceWidth}", CultureInfo.InvariantCulture)] = destination;
        }
    }

    private static void AddBfRangeIncrementalMappings(Match match, Dictionary<string, string> mappings)
    {
        if (!TryReadBfRange(match, out var start, out _, out var sourceWidth, out var count))
        {
            return;
        }

        var destinationStart = RemediationHelpers.NormalizeWhitespace(match.Groups["dst"].Value).ToUpperInvariant();
        for (ulong offset = 0; offset < count; offset++)
        {
            var destinationHex = IncrementHexString(destinationStart, offset);
            if (destinationHex is null)
            {
                continue;
            }

            var destination = DecodeUtf16BeHex(destinationHex);
            if (destination is null)
            {
                continue;
            }

            mappings[(start + offset).ToString($"X{sourceWidth}", CultureInfo.InvariantCulture)] = destination;
        }
    }

    private static bool TryReadBfRange(
        Match match,
        out ulong start,
        out ulong end,
        out int sourceWidth,
        out ulong count)
    {
        var startHex = match.Groups["start"].Value.ToUpperInvariant();
        var endHex = match.Groups["end"].Value.ToUpperInvariant();
        sourceWidth = startHex.Length;
        count = 0;
        if (startHex.Length != endHex.Length
            || !TryParseHexUInt64(startHex, out start)
            || !TryParseHexUInt64(endHex, out end)
            || end < start)
        {
            start = 0;
            end = 0;
            return false;
        }

        count = end - start + 1;
        return count <= MaxBfRangeEntries;
    }

    private static bool TryParseHexUInt64(string hex, out ulong value)
        => ulong.TryParse(
            hex,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out value);

    private static string? IncrementHexString(string hex, ulong offset)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0 || !IsHex(hex))
        {
            return null;
        }

        var bytes = Convert.FromHexString(hex);
        for (ulong i = 0; i < offset; i++)
        {
            var carry = true;
            for (var byteIndex = bytes.Length - 1; byteIndex >= 0; byteIndex--)
            {
                bytes[byteIndex]++;
                if (bytes[byteIndex] != 0)
                {
                    carry = false;
                    break;
                }
            }

            if (carry)
            {
                return null;
            }
        }

        return Convert.ToHexString(bytes);
    }

    private static PdfObject? Dereference(PdfObject? obj)
        => obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;

    private static string? DecodeUtf16BeHex(string hex)
    {
        hex = RemediationHelpers.NormalizeWhitespace(hex).ToUpperInvariant();
        if (hex.Length == 0)
        {
            return string.Empty;
        }

        if (hex.Length % 4 != 0 || !IsHex(hex))
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

    private static bool IsSafeRepairCandidate(RepairCandidate candidate)
    {
        if (candidate.Source is "null_padding")
        {
            return candidate.Replacement == " ";
        }

        if (candidate.Source is "observed_text_coverage")
        {
            return IsSafeCoverageReplacement(candidate.Replacement);
        }

        if (candidate.Source is "known_control_code" && candidate.Replacement == " ")
        {
            return true;
        }

        return IsSafeReplacement(candidate.Replacement);
    }

    private static bool PreserveReplacementWhitespace(string source)
        => source is "null_padding" or "observed_text_coverage"
            || source is "known_control_code";

    private static string? NormalizeCoverageReplacement(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.All(char.IsWhiteSpace))
        {
            return " ";
        }

        return text;
    }

    private static bool IsSafeCoverageReplacement(string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || replacement.Length > 4)
        {
            return false;
        }

        if (replacement == " ")
        {
            return true;
        }

        foreach (var ch in replacement)
        {
            if (IsBadExtractedChar(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch))
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

    private static string KeepFirstChars(string text, int maxChars)
        => string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[..maxChars];

    private static string KeepLastChars(string text, int maxChars)
        => string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[^maxChars..];

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
                            ch.Text,
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
        string ExtractedText,
        int PageNumber,
        int? Mcid,
        string EventText,
        string LineWithPlaceholder,
        string ContextBefore,
        string ContextAfter);

    private sealed record AnomalyGroup(RepairKey Key, IReadOnlyList<DetectedAnomaly> Anomalies);

    private sealed record RemainingIssueGroup(
        string Category,
        string FontObjectId,
        string FontName,
        string Pages,
        string Mcids,
        int SourceCodeCount,
        string SourceCodes,
        int Occurrences,
        int VisibleOccurrences,
        string Sample);

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
    int AiRepairsApplied,
    int ActualTextRepairsApplied = 0,
    int FontEncodingRepairsApplied = 0,
    int CidToGidMapRepairsApplied = 0);
