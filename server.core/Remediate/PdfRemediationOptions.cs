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
    /// When enabled, unresolved character-encoding anomalies may be sent to an AI service for proposed repairs.
    /// </summary>
    public bool UseAiCharacterEncodingRepair { get; set; }

    /// <summary>
    /// Minimum confidence required before applying an AI-proposed character-encoding repair.
    /// </summary>
    public double CharacterEncodingRepairConfidenceThreshold { get; set; } = 0.50;

    /// <summary>
    /// When enabled, demotes small <c>/Table</c> tag-tree elements that have no <c>/TH</c> header cells to <c>/Div</c>.
    /// </summary>
    /// <remarks>
    /// This is intended to address false-positive "layout tables" produced by autotagging that cause accessibility
    /// checker failures such as "Tables should have headers" for tables that are effectively single row/column layout.
    /// Disable this if you find it demoting true data tables.
    /// </remarks>
    public bool DemoteSmallTablesWithoutHeaders { get; set; } = true;

    /// <summary>
    /// When enabled, demotes multi-row, multi-column no-header tables to <c>/Div</c>.
    /// </summary>
    /// <remarks>
    /// This avoids producing tagged tables that fail accessibility checks with "Tables should have headers" when
    /// the source PDF does not already contain usable table header cells.
    /// </remarks>
    public bool DemoteNoHeaderTables { get; set; } = true;

    /// <summary>
    /// Maximum time to wait for each no-header table classification request before leaving that table unchanged.
    /// </summary>
    public int NoHeaderTableClassificationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// When enabled, promotes the first usable row of confident no-header data tables into <c>/TH</c> cells.
    /// </summary>
    public bool PromoteFirstRowHeadersForNoHeaderTables { get; set; } = true;

    /// <summary>
    /// Legacy option retained for existing configuration binding. Use <see cref="DemoteNoHeaderTables" /> instead.
    /// </summary>
    public bool DemoteLikelyFormLayoutTables { get; set; } = true;
}
