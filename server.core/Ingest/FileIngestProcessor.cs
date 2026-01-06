using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using server.core.Data;
using server.core.Domain;
using server.core.Telemetry;

namespace server.core.Ingest;

public interface IFileIngestProcessor
{
    Task ProcessAsync(BlobIngestRequest request, CancellationToken cancellationToken);
}

public sealed class FileIngestProcessor : IFileIngestProcessor
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IBlobStreamOpener _blobStreamOpener;
    private readonly IPdfProcessor _pdfProcessor;
    private readonly IBlobStorage _blobStorage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileIngestProcessor> _logger;

    public FileIngestProcessor(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IBlobStreamOpener blobStreamOpener,
        IPdfProcessor pdfProcessor,
        IBlobStorage blobStorage,
        IConfiguration configuration,
        ILogger<FileIngestProcessor> logger)
    {
        _dbContextFactory = dbContextFactory;
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

        if (!Guid.TryParse(request.FileId, out var fileId))
        {
            _logger.LogError("Invalid file id '{fileId}' for ingest; expected a GUID.", request.FileId);
            throw new InvalidOperationException($"Invalid file id '{request.FileId}' for ingest; expected a GUID.");
        }

        // record the processing attempt in the database
        var attemptId = await StartProcessingAttemptAsync(fileId, cancellationToken);

        // start the actual processing
        try
        {
            await using var stream = await _blobStreamOpener.OpenReadAsync(request.BlobUri, cancellationToken);
            var pdfResult = await _pdfProcessor.ProcessAsync(request.FileId, stream, cancellationToken);

            // pdf processing done, move the resulting PDF to the processed container and clean up
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

            // attempt to save accessibility report if present
            // later we might want to make it required but i think it's better to just ensure the pdf gets processed
            var beforeAccessibilityReportJson = pdfResult.BeforeAccessibilityReportJson;
            if (!string.IsNullOrWhiteSpace(beforeAccessibilityReportJson))
            {
                try
                {
                    await SaveAccessibilityReportAsync(
                        fileId,
                        tool: "AdobePdfServices",
                        stage: AccessibilityReport.Stages.Before,
                        reportJson: beforeAccessibilityReportJson,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to persist BEFORE accessibility report for {fileId}", request.FileId);
                }
            }

            var afterAccessibilityReportJson = pdfResult.AfterAccessibilityReportJson;
            if (!string.IsNullOrWhiteSpace(afterAccessibilityReportJson))
            {
                try
                {
                    await SaveAccessibilityReportAsync(
                        fileId,
                        tool: "AdobePdfServices",
                        stage: AccessibilityReport.Stages.After,
                        reportJson: afterAccessibilityReportJson,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to persist AFTER accessibility report for {fileId}", request.FileId);
                }
            }

            await CompleteProcessingAttemptAsync(
                fileId,
                attemptId,
                outcome: FileProcessingAttempt.Outcomes.Succeeded,
                error: null,
                request,
                CancellationToken.None);

            _logger.LogInformation("Completed ingest for {fileId}", request.FileId);
        }
        catch (OperationCanceledException oce)
        {
            await CompleteProcessingAttemptAsync(
                fileId,
                attemptId,
                outcome: FileProcessingAttempt.Outcomes.Cancelled,
                error: oce,
                request,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await CompleteProcessingAttemptAsync(
                fileId,
                attemptId,
                outcome: FileProcessingAttempt.Outcomes.Failed,
                error: ex,
                request,
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Saves the accessibility report JSON to the database.
    /// We also have the option to store it as a file/blob in the future if needed (using `accessibilityReport?.ReportPath`).
    /// </summary>
    private async Task SaveAccessibilityReportAsync(
        Guid fileId,
        string tool,
        string stage,
        string reportJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return;
        }

        if (!IsValidJson(reportJson))
        {
            _logger.LogWarning(
                "Skipping accessibility report persistence; invalid JSON for fileId={fileId} stage={stage} tool={tool}",
                fileId,
                stage,
                tool);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await dbContext.AccessibilityReports
            .SingleOrDefaultAsync(
                x => x.FileId == fileId && x.Stage == stage && x.Tool == tool,
                cancellationToken);

        if (existing is null)
        {
            dbContext.AccessibilityReports.Add(new AccessibilityReport
            {
                FileId = fileId,
                Stage = stage,
                Tool = tool,
                GeneratedAt = now,
                IssueCount = null,
                ReportJson = reportJson,
            });
        }
        else
        {
            existing.GeneratedAt = now;
            existing.IssueCount = null;
            existing.ReportJson = reportJson;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private async Task<long> StartProcessingAttemptAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var file = await dbContext.Files.SingleOrDefaultAsync(x => x.FileId == fileId, cancellationToken);
        if (file is null)
        {
            _logger.LogError("No file record found for ingest fileId={fileId}", fileId);
            throw new InvalidOperationException($"No file record found for ingest fileId={fileId}");
        }

        file.Status = FileRecord.Statuses.Processing;
        file.StatusUpdatedAt = now;

        var lastAttemptNumber = await dbContext.FileProcessingAttempts
            .Where(x => x.FileId == fileId)
            .MaxAsync(x => (int?)x.AttemptNumber, cancellationToken)
            ?? 0;

        var attempt = new FileProcessingAttempt
        {
            FileId = fileId,
            AttemptNumber = lastAttemptNumber + 1,
            Trigger = FileProcessingAttempt.Triggers.Upload,
            StartedAt = now,
            Outcome = null,
        };

        dbContext.FileProcessingAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);

        return attempt.AttemptId;
    }

    private async Task CompleteProcessingAttemptAsync(
        Guid fileId,
        long attemptId,
        string outcome,
        Exception? error,
        BlobIngestRequest request,
        CancellationToken cancellationToken)
    {
        var finishedAt = DateTimeOffset.UtcNow;

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var attempt = await dbContext.FileProcessingAttempts
                .Include(x => x.File)
                .SingleOrDefaultAsync(x => x.AttemptId == attemptId && x.FileId == fileId, cancellationToken);

            if (attempt is null)
            {
                _logger.LogWarning(
                    "Unable to finalize ingest attempt; attemptId={attemptId} fileId={fileId} not found.",
                    attemptId,
                    fileId);
                return;
            }

            attempt.Outcome = outcome;
            attempt.FinishedAt = finishedAt;

            if (error is not null)
            {
                attempt.ErrorCode = error.GetType().Name;
                attempt.ErrorMessage = Truncate(error.Message, 2048);
                attempt.ErrorDetails =
                    $"BlobUri: {request.BlobUri}\nContainer: {request.ContainerName}\nBlobName: {request.BlobName}\n\n{error}";
            }
            else
            {
                attempt.ErrorCode = null;
                attempt.ErrorMessage = null;
                attempt.ErrorDetails = null;
            }

            attempt.File.Status = outcome switch
            {
                FileProcessingAttempt.Outcomes.Succeeded => FileRecord.Statuses.Completed,
                FileProcessingAttempt.Outcomes.Cancelled => FileRecord.Statuses.Cancelled,
                _ => FileRecord.Statuses.Failed,
            };
            attempt.File.StatusUpdatedAt = finishedAt;

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to finalize ingest attempt; attemptId={attemptId} fileId={fileId} outcome={outcome}",
                attemptId,
                fileId,
                outcome);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
