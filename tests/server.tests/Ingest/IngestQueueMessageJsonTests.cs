using System.Text.Json;
using FluentAssertions;
using server.core.Ingest;

namespace server.tests.Ingest;

public class IngestQueueMessageJsonTests
{
    [Fact]
    public void AutotagJobMessage_RoundTrips()
    {
        var message = new AutotagJobMessage(
            FileId: Guid.NewGuid().ToString(),
            AttemptId: 12,
            Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
            SourceBlobUri: new Uri("https://example.blob.core.windows.net/incoming/source.pdf"),
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/source.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "source.pdf",
            OutputTaggedPdfBlobUri: new Uri("https://example.blob.core.windows.net/temp/file/12/opendataloader.tagged.pdf"),
            OutputReportBlobUri: new Uri("https://example.blob.core.windows.net/reports/file/12/opendataloader.autotag-report.json"),
            PageCount: 5,
            CorrelationId: Guid.NewGuid().ToString("N"),
            EnqueuedAt: DateTimeOffset.UtcNow);

        var roundTrip = IngestQueueMessageJson.Deserialize<AutotagJobMessage>(
            IngestQueueMessageJson.Serialize(message));

        roundTrip.Should().Be(message);
    }

    [Fact]
    public void FinalizePdfMessage_RoundTrips()
    {
        var message = new FinalizePdfMessage(
            FileId: Guid.NewGuid().ToString(),
            AttemptId: 12,
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/source.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "source.pdf",
            PdfToFinalizeBlobUri: new Uri("https://example.blob.core.windows.net/temp/file/12/opendataloader.tagged.pdf"),
            PageCount: 5,
            Autotag: new PdfAutotagMessageMetadata(
                FileIngestOptions.AutotagProviders.OpenDataLoader,
                Required: true,
                SkippedReason: null,
                ChunkCount: 1,
                ReportUris: ["https://example.blob.core.windows.net/reports/file/12/report.json"]),
            CorrelationId: Guid.NewGuid().ToString("N"),
            EnqueuedAt: DateTimeOffset.UtcNow);

        var roundTrip = IngestQueueMessageJson.Deserialize<FinalizePdfMessage>(
            IngestQueueMessageJson.Serialize(message));

        roundTrip.FileId.Should().Be(message.FileId);
        roundTrip.AttemptId.Should().Be(message.AttemptId);
        roundTrip.PdfToFinalizeBlobUri.Should().Be(message.PdfToFinalizeBlobUri);
        roundTrip.Autotag.Should().BeEquivalentTo(message.Autotag);
        roundTrip.CorrelationId.Should().Be(message.CorrelationId);
    }

    [Fact]
    public void AutotagFailedMessage_RoundTrips()
    {
        var message = new AutotagFailedMessage(
            FileId: Guid.NewGuid().ToString(),
            AttemptId: 12,
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/source.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "source.pdf",
            Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
            ErrorCode: "InvalidOperationException",
            ErrorMessage: "ODL failed.",
            ErrorDetails: "details",
            DeliveryCount: 10,
            CorrelationId: Guid.NewGuid().ToString("N"),
            FailedAt: DateTimeOffset.UtcNow);

        var roundTrip = IngestQueueMessageJson.Deserialize<AutotagFailedMessage>(
            IngestQueueMessageJson.Serialize(message));

        roundTrip.Should().Be(message);
    }

    [Theory]
    [InlineData(typeof(AutotagJobMessage))]
    [InlineData(typeof(FinalizePdfMessage))]
    [InlineData(typeof(AutotagFailedMessage))]
    public void Deserialize_WhenRequiredFieldsAreMissing_Throws(Type messageType)
    {
        var deserialize = typeof(IngestQueueMessageJson)
            .GetMethod(nameof(IngestQueueMessageJson.Deserialize))!
            .MakeGenericMethod(messageType);

        Action act = () => deserialize.Invoke(null, ["{}"]);

        act.Should()
            .Throw<Exception>()
            .Where(ex => ex.InnerException is InvalidOperationException);
    }

    [Fact]
    public void Deserialize_WhenJsonIsMalformed_Throws()
    {
        Action act = () => IngestQueueMessageJson.Deserialize<AutotagJobMessage>("{nope");

        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("sourceBlobUri")]
    [InlineData("originalBlobUri")]
    [InlineData("outputTaggedPdfBlobUri")]
    [InlineData("outputReportBlobUri")]
    public void Deserialize_WhenAutotagJobBlobUriIsRelative_Throws(string propertyName)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fileId"] = Guid.NewGuid().ToString(),
            ["attemptId"] = 12,
            ["provider"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
            ["sourceBlobUri"] = "https://example.blob.core.windows.net/incoming/source.pdf",
            ["originalBlobUri"] = "https://example.blob.core.windows.net/incoming/source.pdf",
            ["originalContainerName"] = "incoming",
            ["originalBlobName"] = "source.pdf",
            ["outputTaggedPdfBlobUri"] = "https://example.blob.core.windows.net/temp/source.tagged.pdf",
            ["outputReportBlobUri"] = "https://example.blob.core.windows.net/reports/source.json",
            ["pageCount"] = 5,
            ["correlationId"] = Guid.NewGuid().ToString("N"),
            ["enqueuedAt"] = DateTimeOffset.UtcNow,
            [propertyName] = "incoming/source.pdf",
        });

        Action act = () => IngestQueueMessageJson.Deserialize<AutotagJobMessage>(json);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*must be an absolute URI*");
    }

    [Theory]
    [InlineData("originalBlobUri")]
    [InlineData("pdfToFinalizeBlobUri")]
    public void Deserialize_WhenFinalizePdfBlobUriIsRelative_Throws(string propertyName)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fileId"] = Guid.NewGuid().ToString(),
            ["attemptId"] = 12,
            ["originalBlobUri"] = "https://example.blob.core.windows.net/incoming/source.pdf",
            ["originalContainerName"] = "incoming",
            ["originalBlobName"] = "source.pdf",
            ["pdfToFinalizeBlobUri"] = "https://example.blob.core.windows.net/temp/source.tagged.pdf",
            ["pageCount"] = 5,
            ["autotag"] = new Dictionary<string, object?>
            {
                ["provider"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
                ["required"] = true,
                ["chunkCount"] = 1,
                ["reportUris"] = new[] { "https://example.blob.core.windows.net/reports/source.json" },
            },
            ["correlationId"] = Guid.NewGuid().ToString("N"),
            ["enqueuedAt"] = DateTimeOffset.UtcNow,
            [propertyName] = "/incoming/source.pdf",
        });

        Action act = () => IngestQueueMessageJson.Deserialize<FinalizePdfMessage>(json);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*must be an absolute URI*");
    }

    [Fact]
    public void Deserialize_WhenAutotagFailedOriginalBlobUriIsRelative_Throws()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fileId"] = Guid.NewGuid().ToString(),
            ["attemptId"] = 12,
            ["originalBlobUri"] = "incoming/source.pdf",
            ["originalContainerName"] = "incoming",
            ["originalBlobName"] = "source.pdf",
            ["provider"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
            ["errorCode"] = "InvalidOperationException",
            ["errorMessage"] = "ODL failed.",
            ["deliveryCount"] = 10,
            ["correlationId"] = Guid.NewGuid().ToString("N"),
            ["failedAt"] = DateTimeOffset.UtcNow,
        });

        Action act = () => IngestQueueMessageJson.Deserialize<AutotagFailedMessage>(json);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*must be an absolute URI*");
    }
}
