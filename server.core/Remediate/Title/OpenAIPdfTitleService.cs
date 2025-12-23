using System.ClientModel;
using System.Text;
using OpenAI.Chat;

namespace server.core.Remediate.Title;

public sealed class OpenAIPdfTitleService : IPdfTitleService
{
    private readonly ChatClient _chatClient;

    public OpenAIPdfTitleService(string apiKey, string model)
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

        var prompt = BuildPrompt(request.CurrentTitle, request.ExtractedText);

        List<ChatMessage> messages =
        [
            new UserChatMessage(prompt),
        ];

        ClientResult<ChatCompletion> result =
            await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions(), cancellationToken);

        return ExtractText(result.Value);
    }

    /// <summary>
    /// Builds a prompt that asks for a single plain-text title and includes current title and extracted context.
    /// </summary>
    private static string BuildPrompt(string currentTitle, string extractedText)
    {
        currentTitle = NormalizeWhitespace(currentTitle);
        extractedText = NormalizeWhitespace(extractedText);

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
        sb.Append("Current File Title: ");
        sb.AppendLine(string.IsNullOrWhiteSpace(currentTitle) ? "(none)" : currentTitle);
        sb.AppendLine("Context for title generation:");
        sb.AppendLine(extractedText);
        sb.AppendLine();
        sb.AppendLine("Output only the title as the response and please do not reply with anything else except the generated title.");
        return sb.ToString();
    }

    private static string ExtractText(ChatCompletion completion)
    {
        if (completion.Content.Count == 0)
        {
            return string.Empty;
        }

        return completion.Content[0].Text ?? string.Empty;
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var inWhitespace = true;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(ch);
            inWhitespace = false;
        }

        return sb.ToString().Trim();
    }
}
