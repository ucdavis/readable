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

public class FailedQueueMessage
{
    private readonly ILogger<FailedQueueMessage> _logger;
    private readonly IFileIngestProcessor _fileIngestProcessor;
    private readonly string _queueName;

    public FailedQueueMessage(
        ILogger<FailedQueueMessage> logger,
        IFileIngestProcessor fileIngestProcessor,
        IngestQueueOptions queueOptions)
    {
        _logger = logger;
        _fileIngestProcessor = fileIngestProcessor;
        _queueName = queueOptions.FailedQueueName;
    }

    [Function(nameof(FailedQueueMessage))]
    public async Task Run(
        [ServiceBusTrigger("%INGEST_FAILED_QUEUE_NAME%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            nameof(FailedQueueMessage),
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
        var failedMessage = IngestQueueMessageJson.Deserialize<AutotagFailedMessage>(body);

        activity?.SetTag("file.id", failedMessage.FileId);
        activity?.SetTag("autotag.provider", failedMessage.Provider);
        activity?.SetTag("error.type", failedMessage.ErrorCode);

        using var fileScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["file.id"] = failedMessage.FileId,
            ["autotag.provider"] = failedMessage.Provider,
            ["error.type"] = failedMessage.ErrorCode
        });

        try
        {
            await _fileIngestProcessor.FailAsync(failedMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            await ServiceBusMessageSettlement.AbandonMessageAsync(message, messageActions, _logger, ex);
            throw;
        }

        await ServiceBusMessageSettlement.CompleteMessageAsync(message, messageActions, _logger, cancellationToken);
    }
}
