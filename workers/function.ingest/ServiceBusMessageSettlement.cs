using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace function.ingest;

internal static class ServiceBusMessageSettlement
{
    public static async Task CompleteMessageAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to complete Service Bus message {messageId}; not abandoning after successful processing.",
                message.MessageId);
            throw;
        }
    }

    public static async Task AbandonMessageAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ILogger logger,
        Exception exception)
    {
        try
        {
            await messageActions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch (Exception abandonException)
        {
            logger.LogWarning(
                abandonException,
                "Failed to abandon Service Bus message {messageId} after processing error {errorType}",
                message.MessageId,
                exception.GetType().Name);
        }
    }
}
