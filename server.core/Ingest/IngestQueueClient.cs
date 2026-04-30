using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;

namespace server.core.Ingest;

public interface IIngestQueueClient
{
    Task EnqueueAutotagJobAsync(AutotagJobMessage message, CancellationToken cancellationToken);

    Task EnqueueFinalizePdfAsync(FinalizePdfMessage message, CancellationToken cancellationToken);
}

public sealed record IngestQueueOptions(
    string FilesQueueName,
    string OpenDataLoaderAutotagQueueName,
    string FinalizeQueueName)
{
    public const string DefaultFilesQueueName = "files";
    public const string DefaultOpenDataLoaderAutotagQueueName = "autotag-odl";
    public const string DefaultFinalizeQueueName = "pdf-finalize";

    public static IngestQueueOptions FromConfiguration(IConfiguration configuration)
    {
        return new IngestQueueOptions(
            FilesQueueName: GetQueueName(
                configuration,
                "Ingest:FilesQueueName",
                "INGEST_FILES_QUEUE_NAME",
                DefaultFilesQueueName),
            OpenDataLoaderAutotagQueueName: GetQueueName(
                configuration,
                "Ingest:OpenDataLoaderAutotagQueueName",
                "INGEST_ODL_AUTOTAG_QUEUE_NAME",
                DefaultOpenDataLoaderAutotagQueueName),
            FinalizeQueueName: GetQueueName(
                configuration,
                "Ingest:FinalizeQueueName",
                "INGEST_FINALIZE_QUEUE_NAME",
                DefaultFinalizeQueueName));
    }

    private static string GetQueueName(
        IConfiguration configuration,
        string primaryKey,
        string legacyKey,
        string fallback)
    {
        var value = configuration[primaryKey] ?? configuration[legacyKey];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

public sealed class AzureIngestQueueClient : IIngestQueueClient, IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ServiceBusSender _autotagSender;
    private readonly ServiceBusSender _finalizeSender;

    public AzureIngestQueueClient(ServiceBusClient serviceBusClient, IngestQueueOptions options)
    {
        _serviceBusClient = serviceBusClient;
        _autotagSender = serviceBusClient.CreateSender(options.OpenDataLoaderAutotagQueueName);
        _finalizeSender = serviceBusClient.CreateSender(options.FinalizeQueueName);
    }

    public Task EnqueueAutotagJobAsync(AutotagJobMessage message, CancellationToken cancellationToken)
    {
        return SendAsync(_autotagSender, message.FileId, message.CorrelationId, message, cancellationToken);
    }

    public Task EnqueueFinalizePdfAsync(FinalizePdfMessage message, CancellationToken cancellationToken)
    {
        return SendAsync(_finalizeSender, message.FileId, message.CorrelationId, message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _autotagSender.DisposeAsync();
        await _finalizeSender.DisposeAsync();
        await _serviceBusClient.DisposeAsync();
    }

    private static Task SendAsync<T>(
        ServiceBusSender sender,
        string fileId,
        string correlationId,
        T message,
        CancellationToken cancellationToken)
    {
        var body = IngestQueueMessageJson.Serialize(message);
        var serviceBusMessage = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            CorrelationId = correlationId,
            MessageId = $"{typeof(T).Name}:{fileId}:{correlationId}",
        };

        serviceBusMessage.ApplicationProperties["fileId"] = fileId;
        serviceBusMessage.ApplicationProperties["messageType"] = typeof(T).Name;

        return sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }
}

public sealed class DisabledIngestQueueClient : IIngestQueueClient
{
    public Task EnqueueAutotagJobAsync(AutotagJobMessage message, CancellationToken cancellationToken)
    {
        throw CreateDisabledException();
    }

    public Task EnqueueFinalizePdfAsync(FinalizePdfMessage message, CancellationToken cancellationToken)
    {
        throw CreateDisabledException();
    }

    private static InvalidOperationException CreateDisabledException()
    {
        return new InvalidOperationException(
            "Service Bus is not configured for ingest queueing. Set ServiceBus or ServiceBus:ConnectionString.");
    }
}
