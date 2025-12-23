using System.Diagnostics;
using Microsoft.Extensions.Configuration;
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
    private readonly IBlobStorage _blobStorage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileIngestProcessor> _logger;

    public FileIngestProcessor(
        IBlobStreamOpener blobStreamOpener,
        IPdfProcessor pdfProcessor,
        IBlobStorage blobStorage,
        IConfiguration configuration,
        ILogger<FileIngestProcessor> logger)
    {
        _blobStreamOpener = blobStreamOpener;
        _pdfProcessor = pdfProcessor;
        _blobStorage = blobStorage;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Reads the source blob stream and hands it to the PDF processor, adding basic telemetry/logging.
    /// </summary>
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
        var pdfResult = await _pdfProcessor.ProcessAsync(request.FileId, stream, cancellationToken);

        var processedContainerName =
            _configuration["Storage:ProcessedContainer"]
            ?? _configuration["Storage__ProcessedContainer"]
            ?? "processed";

        var processedBlobUri = BuildSiblingContainerUri(
            request.BlobUri,
            processedContainerName,
            request.BlobName);

        await UploadFinalPdfAsync(
            fileId: request.FileId,
            localPdfPath: pdfResult.OutputPdfPath,
            destinationBlobUri: processedBlobUri,
            cancellationToken: cancellationToken);

        await DeleteIncomingBlobAsync(request, cancellationToken);

        _logger.LogInformation("Completed ingest for {fileId}", request.FileId);
    }

    private async Task UploadFinalPdfAsync(
        string fileId,
        string localPdfPath,
        Uri destinationBlobUri,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Uploading processed PDF for {fileId} from {path} to {destinationBlobUri}",
            fileId,
            localPdfPath,
            destinationBlobUri);

        await using var finalStream = File.OpenRead(localPdfPath);
        await _blobStorage.UploadAsync(
            destinationBlobUri,
            finalStream,
            contentType: "application/pdf",
            cancellationToken);
    }

    private async Task DeleteIncomingBlobAsync(BlobIngestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _blobStorage.DeleteIfExistsAsync(request.BlobUri, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete incoming blob for {fileId}; leaving it in place at {blobUri}",
                request.FileId,
                request.BlobUri);
        }
    }

    private static Uri BuildSiblingContainerUri(Uri sourceBlobUri, string destinationContainer, string destinationBlobName)
    {
        if (string.IsNullOrWhiteSpace(destinationContainer))
        {
            throw new ArgumentException("Destination container name was empty.", nameof(destinationContainer));
        }

        if (string.IsNullOrWhiteSpace(destinationBlobName))
        {
            throw new ArgumentException("Destination blob name was empty.", nameof(destinationBlobName));
        }

        var builder = new UriBuilder(sourceBlobUri)
        {
            Path = $"/{destinationContainer.Trim('/')}/{destinationBlobName.TrimStart('/')}",
        };

        return builder.Uri;
    }
}
