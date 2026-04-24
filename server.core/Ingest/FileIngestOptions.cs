namespace server.core.Ingest;

public sealed class FileIngestOptions
{
    public bool UseAdobePdfServices { get; set; }
    public bool UsePdfRemediationProcessor { get; set; }
    public bool UsePdfBookmarks { get; set; }
    public bool AutotagTaggedPdfs { get; set; }
    public string AutotagProvider { get; set; } = AutotagProviders.Adobe;

    public int PdfMaxPagesPerChunk { get; set; } = 200;
    public int MaxUploadPages { get; set; }
    public string? PdfWorkDirRoot { get; set; }

    public void UseNoops()
    {
        UseAdobePdfServices = false;
        UsePdfRemediationProcessor = false;
        UsePdfBookmarks = false;
        AutotagTaggedPdfs = false;
        MaxUploadPages = 0;
    }

    public static class AutotagProviders
    {
        public const string Adobe = "Adobe";
        public const string None = "None";
        public const string OpenDataLoader = "OpenDataLoader";
    }
}
