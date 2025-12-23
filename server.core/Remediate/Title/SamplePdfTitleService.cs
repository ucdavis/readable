namespace server.core.Remediate.Title;

public sealed class SamplePdfTitleService : IPdfTitleService
{
    public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(request.CurrentTitle))
        {
            return Task.FromResult(request.CurrentTitle);
        }

        return Task.FromResult("sample PDF title");
    }
}
