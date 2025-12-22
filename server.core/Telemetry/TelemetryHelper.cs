using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace server.core.Telemetry;

public enum TelemetryHostKind
{
    Web,
    Worker
}

public static class TelemetryHelper
{
    public const string ActivitySourceName = "readable";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// Configures OpenTelemetry logging with optional JSON console output and OTLP exporter.
    /// </summary>
    public static void ConfigureLogging(
        ILoggingBuilder logging,
        IConfiguration? configuration = null,
        bool clearProviders = true,
        bool addJsonConsole = true)
    {
        ApplyOtelEnvironmentFromConfiguration(configuration);

        if (clearProviders)
        {
            logging.ClearProviders();
        }

        if (addJsonConsole)
        {
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
            });
        }

        logging.AddOpenTelemetry(logOptions =>
        {
            logOptions.SetResourceBuilder(CreateDefaultResourceBuilder());
            logOptions.IncludeFormattedMessage = true; // keep original message
            logOptions.IncludeScopes = true;           // carry scope props
            logOptions.ParseStateValues = true;        // structured state
            logOptions.AddOtlpExporter();              // uses OTEL_* env vars
        });
    }

    /// <summary>
    /// Configures OpenTelemetry tracing and metrics with OTLP exporter.
    /// Uses standard OTEL_* env vars for endpoint/headers/protocol/sampler.
    /// </summary>
    public static void ConfigureOpenTelemetry(IServiceCollection services, TelemetryHostKind hostKind = TelemetryHostKind.Web)
    {
        ConfigureOpenTelemetry(services, configuration: null, hostKind);
    }

    public static void ConfigureOpenTelemetry(
        IServiceCollection services,
        IConfiguration? configuration,
        TelemetryHostKind hostKind = TelemetryHostKind.Web)
    {
        ApplyOtelEnvironmentFromConfiguration(configuration);

        services
            .AddOpenTelemetry()
            .ConfigureResource(ApplyDefaultResource)
            .WithTracing(t =>
            {
                t.AddSource(ActivitySourceName);
                t.AddHttpClientInstrumentation();

                if (hostKind == TelemetryHostKind.Web)
                {
                    t.AddAspNetCoreInstrumentation();
                }

                t.AddOtlpExporter(); // uses OTEL_* env vars
            })
            .WithMetrics(m =>
            {
                m.AddHttpClientInstrumentation();

                if (hostKind == TelemetryHostKind.Web)
                {
                    m.AddAspNetCoreInstrumentation();
                }

                m.AddOtlpExporter(); // uses OTEL_* env vars
            });
    }

    private static ResourceBuilder CreateDefaultResourceBuilder()
    {
        var resourceBuilder = ResourceBuilder.CreateDefault();
        ApplyDefaultResource(resourceBuilder);
        return resourceBuilder;
    }

    private static void ApplyDefaultResource(ResourceBuilder resourceBuilder)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var serviceName =
            Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? entryAssembly?.GetName().Name
            ?? "unknown_service";

        var serviceVersion = entryAssembly?.GetName().Version?.ToString();
        var environmentName =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        resourceBuilder.AddService(serviceName: serviceName, serviceVersion: serviceVersion);

        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            resourceBuilder.AddAttributes(
                new[] { new KeyValuePair<string, object>("deployment.environment", environmentName) });
        }
    }

    private static void ApplyOtelEnvironmentFromConfiguration(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return;
        }

        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Environment variables can't contain ":" so skip nested config.
            if (!key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || key.Contains(':'))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, NormalizeEnvValue(value));
        }
    }

    private static string NormalizeEnvValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            {
                return trimmed[1..^1];
            }
        }

        return trimmed;
    }
}
