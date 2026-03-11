namespace server.core.Ingest;

public sealed class PdfPageLimitExceededException : InvalidOperationException
{
    public PdfPageLimitExceededException(int pageCount, int maxPageCount)
        : base(BuildMessage(pageCount, maxPageCount))
    {
        PageCount = pageCount;
        MaxPageCount = maxPageCount;
    }

    public int PageCount { get; }

    public int MaxPageCount { get; }

    public static string BuildMessage(int pageCount, int maxPageCount) =>
        $"PDFs are temporarily limited to {maxPageCount} pages. This file has {pageCount} pages.";
}
