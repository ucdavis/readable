using System.Diagnostics;

namespace opendataloader.api;

public interface IOpenDataLoaderRunner
{
    Task<OpenDataLoaderRunResult> ConvertAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken);
}

public sealed record OpenDataLoaderRunResult(
    int ExitCode,
    string? TaggedPdfPath,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0 && !string.IsNullOrWhiteSpace(TaggedPdfPath);
}

public sealed class OpenDataLoaderRunner : IOpenDataLoaderRunner
{
    private readonly ILogger<OpenDataLoaderRunner> _logger;
    private readonly OpenDataLoaderOptions _options;

    public OpenDataLoaderRunner(ILogger<OpenDataLoaderRunner> logger, OpenDataLoaderOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<OpenDataLoaderRunResult> ConvertAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var args = BuildArguments(_options, inputPath, outputDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CommandPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Failed to start '{_options.CommandPath}'. Ensure the OpenDataLoader CLI is installed and on PATH.",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var taggedPdfPath = FindTaggedPdfPath(inputPath, outputDirectory);
        _logger.LogInformation(
            "OpenDataLoader exited with code {exitCode}. taggedPdfPath={taggedPdfPath}",
            process.ExitCode,
            taggedPdfPath ?? "<none>");

        return new OpenDataLoaderRunResult(
            ExitCode: process.ExitCode,
            TaggedPdfPath: taggedPdfPath,
            StandardOutput: stdout,
            StandardError: stderr);
    }

    public static IReadOnlyList<string> BuildArguments(OpenDataLoaderOptions options, string inputPath, string outputDirectory)
    {
        var args = new List<string>
        {
            inputPath,
            "--output-dir",
            outputDirectory,
            "--format",
            options.OutputFormat,
            "--quiet",
        };

        if (!string.IsNullOrWhiteSpace(options.HybridUrl))
        {
            args.Add("--hybrid");
            args.Add(options.HybridBackend);
            args.Add("--hybrid-url");
            args.Add(options.HybridUrl);
        }

        return args;
    }

    public static string? FindTaggedPdfPath(string inputPath, string outputDirectory)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var expected = Path.Combine(outputDirectory, $"{stem}_tagged.pdf");
        if (File.Exists(expected))
        {
            return expected;
        }

        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(outputDirectory, "*_tagged.pdf", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during timeout/abort cleanup.
        }
    }
}
