using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace server.core.Ingest;

public sealed class OpenDataLoaderPdfServices : IAdobePdfServices
{
    private const int DefaultTimeoutSeconds = 240;
    private const int DefaultMaxRetries = 5;
    private const int DefaultBaseDelayMilliseconds = 500;

    private readonly HttpClient _httpClient;
    private readonly AdobePdfServices _adobePdfServices;
    private readonly ILogger<OpenDataLoaderPdfServices> _logger;
    private readonly OpenDataLoaderPdfServicesOptions _options;

    public OpenDataLoaderPdfServices(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenDataLoaderPdfServices> logger)
    {
        _httpClient = httpClient;
        _adobePdfServices = new AdobePdfServices(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdobePdfServices>.Instance);
        _logger = logger;
        _options = OpenDataLoaderPdfServicesOptions.FromConfiguration(configuration);

        _httpClient.BaseAddress = _options.BaseUrl;
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);
    }

    public async Task<AdobeAutotagOutput> AutotagPdfAsync(
        string inputPdfPath,
        string outputTaggedPdfPath,
        string outputTaggingReportPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = DateTimeOffset.UtcNow;
        var attempts = 0;

        while (true)
        {
            attempts++;
            using var request = new HttpRequestMessage(HttpMethod.Post, "convert");
            await using var inputStream = File.OpenRead(inputPdfPath);
            request.Content = new StreamContent(inputStream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputTaggedPdfPath)!);
                    await using (var output = File.Open(outputTaggedPdfPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(output, cancellationToken);
                    }

                    await WriteReportAsync(
                        outputTaggingReportPath,
                        new
                        {
                            tool = "OpenDataLoader",
                            status = "succeeded",
                            inputPdfPath,
                            outputTaggedPdfPath,
                            attempts,
                            startedAt,
                            completedAt = DateTimeOffset.UtcNow
                        },
                        cancellationToken);

                    _logger.LogInformation(
                        "OpenDataLoader autotag complete: {input} -> {tagged} attempts={attempts}",
                        inputPdfPath,
                        outputTaggedPdfPath,
                        attempts);

                    return new AdobeAutotagOutput(outputTaggedPdfPath, outputTaggingReportPath);
                }

                var detail = await ReadErrorDetailAsync(response, cancellationToken);
                if (!IsRetryable(response.StatusCode) || attempts > _options.MaxRetries)
                {
                    await WriteReportAsync(
                        outputTaggingReportPath,
                        new
                        {
                            tool = "OpenDataLoader",
                            status = "failed",
                            inputPdfPath,
                            outputTaggedPdfPath,
                            attempts,
                            statusCode = (int)response.StatusCode,
                            error = detail,
                            startedAt,
                            completedAt = DateTimeOffset.UtcNow
                        },
                        CancellationToken.None);

                    throw new InvalidOperationException(
                        $"OpenDataLoader autotag failed with HTTP {(int)response.StatusCode}: {detail}");
                }

                await DelayForRetryAsync(response, attempts, cancellationToken);
            }
            catch (Exception ex) when (IsTransientException(ex) && attempts <= _options.MaxRetries)
            {
                _logger.LogWarning(
                    ex,
                    "OpenDataLoader autotag transient failure on attempt {attempt}; retrying.",
                    attempts);
                await DelayForRetryAsync(null, attempts, cancellationToken);
            }
        }
    }

    public Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
        string inputPdfPath,
        string outputPdfPath,
        string outputReportPath,
        int? pageStart,
        int? pageEnd,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Delegating accessibility check to Adobe PDF Services: {input} -> {report}",
            inputPdfPath,
            outputReportPath);
        return _adobePdfServices.RunAccessibilityCheckerAsync(
            inputPdfPath,
            outputPdfPath,
            outputReportPath,
            pageStart,
            pageEnd,
            cancellationToken);
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static bool IsTransientException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException;
    }

    private async Task DelayForRetryAsync(HttpResponseMessage? response, int attempts, CancellationToken cancellationToken)
    {
        var delay = response?.Headers.RetryAfter?.Delta
            ?? ComputeBackoff(attempts, _options.BaseDelayMilliseconds);

        _logger.LogWarning(
            "OpenDataLoader autotag retrying after {delayMs}ms attempt={attempt}",
            delay.TotalMilliseconds,
            attempts);

        await Task.Delay(delay, cancellationToken);
    }

    private static TimeSpan ComputeBackoff(int attempts, int baseDelayMilliseconds)
    {
        var exponential = baseDelayMilliseconds * Math.Pow(2, Math.Max(0, attempts - 1));
        var jitter = Random.Shared.Next(0, baseDelayMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(exponential + jitter, 10_000));
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return response.ReasonPhrase ?? "OpenDataLoader request failed.";
        }

        return content.Length <= 4000 ? content : content[^4000..];
    }

    private static async Task WriteReportAsync(
        string outputTaggingReportPath,
        object report,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputTaggingReportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = File.Open(outputTaggingReportPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
            output,
            report,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    private sealed record OpenDataLoaderPdfServicesOptions(
        Uri BaseUrl,
        string ApiKey,
        int TimeoutSeconds,
        int MaxRetries,
        int BaseDelayMilliseconds)
    {
        public static OpenDataLoaderPdfServicesOptions FromConfiguration(IConfiguration configuration)
        {
            var baseUrlRaw =
                configuration["OpenDataLoader:BaseUrl"]
                ?? configuration["ODL_BASE_URL"];

            if (string.IsNullOrWhiteSpace(baseUrlRaw) || !Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var baseUrl))
            {
                throw new InvalidOperationException(
                    "OpenDataLoader autotagging is enabled but no valid base URL is configured. Set ODL_BASE_URL.");
            }

            var apiKey =
                configuration["OpenDataLoader:ApiKey"]
                ?? configuration["ODL_API_KEY"]
                ?? configuration["ODL_SHARED_SECRET"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "OpenDataLoader autotagging is enabled but no API key is configured. Set ODL_API_KEY.");
            }

            return new OpenDataLoaderPdfServicesOptions(
                BaseUrl: baseUrl,
                ApiKey: apiKey,
                TimeoutSeconds: GetInt(configuration, "OpenDataLoader:TimeoutSeconds", "ODL_TIMEOUT_SECONDS", DefaultTimeoutSeconds),
                MaxRetries: GetInt(configuration, "OpenDataLoader:MaxRetries", "ODL_MAX_RETRIES", DefaultMaxRetries),
                BaseDelayMilliseconds: GetInt(configuration, "OpenDataLoader:BaseDelayMilliseconds", "ODL_BASE_DELAY_MS", DefaultBaseDelayMilliseconds));
        }

        private static int GetInt(IConfiguration configuration, string primaryKey, string legacyKey, int fallback)
        {
            var raw = configuration[primaryKey] ?? configuration[legacyKey];
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : fallback;
        }
    }
}
