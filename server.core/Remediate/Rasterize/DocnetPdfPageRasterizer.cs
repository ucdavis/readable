using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace server.core.Remediate.Rasterize;

public sealed class DocnetPdfPageRasterizer : IPdfPageRasterizer
{
    private readonly IDocLib _docLib;

    public DocnetPdfPageRasterizer()
    {
        _docLib = DocLib.Instance;
    }

    public bool IsAvailable => true;

    public IPdfRasterDocument OpenDocument(string pdfPath, int dpi)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            throw new ArgumentException("PDF path is required.", nameof(pdfPath));
        }

        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be positive.");
        }

        var scaleFactor = dpi / 72.0;
        var docReader = _docLib.GetDocReader(pdfPath, new PageDimensions(scaleFactor));
        return new DocnetRasterDocument(docReader);
    }

    private sealed class DocnetRasterDocument : IPdfRasterDocument
    {
        private readonly IDocReader _docReader;
        private readonly NaiveTransparencyRemover _transparencyRemover = new(255, 255, 255);

        public DocnetRasterDocument(IDocReader docReader)
        {
            _docReader = docReader ?? throw new ArgumentNullException(nameof(docReader));
        }

        public void Dispose()
        {
            _docReader.Dispose();
        }

        public BgraBitmap RenderPage(int pageNumber1Based, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pageNumber1Based <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber1Based), pageNumber1Based, "Page number must be 1-based and positive.");
            }

            var pageIndex = pageNumber1Based - 1;
            using var pageReader = _docReader.GetPageReader(pageIndex);

            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            // Docnet returns pixels in B-G-R-A order.
            var bytes = pageReader.GetImage(_transparencyRemover);
            var stride = width * 4;
            return new BgraBitmap(bytes, width, height, stride);
        }
    }
}
