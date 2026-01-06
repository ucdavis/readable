using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace server.core.Upload;

public interface IUploadSasService
{
    UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive);
}

public sealed record UploadSasResult(
    Uri UploadUri,
    Uri BlobUri,
    string ContainerName,
    string BlobName,
    DateTimeOffset ExpiresAt);

public sealed class AzureBlobUploadSasService : IUploadSasService
{
    private readonly IConfiguration _configuration;

    public AzureBlobUploadSasService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Creates a SAS URL for uploading an incoming PDF file.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file.</param>
    /// <param name="timeToLive">The duration for which the SAS URL is valid.</param>
    /// <returns>An UploadSasResult containing the SAS URL and related information.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the storage connection string is not configured or the blob client cannot generate SAS URIs.</exception>
    public UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive)
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

        // name the blob as "{fileId}.pdf" regardless of original file name, we'll keep track of the original name in the database
        var blobName = $"{fileId:D}.pdf";

        var blobClient = new BlobClient(connectionString, containerName, blobName);
        if (!blobClient.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "Blob client cannot generate SAS URIs. Ensure Storage__ConnectionString contains an account key.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(timeToLive);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            StartsOn = now.AddMinutes(-5),
            ExpiresOn = expiresAt,
            Protocol = SasProtocol.Https,
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var uploadUri = blobClient.GenerateSasUri(sasBuilder);

        return new UploadSasResult(
            UploadUri: uploadUri,
            BlobUri: blobClient.Uri,
            ContainerName: containerName,
            BlobName: blobName,
            ExpiresAt: expiresAt);
    }
}

