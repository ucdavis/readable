namespace server.core.Remediate;

public interface IPdfCharacterEncodingRepairService
{
    Task<PdfCharacterEncodingRepairResponse> ProposeRepairsAsync(
        PdfCharacterEncodingRepairRequest request,
        CancellationToken cancellationToken);

    Task<PdfCharacterEncodingActualTextRepairResponse> ProposeActualTextRepairsAsync(
        PdfCharacterEncodingActualTextRepairRequest request,
        CancellationToken cancellationToken);
}

public sealed record PdfCharacterEncodingRepairRequest(
    IReadOnlyList<PdfCharacterEncodingAnomalyGroup> Groups,
    string? PrimaryLanguage);

public sealed record PdfCharacterEncodingAnomalyGroup(
    string FontObjectId,
    string FontName,
    string SourceCode,
    string AnomalyKind,
    IReadOnlyList<PdfCharacterEncodingAnomalyContext> Contexts);

public sealed record PdfCharacterEncodingAnomalyContext(
    int PageNumber,
    int? Mcid,
    string Line,
    string ContextBefore,
    string ContextAfter);

public sealed record PdfCharacterEncodingRepairResponse(
    IReadOnlyList<PdfCharacterEncodingRepairProposal> Repairs);

public sealed record PdfCharacterEncodingRepairProposal(
    string FontObjectId,
    string SourceCode,
    string Replacement,
    double Confidence,
    string Reason);

public sealed record PdfCharacterEncodingActualTextRepairRequest(
    IReadOnlyList<PdfCharacterEncodingActualTextIssue> Issues,
    string? PrimaryLanguage);

public sealed record PdfCharacterEncodingActualTextIssue(
    int PageNumber,
    int Mcid,
    string RawText,
    string ContextBefore,
    string ContextAfter);

public sealed record PdfCharacterEncodingActualTextRepairResponse(
    IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal> Repairs);

public sealed record PdfCharacterEncodingActualTextRepairProposal(
    int PageNumber,
    int Mcid,
    string ActualText,
    double Confidence,
    string Reason);

public sealed class NoopPdfCharacterEncodingRepairService : IPdfCharacterEncodingRepairService
{
    public static NoopPdfCharacterEncodingRepairService Instance { get; } = new();

    private NoopPdfCharacterEncodingRepairService()
    {
    }

    public Task<PdfCharacterEncodingRepairResponse> ProposeRepairsAsync(
        PdfCharacterEncodingRepairRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PdfCharacterEncodingRepairResponse(Array.Empty<PdfCharacterEncodingRepairProposal>()));
    }

    public Task<PdfCharacterEncodingActualTextRepairResponse> ProposeActualTextRepairsAsync(
        PdfCharacterEncodingActualTextRepairRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PdfCharacterEncodingActualTextRepairResponse(Array.Empty<PdfCharacterEncodingActualTextRepairProposal>()));
    }
}
