#pragma warning disable OPENAI001

using System.Text;
using OpenAI.Responses;
using server.core.Remediate;

namespace server.core.Remediate.Title;

public sealed class OpenAIPdfTitleService : IPdfTitleService
{
    private readonly IOpenAIResponseGenerationClient _client;
    private readonly string _model;

    public OpenAIPdfTitleService(string apiKey, string model)
        : this(apiKey, model, endpoint: null)
    {
    }

    public OpenAIPdfTitleService(string apiKey, string model, string? endpoint)
        : this(model, CreateClient(apiKey, endpoint))
    {
    }

    internal OpenAIPdfTitleService(string model, IOpenAIResponseGenerationClient client)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("OpenAI model is required.", nameof(model));
        }

        _model = model;
        _client = client;
    }

    private static OpenAIResponseGenerationClient CreateClient(string apiKey, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
        }

        return new OpenAIResponseGenerationClient(apiKey, endpoint);
    }

    /// <summary>
    /// Generates a concise PDF title from extracted early-page text, optionally keeping the current title.
    /// </summary>
    /// <remarks>
    /// The prompt explicitly allows the model to return the current title unchanged when it matches the extracted
    /// context, reducing unnecessary title churn.
    /// </remarks>
    public async Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = BuildPrompt(request.CurrentTitle, request.ExtractedText, request.PrimaryLanguage);

        var options = OpenAIResponseOptions.Create(
            _model,
            "pdf_title",
            OpenAIResponseOptions.TitleMaxOutputTokens);

        options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

        return await _client.CreateResponseAsync(options, cancellationToken);
    }

    /// <summary>
    /// Builds a prompt that asks for a single plain-text title and includes current title and extracted context.
    /// </summary>
    private static string BuildPrompt(string currentTitle, string extractedText, string? primaryLanguage)
    {
        currentTitle = RemediationHelpers.NormalizeWhitespace(currentTitle);
        extractedText = RemediationHelpers.NormalizeWhitespace(extractedText);
        primaryLanguage = RemediationHelpers.NormalizeWhitespace(primaryLanguage ?? string.Empty);

        var sb = new StringBuilder();
        sb.AppendLine(
            "Using the following content extracted from the first two to three pages of a PDF document, "
            + "generate a clear, concise, and descriptive title for the file.");
        sb.AppendLine(
            "The title should accurately summarize the primary focus of the document, be free of unnecessary jargon, "
            + "and comply with WCAG 2.1 AA accessibility guidelines by being understandable and distinguishable.");
        sb.AppendLine();
        sb.AppendLine(
            "Check the current title against the context of the extracted text. "
            + "If you think the current title is good enough based on the context, reply with the current title and nothing else. "
            + "Otherwise, generate a new title based on the provided context.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(primaryLanguage))
        {
            sb.Append("Primary Document Language: ");
            sb.AppendLine(primaryLanguage);
            sb.AppendLine("Write the title in this language.");
            sb.AppendLine();
        }

        sb.Append("Current File Title: ");
        sb.AppendLine(string.IsNullOrWhiteSpace(currentTitle) ? "(none)" : currentTitle);
        sb.AppendLine("Context for title generation:");
        sb.AppendLine(extractedText);
        sb.AppendLine();
        sb.AppendLine("Output only the title as the response and please do not reply with anything else except the generated title.");
        return sb.ToString();
    }
}
