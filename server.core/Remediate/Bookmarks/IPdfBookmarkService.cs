using iText.Kernel.Pdf;

namespace server.core.Remediate.Bookmarks;

public interface IPdfBookmarkService
{
    /// <summary>
    /// Ensures the provided PDF has bookmarks (outlines) when possible.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw; failures should be treated as a no-op.
    /// </remarks>
    Task EnsureBookmarksAsync(PdfDocument pdf, CancellationToken cancellationToken);
}

