namespace server.core.Remediate.Rasterize;

public sealed class NoopPdfPageRasterizer : IPdfPageRasterizer
{
    public static NoopPdfPageRasterizer Instance { get; } = new();

    private NoopPdfPageRasterizer()
    {
    }

    public bool IsAvailable => false;

    public IPdfRasterDocument OpenDocument(string pdfPath, int dpi) => new NoopPdfRasterDocument();

    private sealed class NoopPdfRasterDocument : IPdfRasterDocument
    {
        public void Dispose()
        {
        }

        public BgraBitmap RenderPage(int pageNumber1Based, CancellationToken cancellationToken)
        {
            _ = pageNumber1Based;
            cancellationToken.ThrowIfCancellationRequested();
            return new BgraBitmap(Array.Empty<byte>(), 0, 0, 0);
        }
    }
}

