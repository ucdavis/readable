namespace server.core.Ingest;

public interface IPdfRemediationProcessor
{
    Task<PdfRemediationResult> ProcessAsync(
        string fileId,
        string inputPdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken);
}

public sealed record PdfRemediationResult(string OutputPdfPath);

public sealed class NoopPdfRemediationProcessor : IPdfRemediationProcessor
{
    public async Task<PdfRemediationResult> ProcessAsync(
        string fileId,
        string inputPdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPdfPath)!);
        await using var input = File.OpenRead(inputPdfPath);
        await using var output = File.Open(outputPdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        return new PdfRemediationResult(outputPdfPath);
    }
}

