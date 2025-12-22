using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using server.core.Ingest;
using server.core.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

TelemetryHelper.ConfigureLogging(
    builder.Logging,
    builder.Configuration,
    clearProviders: false,
    addJsonConsole: true);
TelemetryHelper.ConfigureOpenTelemetry(builder.Services, builder.Configuration, TelemetryHostKind.Worker);

builder.Services.AddFileIngest();

using var host = builder.Build();

var (blobUrl, cloudEventPath) = ParseArgs(args);
if (blobUrl is null && cloudEventPath is null)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project tools/ingest.runner -- --blob-url <url>");
    Console.Error.WriteLine("  dotnet run --project tools/ingest.runner -- --cloud-event-json <path>");
    return 2;
}

BlobIngestRequest request;
if (blobUrl is not null)
{
    if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var uri))
    {
        Console.Error.WriteLine($"Invalid --blob-url: '{blobUrl}'");
        return 2;
    }

    if (!StorageBlobCreatedEventParser.TryParse(
            $$"""{"data":{"url":"{{uri}}"}}""",
            out request,
            out var error))
    {
        Console.Error.WriteLine($"Failed to build request from url: {error}");
        return 2;
    }
}
else
{
    var json = await File.ReadAllTextAsync(cloudEventPath!, CancellationToken.None);
    if (!StorageBlobCreatedEventParser.TryParse(json, out request, out var error))
    {
        Console.Error.WriteLine($"Failed to parse CloudEvent JSON: {error}");
        return 2;
    }
}

var processor = host.Services.GetRequiredService<IFileIngestProcessor>();
await processor.ProcessAsync(request, CancellationToken.None);

return 0;

static (string? BlobUrl, string? CloudEventPath) ParseArgs(string[] args)
{
    string? blobUrl = null;
    string? cloudEventPath = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--blob-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            blobUrl = args[++i];
            continue;
        }

        if (string.Equals(arg, "--cloud-event-json", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            cloudEventPath = args[++i];
            continue;
        }
    }

    return (blobUrl, cloudEventPath);
}

