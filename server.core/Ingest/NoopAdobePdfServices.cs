using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace server.core.Ingest;

public sealed class NoopAdobePdfServices : IAdobePdfServices
{
    private readonly ILogger<NoopAdobePdfServices> _logger;

    public NoopAdobePdfServices(ILogger<NoopAdobePdfServices> logger)
    {
        _logger = logger;
    }

    public async Task<AdobeAutotagOutput> AutotagPdfAsync(
        string inputPdfPath,
        string outputTaggedPdfPath,
        string outputTaggingReportPath,
        CancellationToken cancellationToken)
    {
        await CopyFileAsync(inputPdfPath, outputTaggedPdfPath, cancellationToken);

        var report = new
        {
            kind = "noop",
            job = "AutotagPDFJob",
            inputPdfPath,
            outputTaggedPdfPath,
            generatedAtUtc = DateTimeOffset.UtcNow,
            note = "NoopAdobePdfServices: autotag not executed; output is a byte-for-byte copy of input."
        };

        await WriteJsonAsync(outputTaggingReportPath, report, cancellationToken);

        _logger.LogWarning(
            "NOOP Adobe Autotag: copied {input} -> {output}",
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
        await CopyFileAsync(inputPdfPath, outputPdfPath, cancellationToken);

        var report = new
        {
            kind = "noop",
            job = "PDFAccessibilityCheckerJob",
            inputPdfPath,
            outputPdfPath,
            pageStart,
            pageEnd,
            generatedAtUtc = DateTimeOffset.UtcNow,
            summary = new
            {
                note = "NoopAdobePdfServices: accessibility check not executed.",
                findings = Array.Empty<object>()
            }
        };

        var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await EnsureDirAsync(outputReportPath);
        await File.WriteAllTextAsync(outputReportPath, reportJson, cancellationToken);

        _logger.LogWarning(
            "NOOP Adobe Accessibility Checker: wrote placeholder report {report}",
            outputReportPath);

        return new AdobeAccessibilityCheckOutput(outputPdfPath, outputReportPath, reportJson);
    }

    private static async Task CopyFileAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        await EnsureDirAsync(outputPath);
        await using var input = File.OpenRead(inputPath);
        await using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task WriteJsonAsync(string outputPath, object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        await EnsureDirAsync(outputPath);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
    }

    private static Task EnsureDirAsync(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return Task.CompletedTask;
    }
}

