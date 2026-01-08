using System.IO;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace server.core.Storage;

public interface IFileSasService
{
    UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive);
    DownloadSasResult CreateProcessedPdfDownloadSas(Guid fileId, string fileName, TimeSpan timeToLive);
}

public sealed record UploadSasResult(
    Uri UploadUri,
    Uri BlobUri,
    string ContainerName,
    string BlobName,
    DateTimeOffset ExpiresAt);

public sealed record DownloadSasResult(
    Uri DownloadUri,
    Uri BlobUri,
    string ContainerName,
    string BlobName,
    DateTimeOffset ExpiresAt);

public sealed class AzureBlobFileSasService : IFileSasService
{
    private static readonly Regex NonAscii = new(@"[^\x20-\x7E]+", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;

    public AzureBlobFileSasService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive)
    {
        var connectionString = GetConnectionString();

        var containerName =
            _configuration["Storage:IncomingContainer"]
            ?? _configuration["Storage__IncomingContainer"]
            ?? "incoming";

        // name the blob as "{fileId}.pdf" regardless of original file name, we'll keep track of the original name in the database
        var blobName = $"{fileId:D}.pdf";

        var blobClient = CreateBlobClient(connectionString, containerName, blobName);

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

    public DownloadSasResult CreateProcessedPdfDownloadSas(Guid fileId, string fileName, TimeSpan timeToLive)
    {
        var connectionString = GetConnectionString();

        var containerName =
            _configuration["Storage:ProcessedContainer"]
            ?? _configuration["Storage__ProcessedContainer"]
            ?? "processed";

        var blobName = $"{fileId:D}.pdf";

        var blobClient = CreateBlobClient(connectionString, containerName, blobName);

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
            ContentType = "application/pdf",
            ContentDisposition = BuildDownloadContentDisposition(fileName),
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var downloadUri = blobClient.GenerateSasUri(sasBuilder);

        return new DownloadSasResult(
            DownloadUri: downloadUri,
            BlobUri: blobClient.Uri,
            ContainerName: containerName,
            BlobName: blobName,
            ExpiresAt: expiresAt);
    }

    private string GetConnectionString()
    {
        var connectionString =
            _configuration["Storage:ConnectionString"]
            ?? _configuration["Storage__ConnectionString"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Storage connection string is not configured. Set Storage__ConnectionString (or Storage:ConnectionString).");
        }

        return connectionString;
    }

    private static BlobClient CreateBlobClient(string connectionString, string containerName, string blobName)
    {
        var blobClient = new BlobClient(connectionString, containerName, blobName);
        if (!blobClient.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "Blob client cannot generate SAS URIs. Ensure Storage__ConnectionString contains an account key.");
        }

        return blobClient;
    }

    private static string BuildDownloadContentDisposition(string fileName)
    {
        var normalized = (fileName ?? string.Empty).Trim();
        normalized = normalized.Replace("\r", string.Empty).Replace("\n", string.Empty);
        normalized = Path.GetFileName(normalized);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "document.pdf";
        }
        else if (!normalized.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".pdf";
        }

        var asciiFallback = NonAscii.Replace(normalized, "_").Replace("\"", "'");
        var utf8 = Uri.EscapeDataString(normalized);

        return $"attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{utf8}";
    }
}

