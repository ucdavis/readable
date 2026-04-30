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
        if (value is null)
        {
            throw new InvalidOperationException($"Message body did not contain a valid {typeof(T).Name}.");
        }

        Validate(value);
        return value;
    }

    private static void Validate<T>(T value)
    {
        switch (value)
        {
            case AutotagJobMessage message:
                Require(message.FileId, nameof(AutotagJobMessage.FileId), nameof(AutotagJobMessage));
                RequirePositive(message.AttemptId, nameof(AutotagJobMessage.AttemptId), nameof(AutotagJobMessage));
                Require(message.Provider, nameof(AutotagJobMessage.Provider), nameof(AutotagJobMessage));
                RequireAbsoluteUri(message.SourceBlobUri, nameof(AutotagJobMessage.SourceBlobUri), nameof(AutotagJobMessage));
                RequireAbsoluteUri(message.OriginalBlobUri, nameof(AutotagJobMessage.OriginalBlobUri), nameof(AutotagJobMessage));
                Require(message.OriginalContainerName, nameof(AutotagJobMessage.OriginalContainerName), nameof(AutotagJobMessage));
                Require(message.OriginalBlobName, nameof(AutotagJobMessage.OriginalBlobName), nameof(AutotagJobMessage));
                RequireAbsoluteUri(message.OutputTaggedPdfBlobUri, nameof(AutotagJobMessage.OutputTaggedPdfBlobUri), nameof(AutotagJobMessage));
                RequireAbsoluteUri(message.OutputReportBlobUri, nameof(AutotagJobMessage.OutputReportBlobUri), nameof(AutotagJobMessage));
                Require(message.CorrelationId, nameof(AutotagJobMessage.CorrelationId), nameof(AutotagJobMessage));
                break;

            case FinalizePdfMessage message:
                Require(message.FileId, nameof(FinalizePdfMessage.FileId), nameof(FinalizePdfMessage));
                RequirePositive(message.AttemptId, nameof(FinalizePdfMessage.AttemptId), nameof(FinalizePdfMessage));
                RequireAbsoluteUri(message.OriginalBlobUri, nameof(FinalizePdfMessage.OriginalBlobUri), nameof(FinalizePdfMessage));
                Require(message.OriginalContainerName, nameof(FinalizePdfMessage.OriginalContainerName), nameof(FinalizePdfMessage));
                Require(message.OriginalBlobName, nameof(FinalizePdfMessage.OriginalBlobName), nameof(FinalizePdfMessage));
                RequireAbsoluteUri(message.PdfToFinalizeBlobUri, nameof(FinalizePdfMessage.PdfToFinalizeBlobUri), nameof(FinalizePdfMessage));
                Require(message.Autotag, nameof(FinalizePdfMessage.Autotag), nameof(FinalizePdfMessage));
                Require(message.Autotag.Provider, $"{nameof(FinalizePdfMessage.Autotag)}.{nameof(PdfAutotagMessageMetadata.Provider)}", nameof(FinalizePdfMessage));
                Require(message.Autotag.ReportUris, $"{nameof(FinalizePdfMessage.Autotag)}.{nameof(PdfAutotagMessageMetadata.ReportUris)}", nameof(FinalizePdfMessage));
                Require(message.CorrelationId, nameof(FinalizePdfMessage.CorrelationId), nameof(FinalizePdfMessage));
                break;

            case AutotagFailedMessage message:
                Require(message.FileId, nameof(AutotagFailedMessage.FileId), nameof(AutotagFailedMessage));
                RequirePositive(message.AttemptId, nameof(AutotagFailedMessage.AttemptId), nameof(AutotagFailedMessage));
                RequireAbsoluteUri(message.OriginalBlobUri, nameof(AutotagFailedMessage.OriginalBlobUri), nameof(AutotagFailedMessage));
                Require(message.OriginalContainerName, nameof(AutotagFailedMessage.OriginalContainerName), nameof(AutotagFailedMessage));
                Require(message.OriginalBlobName, nameof(AutotagFailedMessage.OriginalBlobName), nameof(AutotagFailedMessage));
                Require(message.Provider, nameof(AutotagFailedMessage.Provider), nameof(AutotagFailedMessage));
                Require(message.ErrorCode, nameof(AutotagFailedMessage.ErrorCode), nameof(AutotagFailedMessage));
                Require(message.ErrorMessage, nameof(AutotagFailedMessage.ErrorMessage), nameof(AutotagFailedMessage));
                RequirePositive(message.DeliveryCount, nameof(AutotagFailedMessage.DeliveryCount), nameof(AutotagFailedMessage));
                Require(message.CorrelationId, nameof(AutotagFailedMessage.CorrelationId), nameof(AutotagFailedMessage));
                break;
        }
    }

    private static void Require(string? value, string propertyName, string messageName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{messageName}.{propertyName} is required.");
        }
    }

    private static void Require(Uri? value, string propertyName, string messageName)
    {
        if (value is null)
        {
            throw new InvalidOperationException($"{messageName}.{propertyName} is required.");
        }
    }

    private static void RequireAbsoluteUri(Uri? value, string propertyName, string messageName)
    {
        Require(value, propertyName, messageName);

        if (!value!.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"{messageName}.{propertyName} must be an absolute URI.");
        }
    }

    private static void Require(object? value, string propertyName, string messageName)
    {
        if (value is null)
        {
            throw new InvalidOperationException($"{messageName}.{propertyName} is required.");
        }
    }

    private static void RequirePositive(long value, string propertyName, string messageName)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{messageName}.{propertyName} must be greater than zero.");
        }
    }
}
