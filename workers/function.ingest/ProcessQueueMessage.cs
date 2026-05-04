using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using server.core.Ingest;
using server.core.Telemetry;

namespace function.ingest;

public class ProcessQueueMessage
{
    private readonly ILogger<ProcessQueueMessage> _logger;
    private readonly IFileIngestProcessor _fileIngestProcessor;
    private readonly string _queueName;

    public ProcessQueueMessage(
        ILogger<ProcessQueueMessage> logger,
        IFileIngestProcessor fileIngestProcessor,
        IngestQueueOptions queueOptions)
    {
        _logger = logger;
        _fileIngestProcessor = fileIngestProcessor;
        _queueName = queueOptions.FilesQueueName;
    }

    [Function(nameof(ProcessQueueMessage))]
    public async Task Run(
        [ServiceBusTrigger("%INGEST_FILES_QUEUE_NAME%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            nameof(ProcessQueueMessage),
            ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "azure.servicebus");
        activity?.SetTag("messaging.destination.name", _queueName);
        activity?.SetTag("messaging.message.id", message.MessageId);

        using var messageScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["messaging.system"] = "azure.servicebus",
            ["messaging.destination.name"] = _queueName,
            ["messaging.message.id"] = message.MessageId
        });

        var body = message.Body.ToString();
        _logger.LogInformation("Message ID: {id}", message.MessageId);
        _logger.LogInformation("Message Content-Type: {contentType}", message.ContentType);

        if (!StorageBlobCreatedEventParser.TryParse(body, out var request, out var error))
        {
            var parseError = error ?? "Invalid CloudEvent.";
            _logger.LogError(
                "Failed to parse CloudEvent message {messageId}. contentType={contentType}; deliveryCount={deliveryCount}; error={error}",
                message.MessageId,
                message.ContentType,
                message.DeliveryCount,
                parseError);

            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "InvalidCloudEvent",
                deadLetterErrorDescription: parseError,
                cancellationToken: cancellationToken);
            return;
        }

        activity?.SetTag("url.full", request.BlobUri.ToString());
        activity?.SetTag("blob.container", request.ContainerName);
        activity?.SetTag("blob.name", request.BlobName);
        activity?.SetTag("file.id", request.FileId);

        using var fileScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["file.id"] = request.FileId,
            ["blob.container"] = request.ContainerName,
            ["blob.name"] = request.BlobName,
            ["url.full"] = request.BlobUri.ToString()
        });

        try
        {
            await _fileIngestProcessor.ProcessAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            await ServiceBusMessageSettlement.AbandonMessageAsync(message, messageActions, _logger, ex);
            throw;
        }

        await ServiceBusMessageSettlement.CompleteMessageAsync(message, messageActions, _logger, cancellationToken);
    }
}
