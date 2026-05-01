using Adobe.PDFServicesSDK;
using Adobe.PDFServicesSDK.auth;
using Adobe.PDFServicesSDK.io;
using Adobe.PDFServicesSDK.pdfjobs.jobs;
using Adobe.PDFServicesSDK.pdfjobs.parameters.autotag;
using Adobe.PDFServicesSDK.pdfjobs.parameters.pdfaccessibilitychecker;
using Adobe.PDFServicesSDK.pdfjobs.results;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace server.core.Ingest;

public interface IAutotagProvider
{
    string AutotagProviderName { get; }

    Task<AdobeAutotagOutput> AutotagPdfAsync(
        string inputPdfPath,
        string outputTaggedPdfPath,
        string outputTaggingReportPath,
        CancellationToken cancellationToken);
}

public interface IAccessibilityChecker
{
    string AccessibilityCheckerName => "AdobePdfServices";

    Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
        string inputPdfPath,
        string outputPdfPath,
        string outputReportPath,
        int? pageStart,
        int? pageEnd,
        CancellationToken cancellationToken);
}

public interface IAdobePdfServices : IAutotagProvider, IAccessibilityChecker;

public sealed record AdobeAutotagOutput(string TaggedPdfPath, string TaggingReportPath);

public sealed record AdobeAccessibilityCheckOutput(string OutputPdfPath, string ReportPath, string? ReportJson);

public sealed class AdobePdfServices : IAdobePdfServices
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobePdfServices> _logger;
    private readonly IAdobePdfServicesRateLimiter _rateLimiter;

    public string AutotagProviderName => FileIngestOptions.AutotagProviders.Adobe;
    public string AccessibilityCheckerName => "AdobePdfServices";

    public AdobePdfServices(
        IConfiguration configuration,
        ILogger<AdobePdfServices> logger,
        IAdobePdfServicesRateLimiter rateLimiter)
    {
        _configuration = configuration;
        _logger = logger;
        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// Runs Adobe "Autotag" on an input PDF and writes the tagged PDF plus an XLSX report to disk.
    /// </summary>
    /// <remarks>
    /// This is an external call to Adobe PDF Services; credentials must be configured via environment or app config.
    /// </remarks>
    public async Task<AdobeAutotagOutput> AutotagPdfAsync(
        string inputPdfPath,
        string outputTaggedPdfPath,
        string outputTaggingReportPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pdfServices = CreateClient();

        await using var inputStream = File.OpenRead(inputPdfPath);
        IAsset asset = pdfServices.Upload(inputStream, PDFServicesMediaType.PDF.GetMIMETypeValue());

        AutotagPDFParams autotagParams = AutotagPDFParams
            .AutotagPDFParamsBuilder()
            .GenerateReport()
            .Build();

        AutotagPDFJob job = new AutotagPDFJob(asset).SetParams(autotagParams);
        var location = pdfServices.Submit(job);

        PDFServicesResponse<AutotagPDFResult> response =
            pdfServices.GetJobResult<AutotagPDFResult>(location, typeof(AutotagPDFResult));

        var taggedAsset = response.Result.TaggedPDF;
        var reportAsset = response.Result.Report;

        var taggedStreamAsset = pdfServices.GetContent(taggedAsset);
        await WriteStreamAssetAsync(taggedStreamAsset, outputTaggedPdfPath, cancellationToken);

        var reportStreamAsset = pdfServices.GetContent(reportAsset);
        await WriteStreamAssetAsync(reportStreamAsset, outputTaggingReportPath, cancellationToken);

        _logger.LogInformation(
            "Adobe Autotag complete: {input} -> {tagged}",
            inputPdfPath,
            outputTaggedPdfPath);

        return new AdobeAutotagOutput(outputTaggedPdfPath, outputTaggingReportPath);
    }

    /// <summary>
    /// Runs Adobe's accessibility checker job and writes the output PDF plus a JSON report to disk.
    /// </summary>
    /// <remarks>
    /// The report JSON is also returned as a convenience for callers that want to persist it directly.
    /// </remarks>
    public async Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
        string inputPdfPath,
        string outputPdfPath,
        string outputReportPath,
        int? pageStart,
        int? pageEnd,
        CancellationToken cancellationToken)
    {
        var maxRetries = GetAccessibilityCheckerMaxRetries();
        var maxAttempts = maxRetries + 1;
        var baseDelay = GetAccessibilityCheckerRetryBaseDelay();
        var maxDelay = GetAccessibilityCheckerRetryMaxDelay();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAccessibilityCheckerOnceAsync(
                    inputPdfPath,
                    outputPdfPath,
                    outputReportPath,
                    pageStart,
                    pageEnd,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryableAccessibilityCheckerException(ex))
            {
                await RecordAccessibilityCheckerThrottleAsync(ex, cancellationToken);

                if (attempt >= maxAttempts)
                {
                    throw;
                }

                var delay = ComputeRetryDelay(attempt, baseDelay, maxDelay);
                _logger.LogWarning(
                    ex,
                    "Adobe Accessibility Checker failed with a retryable error for {input}; retrying attempt {nextAttempt}/{maxAttempts} after {delayMs}ms",
                    inputPdfPath,
                    attempt + 1,
                    maxAttempts,
                    delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerOnceAsync(
        string inputPdfPath,
        string outputPdfPath,
        string outputReportPath,
        int? pageStart,
        int? pageEnd,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pdfServices = CreateClient();

        await using var inputStream = File.OpenRead(inputPdfPath);
        await WaitForAccessibilityCheckerCallAsync("upload", cancellationToken);
        IAsset asset = pdfServices.Upload(inputStream, PDFServicesMediaType.PDF.GetMIMETypeValue());

        var job = new PDFAccessibilityCheckerJob(asset);
        if (pageStart is not null || pageEnd is not null)
        {
            var builder = PDFAccessibilityCheckerParams.PDFAccessibilityCheckerParamsBuilder();
            if (pageStart is not null)
            {
                builder = builder.WithPageStart(pageStart.Value);
            }

            if (pageEnd is not null)
            {
                builder = builder.WithPageEnd(pageEnd.Value);
            }

            job = job.SetParams(builder.Build());
        }

        await WaitForAccessibilityCheckerCallAsync("submit", cancellationToken);
        var location = pdfServices.Submit(job);
        PDFServicesResponse<PDFAccessibilityCheckerResult> response =
            await GetAccessibilityCheckerResultAsync(pdfServices, location, cancellationToken);

        var outputAsset = response.Result.Asset;
        var reportAsset = response.Result.Report;

        await WaitForAccessibilityCheckerCallAsync("get_output_content", cancellationToken);
        var outputStreamAsset = pdfServices.GetContent(outputAsset);
        await WriteStreamAssetAsync(outputStreamAsset, outputPdfPath, cancellationToken);

        await WaitForAccessibilityCheckerCallAsync("get_report_content", cancellationToken);
        var reportStreamAsset = pdfServices.GetContent(reportAsset);
        await WriteStreamAssetAsync(reportStreamAsset, outputReportPath, cancellationToken);

        // Convenience: return the JSON string so callers can persist it directly (e.g., to a DB column).
        // The PDF Services API returns a JSON report for this job; we still write it to disk for traceability.
        string? reportJson = null;
        if (File.Exists(outputReportPath))
        {
            reportJson = await File.ReadAllTextAsync(outputReportPath, cancellationToken);
        }

        _logger.LogInformation(
            "Adobe Accessibility Checker complete: {input} -> {report}",
            inputPdfPath,
            outputReportPath);

        return new AdobeAccessibilityCheckOutput(outputPdfPath, outputReportPath, reportJson);
    }

    private async Task<PDFServicesResponse<PDFAccessibilityCheckerResult>> GetAccessibilityCheckerResultAsync(
        PDFServices pdfServices,
        string location,
        CancellationToken cancellationToken)
    {
        var maxPollDuration = GetAccessibilityCheckerMaxPollDuration();
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        string? lastStatus = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed >= maxPollDuration)
            {
                throw new TimeoutException(
                    $"Adobe Accessibility Checker job polling exceeded {maxPollDuration.TotalSeconds:N0}s. " +
                    $"location={location}; lastStatus={lastStatus ?? "unknown"}; attempts={attempts}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            await WaitForAccessibilityCheckerCallAsync("get_job_status", cancellationToken);
            var statusResponse = pdfServices.GetJobStatus(location);
            attempts++;
            var status = statusResponse.Status;
            lastStatus = status;
            if (string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Adobe Accessibility Checker job failed while polling status. location={location}; status={status}; attempts={attempts}");
            }

            var retryIntervalSeconds = Math.Max(1, statusResponse.GetRetryInterval());
            await Task.Delay(TimeSpan.FromSeconds(retryIntervalSeconds), cancellationToken);
        }

        await WaitForAccessibilityCheckerCallAsync("get_job_result", cancellationToken);
        return pdfServices.GetJobResult<PDFAccessibilityCheckerResult>(
            location,
            typeof(PDFAccessibilityCheckerResult));
    }

    private async Task RecordAccessibilityCheckerThrottleAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await _rateLimiter.RecordThrottleAsync(
                AdobePdfServicesRateLimitOperations.AccessibilityChecker,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception rateLimiterException)
        {
            _logger.LogWarning(
                rateLimiterException,
                "Failed to record Adobe Accessibility Checker shared throttle cooldown.");
        }
    }

    private async Task WaitForAccessibilityCheckerCallAsync(string callName, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["adobe.pdf_services.operation"] = AdobePdfServicesRateLimitOperations.AccessibilityChecker,
            ["adobe.pdf_services.call"] = callName,
        });

        await _rateLimiter.WaitAsync(
            AdobePdfServicesRateLimitOperations.AccessibilityChecker,
            cost: 1,
            cancellationToken);
    }

    private int GetAccessibilityCheckerMaxRetries()
    {
        var configured =
            _configuration.GetValue<int?>("AdobePdfServices:AccessibilityCheckerMaxRetries")
            ?? _configuration.GetValue<int?>("AdobePdfServices__AccessibilityCheckerMaxRetries")
            ?? _configuration.GetValue<int?>("ADOBE_ACCESSIBILITY_CHECKER_MAX_RETRIES");

        return Math.Max(0, configured ?? 3);
    }

    private TimeSpan GetAccessibilityCheckerRetryBaseDelay()
    {
        var configuredSeconds =
            _configuration.GetValue<double?>("AdobePdfServices:AccessibilityCheckerRetryBaseDelaySeconds")
            ?? _configuration.GetValue<double?>("AdobePdfServices__AccessibilityCheckerRetryBaseDelaySeconds")
            ?? _configuration.GetValue<double?>("ADOBE_ACCESSIBILITY_CHECKER_RETRY_BASE_DELAY_SECONDS");

        return TimeSpan.FromSeconds(Math.Max(0, configuredSeconds ?? 2));
    }

    private TimeSpan GetAccessibilityCheckerRetryMaxDelay()
    {
        var configuredSeconds =
            _configuration.GetValue<double?>("AdobePdfServices:AccessibilityCheckerRetryMaxDelaySeconds")
            ?? _configuration.GetValue<double?>("AdobePdfServices__AccessibilityCheckerRetryMaxDelaySeconds")
            ?? _configuration.GetValue<double?>("ADOBE_ACCESSIBILITY_CHECKER_RETRY_MAX_DELAY_SECONDS");

        return TimeSpan.FromSeconds(Math.Max(0, configuredSeconds ?? 30));
    }

    private TimeSpan GetAccessibilityCheckerMaxPollDuration()
    {
        var configuredSeconds =
            _configuration.GetValue<double?>("AdobePdfServices:AccessibilityCheckerMaxPollDurationSeconds")
            ?? _configuration.GetValue<double?>("AdobePdfServices__AccessibilityCheckerMaxPollDurationSeconds")
            ?? _configuration.GetValue<double?>("ADOBE_ACCESSIBILITY_CHECKER_MAX_POLL_DURATION_SECONDS");

        return TimeSpan.FromSeconds(Math.Max(1, configuredSeconds ?? 600));
    }

    private static TimeSpan ComputeRetryDelay(int failedAttempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (baseDelay <= TimeSpan.Zero || maxDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var exponentialMultiplier = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var delayMs = Math.Min(baseDelay.TotalMilliseconds * exponentialMultiplier, maxDelay.TotalMilliseconds);
        var jitterMultiplier = 0.8 + (Random.Shared.NextDouble() * 0.4);

        return TimeSpan.FromMilliseconds(delayMs * jitterMultiplier);
    }

    private static bool IsRetryableAccessibilityCheckerException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (IsAdobeRateLimitException(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAdobeRateLimitException(Exception exception)
    {
        var exceptionText = string.Concat(exception.GetType().FullName, " ", exception.Message);

        return exceptionText.Contains("ServiceUsageException", StringComparison.OrdinalIgnoreCase)
            || exceptionText.Contains("TOO_MANY_REQUESTS", StringComparison.OrdinalIgnoreCase)
            || exceptionText.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || exceptionText.Contains("httpStatusCode = 429", StringComparison.OrdinalIgnoreCase)
            || exceptionText.Contains("httpStatusCode=429", StringComparison.OrdinalIgnoreCase)
            || exceptionText.Contains("StatusCode: 429", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates an authenticated Adobe PDF Services client from configured credentials.
    /// </summary>
    private PDFServices CreateClient()
    {
        EnsureCredentialsConfigured(_configuration);

        var clientId =
            _configuration["PDF_SERVICES_CLIENT_ID"]
            ?? _configuration["AdobePdfServices:ClientId"]
            ?? _configuration["AdobePdfServices__ClientId"];

        var clientSecret =
            _configuration["PDF_SERVICES_CLIENT_SECRET"]
            ?? _configuration["AdobePdfServices:ClientSecret"]
            ?? _configuration["AdobePdfServices__ClientSecret"];

        ICredentials credentials = new ServicePrincipalCredentials(clientId, clientSecret);
        return new PDFServices(credentials);
    }

    public static void EnsureCredentialsConfigured(IConfiguration configuration)
    {
        var clientId =
            configuration["PDF_SERVICES_CLIENT_ID"]
            ?? configuration["AdobePdfServices:ClientId"]
            ?? configuration["AdobePdfServices__ClientId"];

        var clientSecret =
            configuration["PDF_SERVICES_CLIENT_SECRET"]
            ?? configuration["AdobePdfServices:ClientSecret"]
            ?? configuration["AdobePdfServices__ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Adobe PDF Services credentials missing. Set PDF_SERVICES_CLIENT_ID and PDF_SERVICES_CLIENT_SECRET.");
        }
    }

    /// <summary>
    /// Writes a PDF Services stream asset to disk, creating parent directories as needed.
    /// </summary>
    private static async Task WriteStreamAssetAsync(
        StreamAsset streamAsset,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var input = streamAsset.Stream;
        await using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }
}
