namespace server.core.Remediate.Table;

public enum PdfTableKind
{
    DataTable,
    NotDataTable,
}

public sealed record PdfTableClassificationRequest(
    int RowCount,
    int MaxColumnCount,
    bool HasNestedTable,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? PrimaryLanguage = null);

public sealed record PdfTableClassificationResult(PdfTableKind Kind, double Confidence, string Reason);

public interface IPdfTableClassificationService
{
    Task<PdfTableClassificationResult> ClassifyAsync(
        PdfTableClassificationRequest request,
        CancellationToken cancellationToken);
}
