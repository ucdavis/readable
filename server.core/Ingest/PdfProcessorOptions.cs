namespace server.core.Ingest;

public sealed class PdfProcessorOptions
{
    /// <summary>
    /// When true, the pipeline will run Adobe autotagging.
    /// When false, the pipeline will not rewrite the PDF beyond persisting the incoming stream.
    /// </summary>
    public bool UseAdobePdfServices { get; set; }

    /// <summary>
    /// When true, the pipeline will run the remediation processor after tagging/merging.
    /// </summary>
    public bool UsePdfRemediationProcessor { get; set; }

    public int MaxPagesPerChunk { get; set; } = 200;

    /// <summary>
    /// Root directory for PDF ingest work. If null/empty, defaults to /tmp (when available) or <see cref="Path.GetTempPath"/>.
    /// </summary>
    public string? WorkDirRoot { get; set; }
}
