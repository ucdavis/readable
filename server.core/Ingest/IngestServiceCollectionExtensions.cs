using Microsoft.Extensions.DependencyInjection;
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

        if (options.UseNoopAdobePdfServices)
        {
            services.AddSingleton<IAdobePdfServices, NoopAdobePdfServices>();
        }
        else
        {
            services.AddSingleton<IAdobePdfServices, AdobePdfServices>();
        }

        if (options.UseNoopPdfRemediationProcessor)
        {
            services.AddSingleton<IPdfRemediationProcessor, NoopPdfRemediationProcessor>();
        }
        else
        {
            services.AddSingleton<IAltTextService>(_ =>
            {
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new SampleAltTextService();
                }

                var model = Environment.GetEnvironmentVariable("OPENAI_ALT_TEXT_MODEL") ?? "gpt-4o-mini";
                return new OpenAIAltTextService(apiKey, model);
            });
            services.AddSingleton<IPdfTitleService>(_ =>
            {
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new SamplePdfTitleService();
                }

                var model = Environment.GetEnvironmentVariable("OPENAI_PDF_TITLE_MODEL") ?? "gpt-4o-mini";
                return new OpenAIPdfTitleService(apiKey, model);
            });
            services.AddSingleton<IPdfRemediationProcessor, PdfRemediationProcessor>();
        }

        services.AddSingleton<IPdfProcessor, PdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }
}
