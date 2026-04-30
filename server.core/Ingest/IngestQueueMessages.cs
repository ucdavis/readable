using System.Text.Json;
using System.Text.Json.Serialization;

namespace server.core.Ingest;

public sealed record AutotagJobMessage(
    string FileId,
    long AttemptId,
    string Provider,
    Uri SourceBlobUri,
    Uri OriginalBlobUri,
    string OriginalContainerName,
    string OriginalBlobName,
    Uri OutputTaggedPdfBlobUri,
    Uri OutputReportBlobUri,
    int PageCount,
    string CorrelationId,
    DateTimeOffset EnqueuedAt);

public sealed record FinalizePdfMessage(
    string FileId,
    long AttemptId,
    Uri OriginalBlobUri,
    string OriginalContainerName,
    string OriginalBlobName,
    Uri PdfToFinalizeBlobUri,
    int PageCount,
    PdfAutotagMessageMetadata Autotag,
    string CorrelationId,
    DateTimeOffset EnqueuedAt);

public sealed record AutotagFailedMessage(
    string FileId,
    long AttemptId,
    Uri OriginalBlobUri,
    string OriginalContainerName,
    string OriginalBlobName,
    string Provider,
    string ErrorCode,
    string ErrorMessage,
    string? ErrorDetails,
    int DeliveryCount,
    string CorrelationId,
    DateTimeOffset FailedAt);

public sealed record PdfAutotagMessageMetadata(
    string Provider,
    bool Required,
    string? SkippedReason,
    int ChunkCount,
    IReadOnlyList<string> ReportUris)
{
    public static PdfAutotagMessageMetadata FromResult(PdfAutotagMetadata metadata)
    {
        return new PdfAutotagMessageMetadata(
            metadata.Provider,
            metadata.Required,
            metadata.SkippedReason,
            metadata.ChunkCount,
            metadata.LocalReportPaths);
    }

    public PdfAutotagMetadata ToResultMetadata()
    {
        return new PdfAutotagMetadata(
            Provider,
            Required,
            SkippedReason,
            ChunkCount,
            ReportUris);
    }
}

public static class IngestQueueMessageJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, Options);
        return value ?? throw new InvalidOperationException($"Message body did not contain a valid {typeof(T).Name}.");
    }
}
