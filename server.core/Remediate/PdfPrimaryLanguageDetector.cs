using System.Globalization;
using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Panlingo.LanguageIdentification.Whatlang;

namespace server.core.Remediate;

internal static class PdfPrimaryLanguageDetector
{
    public static bool TrySetPrimaryLanguageIfMissing(
        PdfDocument pdf,
        string defaultLanguage,
        int maxPagesToScan,
        int minWords,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var currentLang = RemediationHelpers.NormalizeWhitespace(
            catalogDict.GetAsString(PdfName.Lang)?.ToUnicodeString() ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(currentLang))
        {
            return false;
        }

        var detectedLang = DetectPrimaryLanguage(pdf, maxPagesToScan, minWords, cancellationToken) ?? defaultLanguage;
        detectedLang = RemediationHelpers.NormalizeWhitespace(detectedLang);

        if (string.IsNullOrWhiteSpace(detectedLang))
        {
            detectedLang = defaultLanguage;
        }

        catalogDict.Put(PdfName.Lang, new PdfString(detectedLang));
        return true;
    }

    public static string? DetectPrimaryLanguage(
        PdfDocument pdf,
        int maxPagesToScan,
        int minWords,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (extractedText, wordCount) = ExtractLanguageContext(pdf, maxPagesToScan, minWords, cancellationToken);
        if (wordCount < minWords)
        {
            return null;
        }

        return DetectPrimaryLanguage(extractedText);
    }

    // Language detection is handled by Whatlang; this method returns a BCP-47 compatible language tag.
    public static string? DetectPrimaryLanguage(string text)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var whatlang = new WhatlangDetector();
            var prediction = whatlang.PredictLanguage(text);
            if (prediction is null || !prediction.IsReliable)
            {
                return null;
            }

            var iso6393 = whatlang.GetLanguageCode(prediction.Language);
            iso6393 = RemediationHelpers.NormalizeWhitespace(iso6393).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(iso6393))
            {
                return null;
            }

            return TryMapIso6393ToBcp47(iso6393);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (WhatlangDetectorException)
        {
            return null;
        }
    }

    private static (string ExtractedText, int WordCount) ExtractLanguageContext(
        PdfDocument pdf,
        int maxPagesToScan,
        int minWords,
        CancellationToken cancellationToken)
    {
        var pagesToScan = Math.Min(pdf.GetNumberOfPages(), maxPagesToScan);
        if (pagesToScan <= 0)
        {
            return (string.Empty, 0);
        }

        var sb = new StringBuilder();
        var wordCount = 0;

        for (var pageNumber = 1; pageNumber <= pagesToScan; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pdf.GetPage(pageNumber);
            var pageText = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());
            pageText = RemediationHelpers.NormalizeWhitespace(pageText);
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

            if (wordCount >= minWords)
            {
                break;
            }
        }

        var extracted = RemediationHelpers.NormalizeWhitespace(sb.ToString());
        return (extracted, wordCount);
    }

    private static string TryMapIso6393ToBcp47(string iso6393)
    {
        if (Iso6393ToIso6391Map.Value.TryGetValue(iso6393, out var iso6391))
        {
            return iso6391;
        }

        // ISO 639-3 codes are valid primary subtags in BCP-47 (via the IANA language subtag registry).
        return iso6393;
    }

    private static readonly Lazy<Dictionary<string, string>> Iso6393ToIso6391Map = new(BuildIso6393ToIso6391Map);

    private static Dictionary<string, string> BuildIso6393ToIso6391Map()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var iso6393 = culture.ThreeLetterISOLanguageName;
            var iso6391 = culture.TwoLetterISOLanguageName;

            if (string.IsNullOrWhiteSpace(iso6393) || string.IsNullOrWhiteSpace(iso6391) || iso6391 == "iv")
            {
                continue;
            }

            map.TryAdd(iso6393.ToLowerInvariant(), iso6391.ToLowerInvariant());
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
}
