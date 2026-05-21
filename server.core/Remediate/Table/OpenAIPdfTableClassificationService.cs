#pragma warning disable OPENAI001

using System.Text;
using System.Text.Json;
using OpenAI.Responses;

namespace server.core.Remediate.Table;

public sealed class OpenAIPdfTableClassificationService : IPdfTableClassificationService
{
    private const int MaxRowsInPrompt = 8;
    private const int MaxCellsPerRowInPrompt = 8;
    private const int MaxCharsPerCellInPrompt = 200;
    private static readonly BinaryData ClassificationSchema = BinaryData.FromBytes(
        """
        {
            "type": "object",
            "properties": {
                "kind": {
                    "type": "string",
                    "enum": ["data_table", "not_data_table"]
                },
                "confidence": {
                    "type": "number"
                },
                "reason": {
                    "type": "string"
                }
            },
            "required": ["kind", "confidence", "reason"],
            "additionalProperties": false
        }
        """u8.ToArray());

    private readonly IOpenAIResponseGenerationClient _client;
    private readonly string _model;

    public OpenAIPdfTableClassificationService(string apiKey, string model)
        : this(model, CreateClient(apiKey))
    {
    }

    internal OpenAIPdfTableClassificationService(string model, IOpenAIResponseGenerationClient client)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("OpenAI model is required.", nameof(model));
        }

        _model = model;
        _client = client;
    }

    private static OpenAIResponseGenerationClient CreateClient(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
        }

        return new OpenAIResponseGenerationClient(apiKey);
    }

    public async Task<PdfTableClassificationResult> ClassifyAsync(
        PdfTableClassificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = OpenAIResponseOptions.Create(
            _model,
            "pdf_table_classification",
            OpenAIResponseOptions.TableClassificationMaxOutputTokens,
            OpenAIResponseOptions.CreateJsonSchemaFormat("pdf_table_classification", ClassificationSchema));

        options.Instructions = BuildSystemInstructions();
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(BuildPrompt(request)));

        var text = await _client.CreateResponseAsync(options, cancellationToken);
        return ParseResult(text);
    }

    private static string BuildSystemInstructions() =>
        """
        You classify PDF tag-tree tables for accessibility remediation.
        Decide whether the table is a semantic data table.
        Classify based on the table's apparent semantic purpose, not on whether every row is filled in.
        A data table organizes repeated records, choices, measurements, or values by rows and columns.
        Return not_data_table when the table appears to position labels, instructions, or form fields without conveying tabular relationships.
        Return not_data_table when the evidence is weak or both interpretations are plausible.
        Set confidence from 0 to 1 and keep the reason short.
        """;

    private static string BuildPrompt(PdfTableClassificationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Classify this no-header PDF table.");
        sb.Append("Rows: ");
        sb.AppendLine(request.RowCount.ToString());
        sb.Append("Max columns: ");
        sb.AppendLine(request.MaxColumnCount.ToString());
        sb.Append("Nested table: ");
        sb.AppendLine(request.HasNestedTable ? "yes" : "no");

        var language = RemediationHelpers.NormalizeWhitespace(request.PrimaryLanguage ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(language))
        {
            sb.Append("Primary document language: ");
            sb.AppendLine(language);
        }

        sb.AppendLine("Cell text sample:");
        var rowLimit = Math.Min(request.Rows.Count, MaxRowsInPrompt);
        for (var rowIndex = 0; rowIndex < rowLimit; rowIndex++)
        {
            var cells = request.Rows[rowIndex]
                .Take(MaxCellsPerRowInPrompt)
                .Select(FormatPromptCell);
            sb.Append("Row ");
            sb.Append(rowIndex + 1);
            sb.Append(": ");
            sb.AppendLine(string.Join(" | ", cells));
        }

        if (request.Rows.Count > MaxRowsInPrompt)
        {
            sb.AppendLine($"Only the first {MaxRowsInPrompt} rows are shown.");
        }

        return sb.ToString();
    }

    private static string FormatPromptCell(string? cell)
    {
        var normalized = RemediationHelpers.NormalizeWhitespace(cell ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "[blank]";
        }

        return normalized.Length <= MaxCharsPerCellInPrompt
            ? normalized
            : $"{normalized[..MaxCharsPerCellInPrompt]}...";
    }

    private static PdfTableClassificationResult ParseResult(string text)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Empty classifier response.");
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            text = text[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var kindText = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var confidenceEl)
                && confidenceEl.TryGetDouble(out var parsedConfidence)
                    ? parsedConfidence
                    : 0;
            var reason = root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null;

            var kind = NormalizeKind(kindText);
            confidence = Math.Clamp(confidence, 0, 1);
            reason = RemediationHelpers.NormalizeWhitespace(reason ?? string.Empty);
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "Classifier returned no reason.";
            }

            return new PdfTableClassificationResult(kind, confidence, reason);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Classifier response was not valid JSON.", ex);
        }
    }

    private static PdfTableKind NormalizeKind(string? value) =>
        RemediationHelpers.NormalizeWhitespace(value ?? string.Empty).ToLowerInvariant() switch
        {
            "data_table" or "data table" or "datatable" => PdfTableKind.DataTable,
            "not_data_table" or "not data table" or "notdatatable" or "layout_or_form_table" or
                "layout or form table" or "layout_table" or "form_table" => PdfTableKind.NotDataTable,
            _ => PdfTableKind.NotDataTable,
        };
}
