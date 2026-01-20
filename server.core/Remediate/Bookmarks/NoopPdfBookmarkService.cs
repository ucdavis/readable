using iText.Kernel.Pdf;

namespace server.core.Remediate.Bookmarks;

internal sealed class NoopPdfBookmarkService : IPdfBookmarkService
{
    public Task EnsureBookmarksAsync(PdfDocument pdf, CancellationToken cancellationToken)
    {
        _ = pdf;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

