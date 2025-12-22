using Microsoft.Extensions.DependencyInjection;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    public static IServiceCollection AddFileIngest(this IServiceCollection services)
    {
        services.AddSingleton<IBlobStreamOpener, AzureBlobStreamOpener>();
        services.AddSingleton<IAdobePdfServices, AdobePdfServices>();
        services.AddSingleton<IPdfProcessor, PdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }
}
