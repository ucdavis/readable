namespace server.core.Remediate.Title;

public sealed record PdfTitleRequest(string CurrentTitle, string ExtractedText, string? PrimaryLanguage = null);

public interface IPdfTitleService
{
    Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken);
}
