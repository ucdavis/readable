using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;

namespace server.core.Ingest;

public interface IBlobStorage
{
    Task UploadAsync(Uri destinationBlobUri, Stream content, string contentType, CancellationToken cancellationToken);
    Task DeleteIfExistsAsync(Uri blobUri, CancellationToken cancellationToken);
}

public sealed class AzureBlobStorage : IBlobStorage
{
    private readonly IConfiguration _configuration;

    public AzureBlobStorage(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task UploadAsync(Uri destinationBlobUri, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var client = CreateClient(destinationBlobUri);
        await client.UploadAsync(content, overwrite: true, cancellationToken: cancellationToken);
        await client.SetHttpHeadersAsync(
            new BlobHttpHeaders { ContentType = contentType },
            cancellationToken: cancellationToken);
    }

    public Task DeleteIfExistsAsync(Uri blobUri, CancellationToken cancellationToken)
    {
        var client = CreateClient(blobUri);
        return client.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    private BlobClient CreateClient(Uri blobUri)
    {
        var connectionString =
            _configuration["Storage:ConnectionString"]
            ?? _configuration["Storage__ConnectionString"];

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var (containerName, blobName) = BlobUriParser.ParseContainerAndBlob(blobUri);
            return new BlobClient(connectionString, containerName, blobName);
        }

        // Works for public blobs or URIs with SAS tokens.
        return new BlobClient(blobUri);
    }
}
