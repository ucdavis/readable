using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using opendataloader.api;

var builder = WebApplication.CreateBuilder(args);
var options = OpenDataLoaderOptions.FromConfiguration(builder.Configuration);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = options.MaxRequestBodySizeBytes;
});

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IOpenDataLoaderRunner, OpenDataLoaderRunner>();
builder.Services.AddSingleton<IRuntimeDependencyProbe, RuntimeDependencyProbe>();
builder.Services.AddSingleton<IConversionQueue, ConversionQueue>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method)
        && string.Equals(context.Request.Path, "/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var provided = context.Request.Headers["X-Api-Key"].ToString();
    if (!options.IsAuthorized(provided))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { detail = "Unauthorized" });
        return;
    }

    await next();
});

app.MapGet("/health", (IRuntimeDependencyProbe probe) =>
{
    var status = probe.Probe(options);
    return status.Ok
        ? Results.Ok(status)
        : Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/help", () => Results.Ok(new
{
    name = "OpenDataLoader Tagged PDF API",
    endpoint = "/convert",
    authHeader = "X-Api-Key",
    requestContentType = "application/pdf",
    responseContentType = "application/pdf",
    maxRequestBodySizeMb = options.MaxRequestBodySizeMb,
    processTimeoutSeconds = options.ProcessTimeoutSeconds,
    maxConcurrentConversions = options.MaxConcurrentConversions,
    maxQueuedConversions = options.MaxQueuedConversions,
    queueTimeoutSeconds = options.QueueTimeoutSeconds,
    outputFormat = options.OutputFormat,
    hybridEnabled = !string.IsNullOrWhiteSpace(options.HybridUrl)
}));

app.MapGet("/queue", (IConversionQueue queue) =>
{
    var snapshot = queue.GetSnapshot();
    return Results.Ok(new
    {
        activeConversions = snapshot.ActiveConversions,
        queuedConversions = snapshot.QueuedConversions,
        maxConcurrentConversions = snapshot.MaxConcurrentConversions,
        maxQueuedConversions = snapshot.MaxQueuedConversions
    });
});

app.MapPost("/convert", async Task<IResult> (
    HttpContext context,
    IOpenDataLoaderRunner runner,
    IConversionQueue conversionQueue,
    ILoggerFactory loggerFactory) =>
{
    if (!IsPdfContentType(context.Request.ContentType))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "Unsupported Media Type",
            detail: "Content-Type must be application/pdf.");
    }

    if (context.Request.ContentLength is 0)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Empty Request",
            detail: "Request body must contain PDF bytes.");
    }

    var logger = loggerFactory.CreateLogger("ConvertEndpoint");
    var workDir = Path.Combine(Path.GetTempPath(), "readable-opendataloader", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workDir);

    var inputPath = Path.Combine(workDir, "input.pdf");
    var outputDirectory = Path.Combine(workDir, "output");
    Directory.CreateDirectory(outputDirectory);

    var conversionTimedOut = false;

    try
    {
        await using (var fileStream = File.Create(inputPath))
        {
            await context.Request.Body.CopyToAsync(fileStream, context.RequestAborted);
        }

        if (new FileInfo(inputPath).Length == 0)
        {
            CleanupWorkDir(workDir, logger);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Empty Request",
                detail: "Request body must contain PDF bytes.");
        }

        await using var queueLease = await conversionQueue.TryAcquireAsync(context.RequestAborted);
        if (queueLease is null)
        {
            CleanupWorkDir(workDir, logger);
            context.Response.Headers.RetryAfter = options.QueueTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Conversion Queue Full",
                detail: "OpenDataLoader is currently at conversion capacity. Retry the request later.");
        }

        OpenDataLoaderRunResult result;
        using (var timeoutCts = new CancellationTokenSource(options.ProcessTimeout))
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeoutCts.Token))
        {
            try
            {
                result = await runner.ConvertAsync(inputPath, outputDirectory, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                conversionTimedOut = true;
                throw;
            }
        }

        if (result.ExitCode != 0)
        {
            CleanupWorkDir(workDir, logger);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Conversion Failed",
                detail: options.SanitizeError($"{result.StandardError}\n{result.StandardOutput}"));
        }

        if (string.IsNullOrWhiteSpace(result.TaggedPdfPath) || !File.Exists(result.TaggedPdfPath))
        {
            CleanupWorkDir(workDir, logger);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Tagged PDF Missing",
                detail: "OpenDataLoader completed without producing a tagged PDF artifact.");
        }

        context.Response.OnCompleted(() =>
        {
            CleanupWorkDir(workDir, logger);
            return Task.CompletedTask;
        });

        return Results.File(
            result.TaggedPdfPath,
            contentType: "application/pdf",
            fileDownloadName: "converted-tagged.pdf");
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        CleanupWorkDir(workDir, logger);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Payload Too Large",
            detail: $"The request exceeded the {options.MaxRequestBodySizeMb} MB limit.");
    }
    catch (OperationCanceledException) when (conversionTimedOut)
    {
        CleanupWorkDir(workDir, logger);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status408RequestTimeout,
            title: "Conversion Timed Out",
            detail: $"The conversion exceeded the {options.ProcessTimeoutSeconds}-second limit.");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        CleanupWorkDir(workDir, logger);
        return Results.Empty;
    }
    catch (InvalidOperationException ex)
    {
        CleanupWorkDir(workDir, logger);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Runtime Dependency Error",
            detail: options.SanitizeError(ex.Message));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled conversion failure.");
        CleanupWorkDir(workDir, logger);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Unhandled Error",
            detail: options.SanitizeError(ex.Message));
    }
});

app.Run();

static bool IsPdfContentType(string? contentType)
{
    if (string.IsNullOrWhiteSpace(contentType))
    {
        return false;
    }

    if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
    {
        return false;
    }

    return string.Equals(mediaType.MediaType.Value, "application/pdf", StringComparison.OrdinalIgnoreCase);
}

static void CleanupWorkDir(string path, ILogger logger)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to clean up temporary directory {tempPath}", path);
    }
}

public partial class Program;
