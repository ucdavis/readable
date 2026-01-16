using iText.Kernel.Pdf;
using server.core.Remediate.Bookmarks;

namespace server.tests.Integration.Remediate;

internal sealed class NoopPdfBookmarkService : IPdfBookmarkService
{
    public Task EnsureBookmarksAsync(PdfDocument pdf, CancellationToken cancellationToken)
    {
        _ = pdf;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

