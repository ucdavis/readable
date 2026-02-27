using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace server.core.Storage;

/// <summary>
/// Opens readable streams for processed (remediated) PDFs stored in Azure Blob Storage.
/// </summary>
public interface IProcessedPdfBlobService
{
    /// <summary>
    /// Opens a read-only stream for a processed PDF identified by its file ID.
    /// </summary>
    Task<Stream> OpenReadAsync(Guid fileId, CancellationToken cancellationToken);
}

public sealed class AzureProcessedPdfBlobService : IProcessedPdfBlobService
{
    private readonly IConfiguration _configuration;

    public AzureProcessedPdfBlobService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<Stream> OpenReadAsync(Guid fileId, CancellationToken cancellationToken)
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
            _configuration["Storage:ProcessedContainer"]
            ?? _configuration["Storage__ProcessedContainer"]
            ?? "processed";

        var blobName = $"{fileId:D}.pdf";
        var client = new BlobClient(connectionString, containerName, blobName);

        return await client.OpenReadAsync(cancellationToken: cancellationToken);
    }
}
