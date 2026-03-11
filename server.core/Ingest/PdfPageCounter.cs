using iText.Kernel.Pdf;

namespace server.core.Ingest;

public static class PdfPageCounter
{
    public static int ReadPageCount(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        return pdf.GetNumberOfPages();
    }

    public static int ReadPageCount(Stream pdfStream)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfStream));
        return pdf.GetNumberOfPages();
    }
}
