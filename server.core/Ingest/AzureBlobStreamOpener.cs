using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace server.core.Ingest;

public interface IBlobStreamOpener
{
    Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken);
}

public sealed class AzureBlobStreamOpener : IBlobStreamOpener
{
    private readonly IConfiguration _configuration;

    public AzureBlobStreamOpener(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Opens a readable stream for an Azure Blob URI, using a connection string when configured.
    /// </summary>
    /// <remarks>
    /// If no connection string is present, this falls back to creating a client from the URI directly, which works
    /// for public blobs and URIs with SAS tokens.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken)
    {
        var connectionString =
            _configuration["Storage:ConnectionString"]
            ?? _configuration["Storage__ConnectionString"];

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var (containerName, blobName) = ParseContainerAndBlob(blobUri);
            var client = new BlobClient(connectionString, containerName, blobName);
            return await client.OpenReadAsync(cancellationToken: cancellationToken);
        }

        // Works for public blobs or URIs with SAS tokens.
        return await new BlobClient(blobUri).OpenReadAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Parses a blob URI path into container and blob names.
    /// </summary>
    private static (string ContainerName, string BlobName) ParseContainerAndBlob(Uri blobUri)
    {
        var path = blobUri.AbsolutePath.Trim('/');
        var firstSlash = path.IndexOf('/', StringComparison.Ordinal);

        if (firstSlash <= 0 || firstSlash == path.Length - 1)
        {
            throw new InvalidOperationException(
                $"Blob URL path did not look like '/<container>/<blob>': '{blobUri.AbsolutePath}'.");
        }

        return (path[..firstSlash], path[(firstSlash + 1)..]);
    }
}
