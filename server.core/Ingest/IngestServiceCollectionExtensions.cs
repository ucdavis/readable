using Microsoft.Extensions.DependencyInjection;
using server.core.Remediate;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
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
            services.AddSingleton<IPdfRemediationProcessor, PdfRemediationProcessor>();
        }

        services.AddSingleton<IPdfProcessor, PdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }
}
