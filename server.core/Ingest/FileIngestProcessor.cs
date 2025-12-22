using System.Diagnostics;
using Microsoft.Extensions.Logging;
using server.core.Telemetry;

namespace server.core.Ingest;

public interface IFileIngestProcessor
{
    Task ProcessAsync(BlobIngestRequest request, CancellationToken cancellationToken);
}

public sealed class FileIngestProcessor : IFileIngestProcessor
{
    private readonly IBlobStreamOpener _blobStreamOpener;
    private readonly IPdfProcessor _pdfProcessor;
    private readonly ILogger<FileIngestProcessor> _logger;

    public FileIngestProcessor(
        IBlobStreamOpener blobStreamOpener,
        IPdfProcessor pdfProcessor,
        ILogger<FileIngestProcessor> logger)
    {
        _blobStreamOpener = blobStreamOpener;
        _pdfProcessor = pdfProcessor;
        _logger = logger;
    }

    public async Task ProcessAsync(BlobIngestRequest request, CancellationToken cancellationToken)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            "file_ingest.process",
            ActivityKind.Internal);

        activity?.SetTag("file.id", request.FileId);
        activity?.SetTag("blob.container", request.ContainerName);
        activity?.SetTag("blob.name", request.BlobName);
        activity?.SetTag("url.full", request.BlobUri.ToString());

        _logger.LogInformation(
            "Starting ingest for {fileId} from {blobUri}",
            request.FileId,
            request.BlobUri);

        await using var stream = await _blobStreamOpener.OpenReadAsync(request.BlobUri, cancellationToken);
        await _pdfProcessor.ProcessAsync(request.FileId, stream, cancellationToken);

        _logger.LogInformation("Completed ingest for {fileId}", request.FileId);
    }
}
