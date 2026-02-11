using System.Diagnostics.CodeAnalysis;

namespace server.core.Remediate.Rasterize;

public interface IPdfPageRasterizer
{
    bool IsAvailable { get; }

    IPdfRasterDocument OpenDocument(string pdfPath, int dpi);
}

public interface IPdfRasterDocument : IDisposable
{
    BgraBitmap RenderPage(int pageNumber1Based, CancellationToken cancellationToken);
}

public sealed record BgraBitmap(byte[] BgraBytes, int WidthPx, int HeightPx, int StrideBytes)
{
    [MemberNotNullWhen(true, nameof(BgraBytes))]
    public bool IsValid => BgraBytes is { Length: > 0 }
                           && WidthPx > 0
                           && HeightPx > 0
                           && StrideBytes >= WidthPx * 4;
}

