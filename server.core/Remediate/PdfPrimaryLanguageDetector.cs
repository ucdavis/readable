using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

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

        var (extractedText, _) = ExtractLanguageContext(pdf, maxPagesToScan, minWords, cancellationToken);
        return DetectPrimaryLanguage(extractedText);
    }

    // Minimal, deterministic language detection tuned for setting a reasonable PDF /Lang.
    // Script detection covers many non-Latin languages; Latin script uses simple stopword scoring.
    public static string? DetectPrimaryLanguage(string text)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hangulCount = 0;
        var hiraganaKatakanaCount = 0;
        var hanCount = 0;
        var cyrillicCount = 0;
        var arabicCount = 0;
        var hebrewCount = 0;
        var greekCount = 0;
        var devanagariCount = 0;

        foreach (var ch in text)
        {
            if (IsHangul(ch))
            {
                hangulCount++;
                continue;
            }

            if (IsHiragana(ch) || IsKatakana(ch))
            {
                hiraganaKatakanaCount++;
                continue;
            }

            if (IsHan(ch))
            {
                hanCount++;
                continue;
            }

            if (IsCyrillic(ch))
            {
                cyrillicCount++;
                continue;
            }

            if (IsArabic(ch))
            {
                arabicCount++;
                continue;
            }

            if (IsHebrew(ch))
            {
                hebrewCount++;
                continue;
            }

            if (IsGreek(ch))
            {
                greekCount++;
                continue;
            }

            if (IsDevanagari(ch))
            {
                devanagariCount++;
            }
        }

        if (hangulCount > 0)
        {
            return "ko";
        }

        if (hiraganaKatakanaCount > 0)
        {
            return "ja";
        }

        if (hanCount > 0)
        {
            // If the document is mostly Han without kana/hangul, "zh" is a reasonable default.
            return "zh";
        }

        if (cyrillicCount > 0)
        {
            return "ru";
        }

        if (arabicCount > 0)
        {
            return "ar";
        }

        if (hebrewCount > 0)
        {
            return "he";
        }

        if (greekCount > 0)
        {
            return "el";
        }

        if (devanagariCount > 0)
        {
            return "hi";
        }

        // Latin script: stopword scoring.
        var best = ScoreLatinLanguages(text);
        return best;
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

    private static string? ScoreLatinLanguages(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return null;
        }

        var en = Score(tokens, EnStopWords);
        var es = Score(tokens, EsStopWords);
        var fr = Score(tokens, FrStopWords);
        var de = Score(tokens, DeStopWords);
        var it = Score(tokens, ItStopWords);
        var pt = Score(tokens, PtStopWords);

        var scores = new List<(string Lang, int Score)>
        {
            ("en-US", en),
            ("es", es),
            ("fr", fr),
            ("de", de),
            ("it", it),
            ("pt", pt),
        };

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));

        var best = scores[0];
        var second = scores.Count > 1 ? scores[1] : default;

        // Basic confidence: require at least a couple of stopword hits and a margin over the runner-up.
        if (best.Score < 2)
        {
            return null;
        }

        if (second.Score > 0 && best.Score < second.Score + 1)
        {
            return null;
        }

        return best.Lang;
    }

    private static int Score(List<string> tokens, string[] stopWords)
    {
        if (tokens.Count == 0 || stopWords.Length == 0)
        {
            return 0;
        }

        var set = new HashSet<string>(stopWords, StringComparer.Ordinal);
        var score = 0;
        foreach (var token in tokens)
        {
            if (set.Contains(token))
            {
                score++;
            }
        }

        return score;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            Flush(sb, tokens);
        }

        Flush(sb, tokens);
        return tokens;
    }

    private static void Flush(StringBuilder sb, List<string> tokens)
    {
        if (sb.Length == 0)
        {
            return;
        }

        var token = sb.ToString();
        sb.Clear();

        if (token.Length == 1)
        {
            return;
        }

        tokens.Add(token);
    }

    private static bool IsHangul(char ch) => ch is >= '\uAC00' and <= '\uD7A3';

    private static bool IsHiragana(char ch) => ch is >= '\u3040' and <= '\u309F';

    private static bool IsKatakana(char ch) => ch is >= '\u30A0' and <= '\u30FF';

    private static bool IsHan(char ch) =>
        (ch is >= '\u4E00' and <= '\u9FFF')
        || (ch is >= '\u3400' and <= '\u4DBF');

    private static bool IsCyrillic(char ch) => ch is >= '\u0400' and <= '\u04FF';

    private static bool IsArabic(char ch) =>
        (ch is >= '\u0600' and <= '\u06FF')
        || (ch is >= '\u0750' and <= '\u077F')
        || (ch is >= '\u08A0' and <= '\u08FF');

    private static bool IsHebrew(char ch) => ch is >= '\u0590' and <= '\u05FF';

    private static bool IsGreek(char ch) => ch is >= '\u0370' and <= '\u03FF';

    private static bool IsDevanagari(char ch) => ch is >= '\u0900' and <= '\u097F';

    private static readonly string[] EnStopWords =
    [
        "the", "and", "to", "of", "in", "is", "for", "that", "with", "as", "on", "this", "are", "be", "by",
    ];

    private static readonly string[] EsStopWords =
    [
        "el", "la", "de", "y", "que", "en", "los", "del", "las", "por", "una", "para", "con", "no", "un",
    ];

    private static readonly string[] FrStopWords =
    [
        "le", "la", "de", "et", "les", "des", "en", "une", "que", "pour", "dans", "est", "pas", "un",
    ];

    private static readonly string[] DeStopWords =
    [
        "der", "die", "und", "in", "den", "von", "zu", "das", "mit", "des", "auf", "für", "ist", "ein", "eine",
    ];

    private static readonly string[] ItStopWords =
    [
        "di", "e", "il", "la", "che", "per", "un", "una", "in", "del", "dei", "della", "con", "non", "si",
    ];

    private static readonly string[] PtStopWords =
    [
        "de", "e", "o", "a", "que", "do", "da", "em", "para", "com", "não", "nao", "uma", "os", "as", "um",
    ];
}

