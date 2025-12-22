namespace server.core.Ingest;

public interface IPdfProcessor
{
    Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken);
}

public sealed class NoopPdfProcessor : IPdfProcessor
{
    public Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

