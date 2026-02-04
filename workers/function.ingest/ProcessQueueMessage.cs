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

    public ProcessQueueMessage(ILogger<ProcessQueueMessage> logger, IFileIngestProcessor fileIngestProcessor)
    {
        _logger = logger;
        _fileIngestProcessor = fileIngestProcessor;
    }

    [Function(nameof(ProcessQueueMessage))]
    public async Task Run(
        [ServiceBusTrigger("files", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            nameof(ProcessQueueMessage),
            ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "azure.servicebus");
        activity?.SetTag("messaging.destination.name", "files");
        activity?.SetTag("messaging.message.id", message.MessageId);

        using var messageScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["messaging.system"] = "azure.servicebus",
            ["messaging.destination.name"] = "files",
            ["messaging.message.id"] = message.MessageId
        });

        var body = message.Body.ToString();
        _logger.LogInformation("Message ID: {id}", message.MessageId);
        _logger.LogInformation("Message Content-Type: {contentType}", message.ContentType);

        if (!StorageBlobCreatedEventParser.TryParse(body, out var request, out var error))
        {
            _logger.LogError("Failed to parse CloudEvent message: {error}. Body: {body}", error, body);
            throw new InvalidOperationException(error);
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

        await _fileIngestProcessor.ProcessAsync(request, cancellationToken);

        // Complete the message
        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }
}
