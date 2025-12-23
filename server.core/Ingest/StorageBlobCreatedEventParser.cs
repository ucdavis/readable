using System.Text.Json;

namespace server.core.Ingest;

public static class StorageBlobCreatedEventParser
{
    /// <summary>
    /// Parses an Azure Storage "BlobCreated" CloudEvent into a <see cref="BlobIngestRequest" />.
    /// </summary>
    /// <remarks>
    /// Supports two common event shapes:
    /// <list type="bullet">
    /// <item><description><c>data.url</c> is present (preferred).</description></item>
    /// <item><description><c>subject</c> + <c>source</c> are present; the blob URL is reconstructed.</description></item>
    /// </list>
    /// When reconstructing a URL, the endpoint suffix can be overridden via <c>READABLE_BLOB_ENDPOINT_SUFFIX</c>.
    /// </remarks>
    public static bool TryParse(string cloudEventJson, out BlobIngestRequest request, out string? error)
    {
        request = default!;
        error = null;

        if (string.IsNullOrWhiteSpace(cloudEventJson))
        {
            error = "CloudEvent JSON was empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(cloudEventJson);
            var root = doc.RootElement;

            var url = TryGetString(root, "data", "url");
            var subject = TryGetString(root, "subject");
            var source = TryGetString(root, "source");

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var blobUri))
                {
                    error = $"Invalid blob url: '{url}'.";
                    return false;
                }

                return TryBuildFromBlobUri(blobUri, out request, out error);
            }

            if (!string.IsNullOrWhiteSpace(subject))
            {
                if (!TryParseSubject(subject, out var containerName, out var blobName))
                {
                    error = $"Unrecognized subject format: '{subject}'.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(source))
                {
                    error = "CloudEvent 'data.url' missing and 'source' missing; cannot construct a blob URL.";
                    return false;
                }

                if (!TryGetStorageAccountNameFromSource(source, out var storageAccountName))
                {
                    error = $"Could not parse storage account name from source: '{source}'.";
                    return false;
                }

                var endpointSuffix =
                    Environment.GetEnvironmentVariable("READABLE_BLOB_ENDPOINT_SUFFIX")
                    ?? "blob.core.windows.net";

                var subjectBlobUri = new Uri(
                    $"https://{storageAccountName}.{endpointSuffix}/{Uri.EscapeDataString(containerName)}/{EscapeBlobPath(blobName)}");

                return TryBuildFromBlobUri(subjectBlobUri, out request, out error);
            }

            error = "CloudEvent missing 'data.url' and 'subject'.";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Invalid CloudEvent JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryBuildFromBlobUri(Uri blobUri, out BlobIngestRequest request, out string? error)
    {
        request = default!;
        error = null;

        var path = blobUri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"Blob URL had an empty path: '{blobUri}'.";
            return false;
        }

        var firstSlash = path.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash <= 0 || firstSlash == path.Length - 1)
        {
            error = $"Blob URL path did not look like '/<container>/<blob>': '{blobUri.AbsolutePath}'.";
            return false;
        }

        var containerName = path[..firstSlash];
        var blobName = path[(firstSlash + 1)..];
        var fileId = Path.GetFileNameWithoutExtension(blobName);

        if (string.IsNullOrWhiteSpace(fileId))
        {
            error = $"Could not derive fileId from blob name: '{blobName}'.";
            return false;
        }

        request = new BlobIngestRequest(
            BlobUri: blobUri,
            ContainerName: containerName,
            BlobName: blobName,
            FileId: fileId);

        return true;
    }

    private static bool TryParseSubject(string subject, out string containerName, out string blobName)
    {
        containerName = "";
        blobName = "";

        // Example:
        // /blobServices/default/containers/incoming/blobs/drylab.pdf
        const string containersMarker = "/containers/";
        const string blobsMarker = "/blobs/";

        var containersIndex = subject.IndexOf(containersMarker, StringComparison.OrdinalIgnoreCase);
        if (containersIndex < 0)
        {
            return false;
        }

        var afterContainers = containersIndex + containersMarker.Length;
        var blobsIndex = subject.IndexOf(blobsMarker, afterContainers, StringComparison.OrdinalIgnoreCase);
        if (blobsIndex < 0)
        {
            return false;
        }

        containerName = subject[afterContainers..blobsIndex].Trim('/').Trim();
        blobName = subject[(blobsIndex + blobsMarker.Length)..].Trim('/').Trim();

        return !string.IsNullOrWhiteSpace(containerName) && !string.IsNullOrWhiteSpace(blobName);
    }

    private static bool TryGetStorageAccountNameFromSource(string source, out string storageAccountName)
    {
        storageAccountName = "";

        // Example:
        // /subscriptions/.../providers/Microsoft.Storage/storageAccounts/streadabledevdataor3ker
        const string marker = "/storageAccounts/";
        var idx = source.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        storageAccountName = source[(idx + marker.Length)..].Trim('/').Trim();
        return !string.IsNullOrWhiteSpace(storageAccountName);
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            return current.GetString();
        }

        return null;
    }

    /// <summary>
    /// Uri-escapes each path segment while preserving '/' separators.
    /// </summary>
    private static string EscapeBlobPath(string blobPath)
    {
        // Uri-escape each segment but preserve path separators.
        return string.Join(
            "/",
            blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }
}
