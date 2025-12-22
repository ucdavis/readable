namespace server.core.Ingest;

public sealed class PdfProcessorOptions
{
    public int MaxPagesPerChunk { get; set; } = 200;

    /// <summary>
    /// Root directory for PDF ingest work. If null/empty, defaults to /tmp (when available) or <see cref="Path.GetTempPath"/>.
    /// </summary>
    public string? WorkDirRoot { get; set; }
}

