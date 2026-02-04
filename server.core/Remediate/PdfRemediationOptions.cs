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

    /// <summary>
    /// When enabled, demotes small <c>/Table</c> tag-tree elements that have no <c>/TH</c> header cells to <c>/Div</c>.
    /// </summary>
    /// <remarks>
    /// This is intended to address false-positive "layout tables" produced by autotagging that cause accessibility
    /// checker failures such as "Tables should have headers". Keep this disabled unless you are confident the
    /// affected tables are not true data tables.
    /// </remarks>
    public bool DemoteSmallTablesWithoutHeaders { get; set; }
}
