using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using server.core.Ingest;
using server.core.Telemetry;

namespace function.ingest;

public class FinalizeQueueMessage
{
    private readonly ILogger<FinalizeQueueMessage> _logger;
    private readonly IFileIngestProcessor _fileIngestProcessor;
    private readonly string _queueName;

    public FinalizeQueueMessage(
        ILogger<FinalizeQueueMessage> logger,
        IFileIngestProcessor fileIngestProcessor,
        IngestQueueOptions queueOptions)
    {
        _logger = logger;
        _fileIngestProcessor = fileIngestProcessor;
        _queueName = queueOptions.FinalizeQueueName;
    }

    [Function(nameof(FinalizeQueueMessage))]
    public async Task Run(
        [ServiceBusTrigger("%INGEST_FINALIZE_QUEUE_NAME%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            nameof(FinalizeQueueMessage),
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
        var finalizeMessage = IngestQueueMessageJson.Deserialize<FinalizePdfMessage>(body);

        activity?.SetTag("file.id", finalizeMessage.FileId);
        activity?.SetTag("url.full", finalizeMessage.PdfToFinalizeBlobUri.ToString());
        activity?.SetTag("autotag.provider", finalizeMessage.Autotag.Provider);

        using var fileScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["file.id"] = finalizeMessage.FileId,
            ["url.full"] = finalizeMessage.PdfToFinalizeBlobUri.ToString(),
            ["autotag.provider"] = finalizeMessage.Autotag.Provider
        });

        await _fileIngestProcessor.FinalizeAsync(finalizeMessage, cancellationToken);

        await messageActions.CompleteMessageAsync(message, cancellationToken);
    }
}
