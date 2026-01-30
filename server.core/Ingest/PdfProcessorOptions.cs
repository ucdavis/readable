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

    /// <summary>
    /// When true, the remediation processor will attempt to generate PDF bookmarks (outlines) for tagged PDFs.
    /// </summary>
    public bool UsePdfBookmarks { get; set; }

    /// <summary>
    /// When true, the pipeline will run Adobe autotagging even if the incoming PDF is already tagged.
    /// When false, tagged PDFs will skip autotagging unless the BEFORE accessibility report indicates major
    /// tag/structure problems (e.g., "Tagged content", "Tab order") or the tag tree looks obviously broken.
    /// </summary>
    public bool AutotagTaggedPdfs { get; set; }

    public int MaxPagesPerChunk { get; set; } = 200;

    /// <summary>
    /// Root directory for PDF ingest work. If null/empty, defaults to /tmp (when available) or <see cref="Path.GetTempPath"/>.
    /// </summary>
    public string? WorkDirRoot { get; set; }
}
