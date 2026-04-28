using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Bookmarks;
using server.core.Remediate.Rasterize;
using server.core.Remediate.Table;
using server.core.Remediate.Title;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PDF ingest pipeline (blob read, PDF processing, autotagging, and remediation).
    /// </summary>
    /// <remarks>
    /// When PDF remediation is enabled, OpenAI-backed services are required for title and alt text generation.
    /// Model selection can be overridden via <c>OPENAI_ALT_TEXT_MODEL</c>, <c>OPENAI_PDF_TITLE_MODEL</c>, and
    /// <c>OPENAI_PDF_TABLE_CLASSIFICATION_MODEL</c>.
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
            o.AutotagTaggedPdfs = options.AutotagTaggedPdfs;
            o.MaxPagesPerChunk = options.PdfMaxPagesPerChunk;
            o.MaxUploadPages = options.MaxUploadPages;
            o.WorkDirRoot = options.PdfWorkDirRoot;
        });
        services.AddSingleton<IBlobStreamOpener, AzureBlobStreamOpener>();
        services.AddSingleton<IBlobStorage, AzureBlobStorage>();

        if (options.UseAdobePdfServices)
        {
            if (IsOpenDataLoaderAutotagProvider(options.AutotagProvider))
            {
                services.AddHttpClient<IAdobePdfServices, OpenDataLoaderPdfServices>();
            }
            else
            {
                services.AddSingleton<IAdobePdfServices>(sp =>
                {
                    var configuration = sp.GetRequiredService<IConfiguration>();
                    EnsureAdobeCredentialsConfigured(configuration);
                    return new AdobePdfServices(configuration, sp.GetRequiredService<ILogger<AdobePdfServices>>());
                });
            }
        }
        else
        {
            services.AddSingleton<IAdobePdfServices, NoopAdobePdfServices>();
        }

        if (options.UsePdfRemediationProcessor)
        {
            services.AddOptions<PdfRemediationOptions>().Configure<IConfiguration>((o, configuration) =>
            {
                o.GenerateLinkAltText =
                    configuration.GetValue<bool>("Ingest:GenerateLinkAltText")
                    || configuration.GetValue<bool>("INGEST_GENERATE_LINK_ALT_TEXT");

                o.DemoteSmallTablesWithoutHeaders =
                    configuration.GetValue<bool?>("Ingest:DemoteSmallTablesWithoutHeaders")
                    ?? configuration.GetValue<bool?>("INGEST_DEMOTE_SMALL_TABLES_WITHOUT_HEADERS")
                    ?? true;

                o.PromoteFirstRowHeadersForNoHeaderTables =
                    configuration.GetValue<bool?>("Ingest:PromoteFirstRowHeadersForNoHeaderTables")
                    ?? configuration.GetValue<bool?>("INGEST_PROMOTE_FIRST_ROW_HEADERS_FOR_NO_HEADER_TABLES")
                    ?? true;

                o.DemoteLikelyFormLayoutTables =
                    configuration.GetValue<bool?>("Ingest:DemoteLikelyFormLayoutTables")
                    ?? configuration.GetValue<bool?>("INGEST_DEMOTE_LIKELY_FORM_LAYOUT_TABLES")
                    ?? true;

                o.DemoteNoHeaderTables =
                    configuration.GetValue<bool?>("Ingest:DemoteNoHeaderTables")
                    ?? configuration.GetValue<bool?>("INGEST_DEMOTE_NO_HEADER_TABLES")
                    ?? configuration.GetValue<bool?>("Ingest:DemoteLikelyFormLayoutTables")
                    ?? configuration.GetValue<bool?>("INGEST_DEMOTE_LIKELY_FORM_LAYOUT_TABLES")
                    ?? true;

                o.NoHeaderTableClassificationTimeoutSeconds =
                    configuration.GetValue<int?>("Ingest:NoHeaderTableClassificationTimeoutSeconds")
                    ?? configuration.GetValue<int?>("INGEST_NO_HEADER_TABLE_CLASSIFICATION_TIMEOUT_SECONDS")
                    ?? 30;
            });

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

                var pdfTableClassificationModel =
                    configuration["OPENAI_PDF_TABLE_CLASSIFICATION_MODEL"]
                    ?? configuration["OpenAI:PdfTableClassificationModel"]
                    ?? configuration["OpenAI__PdfTableClassificationModel"]
                    ?? "gpt-5-mini";

                return new OpenAiRemediationConfig(
                    apiKey,
                    altTextModel,
                    pdfTitleModel,
                    pdfTableClassificationModel);
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
            services.AddSingleton<IPdfTableClassificationService>(sp =>
            {
                var cfg = sp.GetRequiredService<OpenAiRemediationConfig>();
                return new OpenAIPdfTableClassificationService(cfg.ApiKey, cfg.PdfTableClassificationModel);
            });
            if (options.UsePdfBookmarks)
            {
                services.AddSingleton<IPdfBookmarkService, PdfBookmarkService>();
            }
            else
            {
                services.AddSingleton<IPdfBookmarkService, NoopPdfBookmarkService>();
            }

            services.AddSingleton<IPdfPageRasterizer, DocnetPdfPageRasterizer>();
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
        AdobePdfServices.EnsureCredentialsConfigured(configuration);
    }

    private static bool IsOpenDataLoaderAutotagProvider(string? value)
    {
        return string.Equals(
            value,
            FileIngestOptions.AutotagProviders.OpenDataLoader,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetOpenAiApiKey(IConfiguration configuration)
    {
        return
            configuration["OPENAI_API_KEY"]
            ?? configuration["OpenAI:ApiKey"]
            ?? configuration["OpenAI__ApiKey"];
    }

    private sealed record OpenAiRemediationConfig(
        string ApiKey,
        string AltTextModel,
        string PdfTitleModel,
        string PdfTableClassificationModel);
}
