using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;

namespace server.core.Storage;

/// <summary>
/// Uploads PDF files directly to the incoming blob container (server-side).
/// Used by the direct-upload API endpoint so that API-key consumers don't need
/// to deal with SAS URLs.
/// </summary>
public interface IIncomingBlobUploadService
{
    /// <summary>
    /// Uploads a stream to the incoming container as {fileId}.pdf.
    /// </summary>
    Task UploadAsync(Guid fileId, Stream content, string contentType, CancellationToken cancellationToken);
}

public sealed class AzureIncomingBlobUploadService : IIncomingBlobUploadService
{
    private readonly IConfiguration _configuration;

    public AzureIncomingBlobUploadService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task UploadAsync(Guid fileId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var connectionString =
            _configuration["Storage:ConnectionString"]
            ?? _configuration["Storage__ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Storage connection string is not configured. Set Storage__ConnectionString (or Storage:ConnectionString).");
        }

        var containerName =
            _configuration["Storage:IncomingContainer"]
            ?? _configuration["Storage__IncomingContainer"]
            ?? "incoming";

        var blobName = $"{fileId:D}.pdf";
        var client = new BlobClient(connectionString, containerName, blobName);

        await client.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            },
            cancellationToken);
    }
}
