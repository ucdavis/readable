using Adobe.PDFServicesSDK;
using Adobe.PDFServicesSDK.auth;
using Adobe.PDFServicesSDK.io;
using Adobe.PDFServicesSDK.pdfjobs.jobs;
using Adobe.PDFServicesSDK.pdfjobs.parameters.autotag;
using Adobe.PDFServicesSDK.pdfjobs.parameters.pdfaccessibilitychecker;
using Adobe.PDFServicesSDK.pdfjobs.results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace server.core.Ingest;

public interface IAdobePdfServices
{
    Task<AdobeAutotagOutput> AutotagPdfAsync(
        string inputPdfPath,
        string outputTaggedPdfPath,
        string outputTaggingReportPath,
        CancellationToken cancellationToken);

    Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
        string inputPdfPath,
        string outputPdfPath,
        string outputReportPath,
        int? pageStart,
        int? pageEnd,
        CancellationToken cancellationToken);
}

public sealed record AdobeAutotagOutput(string TaggedPdfPath, string TaggingReportPath);

public sealed record AdobeAccessibilityCheckOutput(string OutputPdfPath, string ReportPath, string? ReportJson);

public sealed class AdobePdfServices : IAdobePdfServices
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobePdfServices> _logger;

    public AdobePdfServices(IConfiguration configuration, ILogger<AdobePdfServices> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

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

    public async Task<AdobeAccessibilityCheckOutput> RunAccessibilityCheckerAsync(
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

        var location = pdfServices.Submit(job);
        PDFServicesResponse<PDFAccessibilityCheckerResult> response =
            pdfServices.GetJobResult<PDFAccessibilityCheckerResult>(location, typeof(PDFAccessibilityCheckerResult));

        var outputAsset = response.Result.Asset;
        var reportAsset = response.Result.Report;

        var outputStreamAsset = pdfServices.GetContent(outputAsset);
        await WriteStreamAssetAsync(outputStreamAsset, outputPdfPath, cancellationToken);

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

    private PDFServices CreateClient()
    {
        var clientId =
            _configuration["PDF_SERVICES_CLIENT_ID"]
            ?? _configuration["AdobePdfServices:ClientId"]
            ?? _configuration["AdobePdfServices__ClientId"];

        var clientSecret =
            _configuration["PDF_SERVICES_CLIENT_SECRET"]
            ?? _configuration["AdobePdfServices:ClientSecret"]
            ?? _configuration["AdobePdfServices__ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Adobe PDF Services credentials missing. Set PDF_SERVICES_CLIENT_ID and PDF_SERVICES_CLIENT_SECRET.");
        }

        ICredentials credentials = new ServicePrincipalCredentials(clientId, clientSecret);
        return new PDFServices(credentials);
    }

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

        await using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await streamAsset.Stream.CopyToAsync(output, cancellationToken);
    }
}
