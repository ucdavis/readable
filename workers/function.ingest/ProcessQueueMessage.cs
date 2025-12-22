using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using server.core.Telemetry;

namespace function.ingest;

public class ProcessQueueMessage
{
    private readonly ILogger<ProcessQueueMessage> _logger;

    public ProcessQueueMessage(ILogger<ProcessQueueMessage> logger)
    {
        _logger = logger;
    }

    [Function(nameof(ProcessQueueMessage))]
    public async Task Run(
        [ServiceBusTrigger("files", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        using var activity = TelemetryHelper.ActivitySource.StartActivity(
            nameof(ProcessQueueMessage),
            ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "azure.servicebus");
        activity?.SetTag("messaging.destination.name", "files");
        activity?.SetTag("messaging.message.id", message.MessageId);

        _logger.LogInformation("Message ID: {id}", message.MessageId);
        _logger.LogInformation("Message Body: {body}", message.Body);
        _logger.LogInformation("Message Content-Type: {contentType}", message.ContentType);

        // Complete the message
        await messageActions.CompleteMessageAsync(message);
    }
}
