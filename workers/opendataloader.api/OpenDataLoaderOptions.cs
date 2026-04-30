using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace opendataloader.api;

public sealed class OpenDataLoaderOptions
{
    private const int DefaultProcessTimeoutSeconds = 210;
    private const int DefaultMaxConcurrentConversions = 1;
    private const int MaxErrorLength = 4000;

    public int ProcessTimeoutSeconds { get; init; } = DefaultProcessTimeoutSeconds;
    public int MaxConcurrentConversions { get; init; } = DefaultMaxConcurrentConversions;
    public string CommandPath { get; init; } = "opendataloader-pdf";
    public string OutputFormat { get; init; } = "tagged-pdf";
    public string? HybridUrl { get; init; }
    public string HybridBackend { get; init; } = "docling-fast";
    public string ServiceBusConnectionString { get; init; } = string.Empty;
    public string StorageConnectionString { get; init; } = string.Empty;
    public string AutotagQueueName { get; init; } = "autotag-odl";
    public string FinalizeQueueName { get; init; } = "pdf-finalize";

    public TimeSpan ProcessTimeout => TimeSpan.FromSeconds(ProcessTimeoutSeconds);

    public static OpenDataLoaderOptions FromConfiguration(IConfiguration configuration)
    {
        static int GetInt(
            IConfiguration configuration,
            string primaryKey,
            string legacyKey,
            int fallback,
            int minValue = 1)
        {
            var raw = configuration[primaryKey] ?? configuration[legacyKey];
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minValue
                ? value
                : fallback;
        }

        var outputFormat = configuration["OpenDataLoader:OutputFormat"]
                           ?? configuration["ODL_OUTPUT_FORMAT"]
                           ?? "tagged-pdf";

        var hybridBackend = configuration["OpenDataLoader:HybridBackend"]
                            ?? configuration["ODL_HYBRID_BACKEND"]
                            ?? "docling-fast";

        return new OpenDataLoaderOptions
        {
            ProcessTimeoutSeconds = GetInt(
                configuration,
                "OpenDataLoader:ProcessTimeoutSeconds",
                "ODL_PROCESS_TIMEOUT_SECONDS",
                DefaultProcessTimeoutSeconds),
            MaxConcurrentConversions = GetInt(
                configuration,
                "OpenDataLoader:MaxConcurrentConversions",
                "ODL_MAX_CONCURRENT_CONVERSIONS",
                DefaultMaxConcurrentConversions),
            CommandPath = configuration["OpenDataLoader:CommandPath"]
                          ?? configuration["ODL_COMMAND_PATH"]
                          ?? "opendataloader-pdf",
            OutputFormat = string.IsNullOrWhiteSpace(outputFormat) ? "tagged-pdf" : outputFormat.Trim(),
            HybridUrl = configuration["OpenDataLoader:HybridUrl"]
                        ?? configuration["ODL_HYBRID_URL"],
            HybridBackend = string.IsNullOrWhiteSpace(hybridBackend) ? "docling-fast" : hybridBackend.Trim(),
            ServiceBusConnectionString =
                configuration["ServiceBus"]
                ?? configuration["ServiceBus:ConnectionString"]
                ?? configuration["ServiceBus__ConnectionString"]
                ?? string.Empty,
            StorageConnectionString =
                configuration["Storage:ConnectionString"]
                ?? configuration["Storage__ConnectionString"]
                ?? string.Empty,
            AutotagQueueName = GetString(
                configuration,
                "OpenDataLoader:AutotagQueueName",
                "ODL_AUTOTAG_QUEUE_NAME",
                "autotag-odl"),
            FinalizeQueueName = GetString(
                configuration,
                "OpenDataLoader:FinalizeQueueName",
                "ODL_FINALIZE_QUEUE_NAME",
                "pdf-finalize")
        };
    }

    private static string GetString(
        IConfiguration configuration,
        string primaryKey,
        string legacyKey,
        string fallback)
    {
        var value = configuration[primaryKey] ?? configuration[legacyKey];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public string SanitizeError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Conversion failed.";
        }

        var flattened = value.Replace("\r", string.Empty).Trim();
        return flattened.Length <= MaxErrorLength
            ? flattened
            : flattened[^MaxErrorLength..];
    }

}
