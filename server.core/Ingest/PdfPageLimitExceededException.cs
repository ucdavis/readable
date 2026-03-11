namespace server.core.Ingest;

public sealed class PdfPageLimitExceededException : Exception
{
    public PdfPageLimitExceededException(int actualPageCount, int maxAllowedPages)
        : base($"PDF has {actualPageCount} pages; maximum allowed is {maxAllowedPages}.")
    {
        ActualPageCount = actualPageCount;
        MaxAllowedPages = maxAllowedPages;
    }

    public int ActualPageCount { get; }

    public int MaxAllowedPages { get; }
}
