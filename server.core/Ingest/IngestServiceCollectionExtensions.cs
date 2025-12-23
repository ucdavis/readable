using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PDF ingest pipeline (blob read, PDF processing, autotagging, and remediation).
    /// </summary>
    /// <remarks>
    /// When OpenAI is enabled (<c>OPENAI_API_KEY</c>), remediation uses chat-based services for title and alt text.
    /// Without an API key, deterministic "Sample*" services are used as a local fallback. Model selection can be
    /// overridden via <c>OPENAI_ALT_TEXT_MODEL</c> and <c>OPENAI_PDF_TITLE_MODEL</c>.
    /// </remarks>
    public static IServiceCollection AddFileIngest(this IServiceCollection services, Action<FileIngestOptions>? configure = null)
    {
        var options = new FileIngestOptions();
        configure?.Invoke(options);

        services.AddOptions<PdfProcessorOptions>().Configure(o =>
        {
            o.MaxPagesPerChunk = options.PdfMaxPagesPerChunk;
            o.WorkDirRoot = options.PdfWorkDirRoot;
        });
        services.AddSingleton<IBlobStreamOpener, AzureBlobStreamOpener>();
        services.AddSingleton<IBlobStorage, AzureBlobStorage>();

        if (options.UseAdobePdfServices)
        {
            services.AddSingleton<IAdobePdfServices>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                EnsureAdobeCredentialsConfigured(configuration);
                return new AdobePdfServices(configuration, sp.GetRequiredService<ILogger<AdobePdfServices>>());
            });
        }
        else
        {
            services.AddSingleton<IAdobePdfServices, NoopAdobePdfServices>();
        }

        if (options.UseNoopPdfRemediationProcessor)
        {
            services.AddSingleton<IPdfRemediationProcessor, NoopPdfRemediationProcessor>();
        }
        else
        {
            services.AddSingleton<IAltTextService>(_ =>
            {
                var configuration = _.GetRequiredService<IConfiguration>();
                var apiKey = GetOpenAiApiKey(configuration);

                if (options.UseOpenAiRemediationServices is true && string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "OpenAI remediation is enabled but no API key is configured. Set OPENAI_API_KEY.");
                }

                if (options.UseOpenAiRemediationServices is false || string.IsNullOrWhiteSpace(apiKey))
                {
                    return new SampleAltTextService();
                }

                var model =
                    configuration["OPENAI_ALT_TEXT_MODEL"]
                    ?? configuration["OpenAI:AltTextModel"]
                    ?? configuration["OpenAI__AltTextModel"]
                    ?? "gpt-4o-mini";

                return new OpenAIAltTextService(apiKey, model);
            });
            services.AddSingleton<IPdfTitleService>(_ =>
            {
                var configuration = _.GetRequiredService<IConfiguration>();
                var apiKey = GetOpenAiApiKey(configuration);

                if (options.UseOpenAiRemediationServices is true && string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "OpenAI remediation is enabled but no API key is configured. Set OPENAI_API_KEY.");
                }

                if (options.UseOpenAiRemediationServices is false || string.IsNullOrWhiteSpace(apiKey))
                {
                    return new SamplePdfTitleService();
                }

                var model =
                    configuration["OPENAI_PDF_TITLE_MODEL"]
                    ?? configuration["OpenAI:PdfTitleModel"]
                    ?? configuration["OpenAI__PdfTitleModel"]
                    ?? "gpt-4o-mini";

                return new OpenAIPdfTitleService(apiKey, model);
            });
            services.AddSingleton<IPdfRemediationProcessor, PdfRemediationProcessor>();
        }

        services.AddSingleton<IPdfProcessor, PdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }

    private static void EnsureAdobeCredentialsConfigured(IConfiguration configuration)
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
                "Adobe PDF Services is enabled but credentials are missing. Set PDF_SERVICES_CLIENT_ID and PDF_SERVICES_CLIENT_SECRET.");
        }
    }

    private static string? GetOpenAiApiKey(IConfiguration configuration)
    {
        return
            configuration["OPENAI_API_KEY"]
            ?? configuration["OpenAI:ApiKey"]
            ?? configuration["OpenAI__ApiKey"];
    }
}
