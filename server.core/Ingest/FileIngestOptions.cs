namespace server.core.Ingest;

public sealed class FileIngestOptions
{
    public bool UseAdobePdfServices { get; set; }
    public bool UsePdfRemediationProcessor { get; set; }
    public bool UsePdfBookmarks { get; set; }
    public bool AutotagTaggedPdfs { get; set; }

    public int PdfMaxPagesPerChunk { get; set; } = 200;
    public string? PdfWorkDirRoot { get; set; }

    public void UseNoops()
    {
        UseAdobePdfServices = false;
        UsePdfRemediationProcessor = false;
        UsePdfBookmarks = false;
        AutotagTaggedPdfs = false;
    }
}
