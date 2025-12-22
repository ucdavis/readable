namespace server.core.Ingest;

public sealed class FileIngestOptions
{
    public bool UseNoopAdobePdfServices { get; set; }
    public bool UseNoopPdfRemediationProcessor { get; set; }

    public void UseNoops()
    {
        UseNoopAdobePdfServices = true;
        UseNoopPdfRemediationProcessor = true;
    }
}

