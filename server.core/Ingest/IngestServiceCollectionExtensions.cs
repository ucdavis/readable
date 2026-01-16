using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Bookmarks;
using server.core.Remediate.Title;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PDF ingest pipeline (blob read, PDF processing, autotagging, and remediation).
    /// </summary>
    /// <remarks>
    /// When PDF remediation is enabled, OpenAI-backed services are required for title and alt text generation.
    /// Model selection can be overridden via <c>OPENAI_ALT_TEXT_MODEL</c> and <c>OPENAI_PDF_TITLE_MODEL</c>.
    /// When Adobe PDF Services is enabled, credentials must be provided via environment variables
    /// or configuration settings.
    /// </remarks>
    public static IServiceCollection AddFileIngest(this IServiceCollection services, Action<FileIngestOptions>? configure = null)
    {
        var options = new FileIngestOptions();
        configure?.Invoke(options);

        services.AddOptions<PdfProcessorOptions>().Configure(o =>
        {
            o.UseAdobePdfServices = options.UseAdobePdfServices;
            o.UsePdfRemediationProcessor = options.UsePdfRemediationProcessor;
            o.UsePdfBookmarks = options.UsePdfBookmarks;
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

        if (options.UsePdfRemediationProcessor)
        {
            services.AddSingleton<OpenAiRemediationConfig>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var apiKey = GetOpenAiApiKey(configuration);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "PDF remediation is enabled but no OpenAI API key is configured. Set OPENAI_API_KEY.");
                }

                var altTextModel =
                    configuration["OPENAI_ALT_TEXT_MODEL"]
                    ?? configuration["OpenAI:AltTextModel"]
                    ?? configuration["OpenAI__AltTextModel"]
                    ?? "gpt-5-mini";

                var pdfTitleModel =
                    configuration["OPENAI_PDF_TITLE_MODEL"]
                    ?? configuration["OpenAI:PdfTitleModel"]
                    ?? configuration["OpenAI__PdfTitleModel"]
                    ?? "gpt-5-mini";

                return new OpenAiRemediationConfig(apiKey, altTextModel, pdfTitleModel);
            });
            services.AddSingleton<IAltTextService>(sp =>
            {
                var cfg = sp.GetRequiredService<OpenAiRemediationConfig>();
                return new OpenAIAltTextService(cfg.ApiKey, cfg.AltTextModel);
            });
            services.AddSingleton<IPdfTitleService>(sp =>
            {
                var cfg = sp.GetRequiredService<OpenAiRemediationConfig>();
                return new OpenAIPdfTitleService(cfg.ApiKey, cfg.PdfTitleModel);
            });
            if (options.UsePdfBookmarks)
            {
                services.AddSingleton<IPdfBookmarkService, PdfBookmarkService>();
            }
            else
            {
                services.AddSingleton<IPdfBookmarkService, NoopPdfBookmarkService>();
            }
            services.AddSingleton<IPdfRemediationProcessor, PdfRemediationProcessor>();
        }
        else
        {
            services.AddSingleton<IPdfRemediationProcessor, NoopPdfRemediationProcessor>();
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

    private sealed record OpenAiRemediationConfig(string ApiKey, string AltTextModel, string PdfTitleModel);
}
