using Azure.Messaging.ServiceBus;
using function.ingest;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Ingest;

namespace server.tests.Workers;

public class ProcessQueueMessageTests
{
    [Fact]
    public async Task Run_WhenCloudEventIsMalformed_DeadLettersAndDoesNotProcess()
    {
        var processor = new CapturingFileIngestProcessor();
        var actions = new RecordingServiceBusMessageActions();
        var handler = CreateHandler(processor);
        var message = CreateMessage("{not-json", deliveryCount: 2);

        await handler.Run(message, actions, CancellationToken.None);

        processor.ProcessCalls.Should().Be(0);
        actions.DeadLetterCalls.Should().Be(1);
        actions.DeadLetterReason.Should().Be("InvalidCloudEvent");
        actions.CompleteCalls.Should().Be(0);
        actions.AbandonCalls.Should().Be(0);
    }

    [Fact]
    public async Task Run_WhenProcessingFails_AbandonsAndRethrows()
    {
        var processor = new CapturingFileIngestProcessor
        {
            ProcessException = new InvalidOperationException("processing failed"),
        };
        var actions = new RecordingServiceBusMessageActions();
        var handler = CreateHandler(processor);
        var message = CreateMessage(CreateBlobCreatedCloudEvent());

        var act = async () => await handler.Run(message, actions, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("processing failed");
        processor.ProcessCalls.Should().Be(1);
        actions.AbandonCalls.Should().Be(1);
        actions.CompleteCalls.Should().Be(0);
        actions.DeadLetterCalls.Should().Be(0);
    }

    [Fact]
    public async Task Run_WhenCompleteFailsAfterProcessing_DoesNotAbandon()
    {
        var processor = new CapturingFileIngestProcessor();
        var actions = new RecordingServiceBusMessageActions
        {
            CompleteException = new InvalidOperationException("complete failed"),
        };
        var handler = CreateHandler(processor);
        var message = CreateMessage(CreateBlobCreatedCloudEvent());

        var act = async () => await handler.Run(message, actions, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("complete failed");
        processor.ProcessCalls.Should().Be(1);
        actions.CompleteCalls.Should().Be(1);
        actions.AbandonCalls.Should().Be(0);
        actions.DeadLetterCalls.Should().Be(0);
    }

    private static ProcessQueueMessage CreateHandler(CapturingFileIngestProcessor processor)
    {
        return new ProcessQueueMessage(
            NullLogger<ProcessQueueMessage>.Instance,
            processor,
            new IngestQueueOptions(
                FilesQueueName: "files",
                OpenDataLoaderAutotagQueueName: "autotag",
                FinalizeQueueName: "finalize",
                FailedQueueName: "failed"));
    }

    private static ServiceBusReceivedMessage CreateMessage(string body, int deliveryCount = 1)
    {
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(body),
            messageId: "message-1",
            contentType: "application/json",
            deliveryCount: deliveryCount);
    }

    private static string CreateBlobCreatedCloudEvent()
    {
        return """
            {
              "specversion": "1.0",
              "type": "Microsoft.Storage.BlobCreated",
              "source": "/subscriptions/test/resourceGroups/test/providers/Microsoft.Storage/storageAccounts/account",
              "id": "event-1",
              "time": "2026-05-03T00:00:00Z",
              "data": {
                "url": "https://account.blob.core.windows.net/incoming/file-1.pdf"
              }
            }
            """;
    }

    private sealed class CapturingFileIngestProcessor : IFileIngestProcessor
    {
        public int ProcessCalls { get; private set; }

        public Exception? ProcessException { get; init; }

        public Task ProcessAsync(BlobIngestRequest request, CancellationToken cancellationToken)
        {
            ProcessCalls++;
            if (ProcessException is not null)
            {
                throw ProcessException;
            }

            return Task.CompletedTask;
        }

        public Task FinalizeAsync(FinalizePdfMessage message, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task FailAsync(AutotagFailedMessage message, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingServiceBusMessageActions : ServiceBusMessageActions
    {
        public int CompleteCalls { get; private set; }

        public int AbandonCalls { get; private set; }

        public int DeadLetterCalls { get; private set; }

        public string? DeadLetterReason { get; private set; }

        public Exception? CompleteException { get; init; }

        public override Task CompleteMessageAsync(
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            if (CompleteException is not null)
            {
                throw CompleteException;
            }

            return Task.CompletedTask;
        }

        public override Task AbandonMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify = null,
            CancellationToken cancellationToken = default)
        {
            AbandonCalls++;
            return Task.CompletedTask;
        }

        public override Task DeadLetterMessageAsync(
            ServiceBusReceivedMessage message,
            Dictionary<string, object>? propertiesToModify = null,
            string? deadLetterReason = null,
            string? deadLetterErrorDescription = null,
            CancellationToken cancellationToken = default)
        {
            DeadLetterCalls++;
            DeadLetterReason = deadLetterReason;
            return Task.CompletedTask;
        }
    }
}
