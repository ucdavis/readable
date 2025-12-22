using Microsoft.Extensions.DependencyInjection;

namespace server.core.Ingest;

public static class IngestServiceCollectionExtensions
{
    public static IServiceCollection AddFileIngest(this IServiceCollection services)
    {
        services.AddSingleton<IBlobStreamOpener, AzureBlobStreamOpener>();
        services.AddSingleton<IPdfProcessor, NoopPdfProcessor>();
        services.AddSingleton<IFileIngestProcessor, FileIngestProcessor>();
        return services;
    }
}

