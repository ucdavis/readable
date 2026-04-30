namespace server.core.Ingest;

public static class BlobUriParser
{
    public static (string ContainerName, string BlobName) ParseContainerAndBlob(Uri blobUri)
    {
        var path = blobUri.AbsolutePath.Trim('/');
        var firstSlash = path.IndexOf('/', StringComparison.Ordinal);

        if (firstSlash <= 0 || firstSlash == path.Length - 1)
        {
            throw new InvalidOperationException(
                $"Blob URL path did not look like '/<container>/<blob>': '{blobUri.AbsolutePath}'.");
        }

        return (
            Uri.UnescapeDataString(path[..firstSlash]),
            Uri.UnescapeDataString(path[(firstSlash + 1)..]));
    }
}
