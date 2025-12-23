namespace server.core.Remediate.Title;

public sealed record PdfTitleRequest(string CurrentTitle, string ExtractedText);

public interface IPdfTitleService
{
    Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken);
}

