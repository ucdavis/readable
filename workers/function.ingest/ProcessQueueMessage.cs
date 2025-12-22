using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

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
        _logger.LogInformation("Message ID: {id}", message.MessageId);
        _logger.LogInformation("Message Body: {body}", message.Body);
        _logger.LogInformation("Message Content-Type: {contentType}", message.ContentType);

        // Complete the message
        await messageActions.CompleteMessageAsync(message);
    }
}
