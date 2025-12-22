namespace server.core.Ingest;

public sealed class FileIngestOptions
{
    public bool UseNoopAdobePdfServices { get; set; }
    public bool UseNoopPdfRemediationProcessor { get; set; }

    public int PdfMaxPagesPerChunk { get; set; } = 200;
    public string? PdfWorkDirRoot { get; set; }

    public void UseNoops()
    {
        UseNoopAdobePdfServices = true;
        UseNoopPdfRemediationProcessor = true;
    }
}
