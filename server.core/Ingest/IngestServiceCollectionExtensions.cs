using Microsoft.Extensions.DependencyInjection;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    public static IServiceCollection AddFileIngest(this IServiceCollection services, Action<FileIngestOptions>? configure = null)
    {
        var options = new FileIngestOptions();
        configure?.Invoke(options);

        services.AddSingleton<IBlobStreamOpener, AzureBlobStreamOpener>();

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
            // Placeholder until a real remediation processor exists.
            services.AddSingleton<IPdfRemediationProcessor, NoopPdfRemediationProcessor>();
        }

        services.AddSingleton<IPdfProcessor, PdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }
}
