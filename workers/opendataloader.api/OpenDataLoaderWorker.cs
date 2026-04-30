using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using server.core.Ingest;

namespace opendataloader.api;

public sealed class OpenDataLoaderWorker : BackgroundService
{
    private readonly ILogger<OpenDataLoaderWorker> _logger;
    private readonly IOpenDataLoaderRunner _runner;
    private readonly IRuntimeDependencyProbe _dependencyProbe;
    private readonly OpenDataLoaderOptions _options;

    private ServiceBusClient? _serviceBusClient;
    private ServiceBusProcessor? _processor;
    private ServiceBusSender? _finalizeSender;
    private ServiceBusSender? _failureSender;

    public OpenDataLoaderWorker(
        ILogger<OpenDataLoaderWorker> logger,
        IOpenDataLoaderRunner runner,
        IRuntimeDependencyProbe dependencyProbe,
        OpenDataLoaderOptions options)
    {
        _logger = logger;
        _runner = runner;
        _dependencyProbe = dependencyProbe;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ValidateConfiguration();

        var dependencyStatus = _dependencyProbe.Probe(_options);
        if (!dependencyStatus.Ok)
        {
            throw new InvalidOperationException(
                $"OpenDataLoader worker dependencies are not ready: {JsonSerializer.Serialize(dependencyStatus)}");
        }

        _serviceBusClient = new ServiceBusClient(_options.ServiceBusConnectionString);
        _finalizeSender = _serviceBusClient.CreateSender(_options.FinalizeQueueName);
        _failureSender = _serviceBusClient.CreateSender(_options.FailedQueueName);
        _processor = _serviceBusClient.CreateProcessor(
            _options.AutotagQueueName,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = _options.MaxConcurrentConversions,
                MaxAutoLockRenewalDuration = _options.ProcessTimeout + TimeSpan.FromMinutes(5),
            });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _logger.LogInformation(
            "Starting OpenDataLoader worker. autotagQueue={autotagQueue} finalizeQueue={finalizeQueue} failedQueue={failedQueue} maxConcurrentConversions={maxConcurrentConversions}",
            _options.AutotagQueueName,
            _options.FinalizeQueueName,
            _options.FailedQueueName,
            _options.MaxConcurrentConversions);

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        if (_finalizeSender is not null)
        {
            await _finalizeSender.DisposeAsync();
        }

        if (_failureSender is not null)
        {
            await _failureSender.DisposeAsync();
        }

        if (_serviceBusClient is not null)
        {
            await _serviceBusClient.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();
        var job = IngestQueueMessageJson.Deserialize<AutotagJobMessage>(body);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["file.id"] = job.FileId,
            ["messaging.message.id"] = args.Message.MessageId,
            ["messaging.destination.name"] = _options.AutotagQueueName
        });

        _logger.LogInformation(
            "Processing OpenDataLoader autotag job fileId={fileId} source={source} output={output}",
            job.FileId,
            job.SourceBlobUri,
            job.OutputTaggedPdfBlobUri);

        var workDir = Path.Combine(Path.GetTempPath(), "readable-opendataloader-worker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var inputPath = Path.Combine(workDir, "input.pdf");
        var outputDirectory = Path.Combine(workDir, "output");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await DownloadBlobAsync(job.SourceBlobUri, inputPath, args.CancellationToken);

            OpenDataLoaderRunResult result;
            using (var timeoutCts = new CancellationTokenSource(_options.ProcessTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(args.CancellationToken, timeoutCts.Token))
            {
                result = await _runner.ConvertAsync(inputPath, outputDirectory, linkedCts.Token);
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    _options.SanitizeError($"{result.StandardError}\n{result.StandardOutput}"));
            }

            if (string.IsNullOrWhiteSpace(result.TaggedPdfPath) || !File.Exists(result.TaggedPdfPath))
            {
                throw new InvalidOperationException("OpenDataLoader completed without producing a tagged PDF artifact.");
            }

            await UploadBlobAsync(job.OutputTaggedPdfBlobUri, result.TaggedPdfPath, "application/pdf", args.CancellationToken);
            await WriteReportAsync(job, result, args.CancellationToken);
            await SendFinalizeMessageAsync(job, args.CancellationToken);

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);

            _logger.LogInformation(
                "OpenDataLoader autotag job complete fileId={fileId} output={output}",
                job.FileId,
                job.OutputTaggedPdfBlobUri);
        }
        catch (Exception ex) when (!args.CancellationToken.IsCancellationRequested)
        {
            if (ShouldReportTerminalFailure(args.Message))
            {
                await SendFailureMessageAsync(job, ex, args.Message.DeliveryCount, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);

                _logger.LogError(
                    ex,
                    "OpenDataLoader autotag job failed terminally and was reported to ingest. fileId={fileId} deliveryCount={deliveryCount}",
                    job.FileId,
                    args.Message.DeliveryCount);
                return;
            }

            _logger.LogWarning(
                ex,
                "OpenDataLoader autotag job failed; message will be retried. fileId={fileId} deliveryCount={deliveryCount} maxDeliveryCount={maxDeliveryCount}",
                job.FileId,
                args.Message.DeliveryCount,
                _options.MaxDeliveryCount);
            throw;
        }
        finally
        {
            CleanupWorkDir(workDir);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "OpenDataLoader worker Service Bus error. entity={entity} source={source}",
            args.EntityPath,
            args.ErrorSource);
        return Task.CompletedTask;
    }

    private async Task DownloadBlobAsync(Uri sourceBlobUri, string destinationPath, CancellationToken cancellationToken)
    {
        var client = CreateBlobClient(sourceBlobUri);
        await using var output = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await client.DownloadToAsync(output, cancellationToken);
    }

    private async Task UploadBlobAsync(Uri destinationBlobUri, string sourcePath, string contentType, CancellationToken cancellationToken)
    {
        var client = CreateBlobClient(destinationBlobUri);
        await using var input = File.OpenRead(sourcePath);
        await client.UploadAsync(input, overwrite: true, cancellationToken);
        await client.SetHttpHeadersAsync(
            new BlobHttpHeaders { ContentType = contentType },
            cancellationToken: cancellationToken);
    }

    private async Task WriteReportAsync(
        AutotagJobMessage job,
        OpenDataLoaderRunResult result,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"readable-odl-report-{Guid.NewGuid():N}.json");
        try
        {
            await using (var output = File.Open(reportPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    output,
                    new
                    {
                        tool = "OpenDataLoader",
                        status = "succeeded",
                        job.FileId,
                        job.AttemptId,
                        sourceBlobUri = job.SourceBlobUri,
                        outputTaggedPdfBlobUri = job.OutputTaggedPdfBlobUri,
                        job.OutputReportBlobUri,
                        completedAt = DateTimeOffset.UtcNow,
                        exitCode = result.ExitCode,
                    },
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
            }

            await UploadBlobAsync(job.OutputReportBlobUri, reportPath, "application/json", cancellationToken);
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    private async Task SendFinalizeMessageAsync(AutotagJobMessage job, CancellationToken cancellationToken)
    {
        if (_finalizeSender is null)
        {
            throw new InvalidOperationException("Finalize sender has not been initialized.");
        }

        var finalizeMessage = new FinalizePdfMessage(
            FileId: job.FileId,
            AttemptId: job.AttemptId,
            OriginalBlobUri: job.OriginalBlobUri,
            OriginalContainerName: job.OriginalContainerName,
            OriginalBlobName: job.OriginalBlobName,
            PdfToFinalizeBlobUri: job.OutputTaggedPdfBlobUri,
            PageCount: job.PageCount,
            Autotag: new PdfAutotagMessageMetadata(
                Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
                Required: true,
                SkippedReason: null,
                ChunkCount: 1,
                ReportUris: [job.OutputReportBlobUri.ToString()]),
            CorrelationId: job.CorrelationId,
            EnqueuedAt: DateTimeOffset.UtcNow);

        var message = new ServiceBusMessage(IngestQueueMessageJson.Serialize(finalizeMessage))
        {
            ContentType = "application/json",
            CorrelationId = job.CorrelationId,
            MessageId = $"{nameof(FinalizePdfMessage)}:{job.FileId}:{job.CorrelationId}",
        };
        message.ApplicationProperties["fileId"] = job.FileId;
        message.ApplicationProperties["messageType"] = nameof(FinalizePdfMessage);

        await _finalizeSender.SendMessageAsync(message, cancellationToken);
    }

    private async Task SendFailureMessageAsync(
        AutotagJobMessage job,
        Exception exception,
        int deliveryCount,
        CancellationToken cancellationToken)
    {
        if (_failureSender is null)
        {
            throw new InvalidOperationException("Failure sender has not been initialized.");
        }

        var failureMessage = new AutotagFailedMessage(
            FileId: job.FileId,
            AttemptId: job.AttemptId,
            OriginalBlobUri: job.OriginalBlobUri,
            OriginalContainerName: job.OriginalContainerName,
            OriginalBlobName: job.OriginalBlobName,
            Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
            ErrorCode: exception.GetType().Name,
            ErrorMessage: _options.SanitizeError(exception.Message),
            ErrorDetails: _options.SanitizeError(exception.ToString()),
            DeliveryCount: deliveryCount,
            CorrelationId: job.CorrelationId,
            FailedAt: DateTimeOffset.UtcNow);

        var message = new ServiceBusMessage(IngestQueueMessageJson.Serialize(failureMessage))
        {
            ContentType = "application/json",
            CorrelationId = job.CorrelationId,
            MessageId = $"{nameof(AutotagFailedMessage)}:{job.FileId}:{job.CorrelationId}",
        };
        message.ApplicationProperties["fileId"] = job.FileId;
        message.ApplicationProperties["messageType"] = nameof(AutotagFailedMessage);

        await _failureSender.SendMessageAsync(message, cancellationToken);
    }

    private bool ShouldReportTerminalFailure(ServiceBusReceivedMessage message)
    {
        return message.DeliveryCount >= _options.MaxDeliveryCount;
    }

    private BlobClient CreateBlobClient(Uri blobUri)
    {
        if (!string.IsNullOrWhiteSpace(_options.StorageConnectionString))
        {
            var (containerName, blobName) = ParseContainerAndBlob(blobUri);
            return new BlobClient(_options.StorageConnectionString, containerName, blobName);
        }

        return new BlobClient(blobUri);
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.ServiceBusConnectionString))
        {
            throw new InvalidOperationException("OpenDataLoader worker requires ServiceBus or ServiceBus:ConnectionString.");
        }
    }

    private static (string ContainerName, string BlobName) ParseContainerAndBlob(Uri blobUri)
    {
        var path = blobUri.AbsolutePath.Trim('/');
        var firstSlash = path.IndexOf('/', StringComparison.Ordinal);

        if (firstSlash <= 0 || firstSlash == path.Length - 1)
        {
            throw new InvalidOperationException(
                $"Blob URL path did not look like '/<container>/<blob>': '{blobUri.AbsolutePath}'.");
        }

        return (path[..firstSlash], path[(firstSlash + 1)..]);
    }

    private void CleanupWorkDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temporary directory {tempPath}", path);
        }
    }
}
