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
    private const string OtelResourceAttributesEnvVarName = "OTEL_RESOURCE_ATTRIBUTES";

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
        var serviceName = GetNonEmptyEnvironmentVariable("OTEL_SERVICE_NAME")
                          ?? GetOtelResourceAttribute("service.name")
                          ?? entryAssembly?.GetName().Name
                          ?? "unknown_service";

        var serviceVersion = GetOtelResourceAttribute("service.version")
                             ?? entryAssembly?.GetName().Version?.ToString();
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

    private static string? GetNonEmptyEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetOtelResourceAttribute(string attributeName)
    {
        var raw = Environment.GetEnvironmentVariable(OtelResourceAttributesEnvVarName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (var pair in SplitUnescaped(raw, ','))
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                continue;
            }

            if (!TrySplitFirstUnescaped(pair, '=', out var rawKey, out var rawValue))
            {
                continue;
            }

            var key = UnescapeOtelValue(rawKey).Trim();
            if (!string.Equals(key, attributeName, StringComparison.Ordinal))
            {
                continue;
            }

            var value = UnescapeOtelValue(rawValue).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static IEnumerable<string> SplitUnescaped(string input, char separator)
    {
        var start = 0;
        var escaped = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == separator)
            {
                yield return input[start..i];
                start = i + 1;
            }
        }

        yield return input[start..];
    }

    private static bool TrySplitFirstUnescaped(string input, char separator, out string left, out string right)
    {
        var escaped = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == separator)
            {
                left = input[..i];
                right = input[(i + 1)..];
                return true;
            }
        }

        left = string.Empty;
        right = string.Empty;
        return false;
    }

    private static string UnescapeOtelValue(string value)
    {
        // OTEL_RESOURCE_ATTRIBUTES supports escaping for separators (\, \=) and backslash (\\).
        // Keep this intentionally small and only handle the documented sequences.
        return value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\=", "=", StringComparison.Ordinal);
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
