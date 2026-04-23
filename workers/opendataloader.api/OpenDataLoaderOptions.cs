using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace opendataloader.api;

public sealed class OpenDataLoaderOptions
{
    private const int DefaultMaxRequestBodySizeMb = 50;
    private const int DefaultProcessTimeoutSeconds = 210;
    private const int MaxErrorLength = 4000;

    public string SharedSecret { get; init; } = string.Empty;
    public int MaxRequestBodySizeMb { get; init; } = DefaultMaxRequestBodySizeMb;
    public int ProcessTimeoutSeconds { get; init; } = DefaultProcessTimeoutSeconds;
    public string CommandPath { get; init; } = "opendataloader-pdf";
    public string OutputFormat { get; init; } = "tagged-pdf";
    public string? HybridUrl { get; init; }
    public string HybridBackend { get; init; } = "docling-fast";

    public long MaxRequestBodySizeBytes => MaxRequestBodySizeMb * 1024L * 1024L;
    public TimeSpan ProcessTimeout => TimeSpan.FromSeconds(ProcessTimeoutSeconds);

    public static OpenDataLoaderOptions FromConfiguration(IConfiguration configuration)
    {
        static int GetInt(IConfiguration configuration, string primaryKey, string legacyKey, int fallback)
        {
            var raw = configuration[primaryKey] ?? configuration[legacyKey];
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
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
            SharedSecret = configuration["OpenDataLoader:SharedSecret"]
                           ?? configuration["ODL_SHARED_SECRET"]
                           ?? configuration["ODL_API_KEY"]
                           ?? string.Empty,
            MaxRequestBodySizeMb = GetInt(
                configuration,
                "OpenDataLoader:MaxRequestBodySizeMb",
                "ODL_MAX_REQUEST_BODY_SIZE_MB",
                DefaultMaxRequestBodySizeMb),
            ProcessTimeoutSeconds = GetInt(
                configuration,
                "OpenDataLoader:ProcessTimeoutSeconds",
                "ODL_PROCESS_TIMEOUT_SECONDS",
                DefaultProcessTimeoutSeconds),
            CommandPath = configuration["OpenDataLoader:CommandPath"]
                          ?? configuration["ODL_COMMAND_PATH"]
                          ?? "opendataloader-pdf",
            OutputFormat = string.IsNullOrWhiteSpace(outputFormat) ? "tagged-pdf" : outputFormat.Trim(),
            HybridUrl = configuration["OpenDataLoader:HybridUrl"]
                        ?? configuration["ODL_HYBRID_URL"],
            HybridBackend = string.IsNullOrWhiteSpace(hybridBackend) ? "docling-fast" : hybridBackend.Trim()
        };
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

    public bool IsAuthorized(string? providedSecret)
    {
        if (string.IsNullOrEmpty(SharedSecret) || string.IsNullOrEmpty(providedSecret))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(SharedSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}

