namespace server.core.Remediate;

public sealed class PdfRemediationOptions
{
    /// <summary>
    /// When enabled, generates <c>/Alt</c> values for <c>/Link</c> structure elements.
    /// </summary>
    /// <remarks>
    /// For most PDFs, the link's accessible name is already conveyed by the tagged text content. Generating link
    /// <c>/Alt</c> can be expensive (requires page text scanning) and may not be required for a document to be usable.
    /// </remarks>
    public bool GenerateLinkAltText { get; set; }
}
