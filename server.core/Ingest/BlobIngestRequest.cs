namespace server.core.Ingest;

public sealed record BlobIngestRequest(
    Uri BlobUri,
    string ContainerName,
    string BlobName,
    string FileId);

