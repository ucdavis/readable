namespace server.core.Remediate.Table;

public sealed class SamplePdfTableClassificationService : IPdfTableClassificationService
{
    public Task<PdfTableClassificationResult> ClassifyAsync(
        PdfTableClassificationRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PdfTableClassificationResult(PdfTableKind.DataTable, 1, "Sample classifier preserves table semantics."));
    }
}
