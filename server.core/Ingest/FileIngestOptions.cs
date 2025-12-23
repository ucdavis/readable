namespace server.core.Ingest;

public sealed class FileIngestOptions
{
    public bool UseAdobePdfServices { get; set; }
    public bool UseNoopPdfRemediationProcessor { get; set; }

    /// <summary>
    /// Controls whether remediation uses OpenAI-backed services.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>null</c>: auto (use OpenAI only when an API key is configured; otherwise use sample services).</description></item>
    /// <item><description><c>false</c>: force sample services even if an API key exists.</description></item>
    /// <item><description><c>true</c>: force OpenAI; throws if API key is missing.</description></item>
    /// </list>
    /// </remarks>
    public bool? UseOpenAiRemediationServices { get; set; }

    public int PdfMaxPagesPerChunk { get; set; } = 200;
    public string? PdfWorkDirRoot { get; set; }

    public void UseNoops()
    {
        UseAdobePdfServices = false;
        UseNoopPdfRemediationProcessor = true;
        UseOpenAiRemediationServices = false;
    }
}
