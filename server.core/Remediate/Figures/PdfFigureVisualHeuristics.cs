using System.Security.Cryptography;
using System.Text;
using iText.Kernel.Geom;
using server.core.Remediate.Rasterize;

namespace server.core.Remediate.Figures;

internal static class PdfFigureVisualHeuristics
{
    public const int RepeatedFigureChromeThreshold = 3;

    private const double TinyFigureAreaRatio = 0.001;
    private const float TinyFigureMinDimensionPts = 12f;
    private const double LowInformationDominantColorRatio = 0.985;
    private const double LowInformationNonBackgroundRatio = 0.015;

    public static bool IsTinyFigureBounds(Rectangle boundsPts, Rectangle pageSizePts, out string reason)
    {
        reason = string.Empty;

        var figureWidth = boundsPts.GetWidth();
        var figureHeight = boundsPts.GetHeight();
        var pageArea = pageSizePts.GetWidth() * pageSizePts.GetHeight();
        var figureArea = figureWidth * figureHeight;

        if (figureWidth <= 0 || figureHeight <= 0 || pageArea <= 0)
        {
            reason = "figure has invalid visual bounds";
            return true;
        }

        if (figureWidth < TinyFigureMinDimensionPts || figureHeight < TinyFigureMinDimensionPts)
        {
            reason = "figure visual bounds are too small for useful alt text";
            return true;
        }

        if (figureArea / pageArea < TinyFigureAreaRatio)
        {
            reason = "figure visual area is too small for useful alt text";
            return true;
        }

        return false;
    }

    public static string? BuildRepeatedChromeSignature(string visualHash, Rectangle boundsPts, Rectangle pageSizePts)
    {
        var pageWidth = pageSizePts.GetWidth();
        var pageHeight = pageSizePts.GetHeight();
        if (string.IsNullOrWhiteSpace(visualHash) || pageWidth <= 0 || pageHeight <= 0)
        {
            return null;
        }

        static int Bucket(float value, float extent) => (int)Math.Round(value / extent * 1000f, MidpointRounding.AwayFromZero);

        var x = Bucket(boundsPts.GetX(), pageWidth);
        var y = Bucket(boundsPts.GetY(), pageHeight);
        var w = Bucket(boundsPts.GetWidth(), pageWidth);
        var h = Bucket(boundsPts.GetHeight(), pageHeight);
        return $"{visualHash}:{x}:{y}:{w}:{h}";
    }

    public static bool TryClassifyLowInformationBitmap(BgraBitmap bitmap, out string reason)
    {
        reason = string.Empty;
        if (!bitmap.IsValid)
        {
            reason = "figure crop is invalid";
            return true;
        }

        var totalPixels = bitmap.WidthPx * bitmap.HeightPx;
        if (totalPixels <= 0)
        {
            reason = "figure crop is empty";
            return true;
        }

        var backgroundLikePixels = 0;
        var nonBackgroundPixels = 0;
        var minNonBackgroundX = bitmap.WidthPx;
        var minNonBackgroundY = bitmap.HeightPx;
        var maxNonBackgroundX = -1;
        var maxNonBackgroundY = -1;
        var colorCounts = new Dictionary<int, int>();

        for (var y = 0; y < bitmap.HeightPx; y++)
        {
            var rowOffset = y * bitmap.StrideBytes;
            for (var x = 0; x < bitmap.WidthPx; x++)
            {
                var offset = rowOffset + (x * 4);
                var b = bitmap.BgraBytes[offset + 0];
                var g = bitmap.BgraBytes[offset + 1];
                var r = bitmap.BgraBytes[offset + 2];
                var a = bitmap.BgraBytes[offset + 3];

                var colorKey = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                colorCounts[colorKey] = colorCounts.TryGetValue(colorKey, out var count) ? count + 1 : 1;

                if (IsBackgroundLikePixel(r, g, b, a))
                {
                    backgroundLikePixels++;
                    continue;
                }

                nonBackgroundPixels++;
                minNonBackgroundX = Math.Min(minNonBackgroundX, x);
                minNonBackgroundY = Math.Min(minNonBackgroundY, y);
                maxNonBackgroundX = Math.Max(maxNonBackgroundX, x);
                maxNonBackgroundY = Math.Max(maxNonBackgroundY, y);
            }
        }

        if (backgroundLikePixels / (double)totalPixels >= LowInformationDominantColorRatio)
        {
            reason = "figure crop is blank or mostly page background";
            return true;
        }

        if (nonBackgroundPixels / (double)totalPixels <= LowInformationNonBackgroundRatio)
        {
            reason = "figure crop has too little visible content";
            return true;
        }

        var dominantColorPixels = colorCounts.Values.Count == 0 ? 0 : colorCounts.Values.Max();
        if (dominantColorPixels / (double)totalPixels >= LowInformationDominantColorRatio)
        {
            reason = "figure crop is nearly a single flat color";
            return true;
        }

        var nonBackgroundWidth = maxNonBackgroundX - minNonBackgroundX + 1;
        var nonBackgroundHeight = maxNonBackgroundY - minNonBackgroundY + 1;
        if (nonBackgroundWidth <= 3 || nonBackgroundHeight <= 3)
        {
            reason = "figure crop is a thin visual rule";
            return true;
        }

        return false;
    }

    public static string ComputeBitmapHash(BgraBitmap bitmap)
    {
        var prefix = Encoding.ASCII.GetBytes($"{bitmap.WidthPx}x{bitmap.HeightPx}x{bitmap.StrideBytes}:");
        var bytes = new byte[prefix.Length + bitmap.BgraBytes.Length];
        Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
        Buffer.BlockCopy(bitmap.BgraBytes, 0, bytes, prefix.Length, bitmap.BgraBytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsBackgroundLikePixel(byte r, byte g, byte b, byte a)
        => a <= 8 || (r >= 245 && g >= 245 && b >= 245);
}
