using System.ClientModel;
using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace server.core.Remediate;

public sealed class OpenAIPdfCharacterEncodingRepairService : IPdfCharacterEncodingRepairService
{
    private const int MaxGroupsInPrompt = 40;
    private const int MaxContextsPerGroup = 4;
    private const int MaxCharsPerContext = 260;
    private static readonly BinaryData RepairSchema = BinaryData.FromBytes(
        """
        {
            "type": "object",
            "properties": {
                "repairs": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "fontObjectId": { "type": "string" },
                            "sourceCode": { "type": "string" },
                            "replacement": { "type": "string" },
                            "confidence": { "type": "number" },
                            "reason": { "type": "string" }
                        },
                        "required": ["fontObjectId", "sourceCode", "replacement", "confidence", "reason"],
                        "additionalProperties": false
                    }
                }
            },
            "required": ["repairs"],
            "additionalProperties": false
        }
        """u8.ToArray());

    private readonly ChatClient _chatClient;

    public OpenAIPdfCharacterEncodingRepairService(string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("OpenAI model is required.", nameof(model));
        }

        _chatClient = new ChatClient(model: model, apiKey: apiKey);
    }

    public async Task<PdfCharacterEncodingRepairResponse> ProposeRepairsAsync(
        PdfCharacterEncodingRepairRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Groups.Count == 0)
        {
            return new PdfCharacterEncodingRepairResponse(Array.Empty<PdfCharacterEncodingRepairProposal>());
        }

        List<ChatMessage> messages =
        [
            new SystemChatMessage(BuildSystemInstructions()),
            new UserChatMessage(BuildPrompt(request)),
        ];

        ChatCompletionOptions options = new()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "pdf_character_encoding_repairs",
                jsonSchema: RepairSchema,
                jsonSchemaIsStrict: true),
        };

        ClientResult<ChatCompletion> result =
            await _chatClient.CompleteChatAsync(messages, options, cancellationToken);

        return ParseResponse(RemediationHelpers.ExtractFirstTextOrEmpty(result.Value));
    }

    private static string BuildSystemInstructions() =>
        """
        You propose PDF ToUnicode character mapping repairs for accessibility remediation.
        The PDF itself will be edited by deterministic code; you only propose substitutions.
        Use the compact contexts to infer a single printable Unicode replacement for a font object and source code.
        Return a repair only when the replacement is strongly supported by the contexts.
        Return no repair when the evidence is weak, ambiguous, conflicts across contexts, or would require broader text rewriting.
        Keep replacements short: usually one visible character.
        """;

    private static string BuildPrompt(PdfCharacterEncodingRepairRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Propose high-confidence repairs for these PDF text encoding anomalies.");

        var language = RemediationHelpers.NormalizeWhitespace(request.PrimaryLanguage ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(language))
        {
            sb.Append("Primary document language: ");
            sb.AppendLine(language);
        }

        var groups = request.Groups.Take(MaxGroupsInPrompt).ToArray();
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            sb.AppendLine();
            sb.Append("Group ");
            sb.Append(groupIndex + 1);
            sb.Append(": fontObjectId=");
            sb.Append(group.FontObjectId);
            sb.Append(" fontName=");
            sb.Append(Truncate(group.FontName, 80));
            sb.Append(" sourceCode=");
            sb.Append(group.SourceCode);
            sb.Append(" anomalyKind=");
            sb.AppendLine(group.AnomalyKind);

            foreach (var context in group.Contexts.Take(MaxContextsPerGroup))
            {
                sb.Append("Page ");
                sb.Append(context.PageNumber);
                if (context.Mcid is not null)
                {
                    sb.Append(" mcid=");
                    sb.Append(context.Mcid.Value);
                }

                sb.Append(": ");
                sb.AppendLine(Truncate(context.Line, MaxCharsPerContext));
            }
        }

        if (request.Groups.Count > groups.Length)
        {
            sb.AppendLine();
            sb.AppendLine($"Only the first {groups.Length} anomaly groups are shown.");
        }

        return sb.ToString();
    }

    private static PdfCharacterEncodingRepairResponse ParseResponse(string text)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PdfCharacterEncodingRepairResponse(Array.Empty<PdfCharacterEncodingRepairProposal>());
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
            if (!root.TryGetProperty("repairs", out var repairsElement) || repairsElement.ValueKind != JsonValueKind.Array)
            {
                return new PdfCharacterEncodingRepairResponse(Array.Empty<PdfCharacterEncodingRepairProposal>());
            }

            var repairs = new List<PdfCharacterEncodingRepairProposal>();
            foreach (var repairElement in repairsElement.EnumerateArray())
            {
                var fontObjectId = TryGetString(repairElement, "fontObjectId");
                var sourceCode = TryGetString(repairElement, "sourceCode");
                var replacement = TryGetString(repairElement, "replacement");
                var reason = TryGetString(repairElement, "reason");
                var confidence = repairElement.TryGetProperty("confidence", out var confidenceElement)
                    && confidenceElement.TryGetDouble(out var parsedConfidence)
                        ? Math.Clamp(parsedConfidence, 0, 1)
                        : 0;

                if (string.IsNullOrWhiteSpace(fontObjectId) || string.IsNullOrWhiteSpace(sourceCode))
                {
                    continue;
                }

                repairs.Add(
                    new PdfCharacterEncodingRepairProposal(
                        RemediationHelpers.NormalizeWhitespace(fontObjectId),
                        RemediationHelpers.NormalizeWhitespace(sourceCode).ToUpperInvariant(),
                        replacement ?? string.Empty,
                        confidence,
                        RemediationHelpers.NormalizeWhitespace(reason ?? string.Empty)));
            }

            return new PdfCharacterEncodingRepairResponse(repairs);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Character encoding repair response was not valid JSON.", ex);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Truncate(string text, int maxChars)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);
        return text.Length <= maxChars ? text : $"{text[..maxChars]}...";
    }
}
