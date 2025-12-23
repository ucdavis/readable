using System.ClientModel;
using System.Text;
using OpenAI.Chat;

namespace server.core.Remediate.AltText;

public sealed class OpenAIAltTextService : IAltTextService
{
    private readonly ChatClient _chatClient;

    public OpenAIAltTextService(string apiKey, string model)
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
    /// Generates accessible alt text for an image using the surrounding PDF text as context.
    /// </summary>
    /// <remarks>
    /// The prompt instructs the model to return only the alt text (no quotes/markdown) and the output is trimmed and
    /// length-limited before returning.
    /// </remarks>
    public async Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = BuildImagePrompt(request.ContextBefore, request.ContextAfter);
        var imageData = BinaryData.FromBytes(request.ImageBytes);

        List<ChatMessage> messages =
        [
            new SystemChatMessage(BuildSystemInstructions()),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(prompt),
                ChatMessageContentPart.CreateImagePart(imageData, request.MimeType)),
        ];

        ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions(), cancellationToken);
        var text = RemediationHelpers.ExtractFirstTextOrEmpty(result.Value);
        return NormalizeAltText(text, fallback: GetFallbackAltTextForImage());
    }

    /// <summary>
    /// Generates accessible replacement text for a link using link target/text and nearby PDF context.
    /// </summary>
    public async Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<ChatMessage> messages =
        [
            new SystemChatMessage(BuildSystemInstructions()),
            new UserChatMessage(BuildLinkPrompt(request.Target, request.LinkText, request.ContextBefore, request.ContextAfter)),
        ];

        ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions(), cancellationToken);
        var text = RemediationHelpers.ExtractFirstTextOrEmpty(result.Value);
        return NormalizeAltText(text, fallback: GetFallbackAltTextForLink());
    }

    public string GetFallbackAltTextForImage() => "alt text for image";

    public string GetFallbackAltTextForLink() => "alt text for link";

    /// <summary>
    /// Returns the system instructions used to constrain response formatting.
    /// </summary>
    private static string BuildSystemInstructions() =>
        """
        You write WCAG 2.1-compliant PDF alt text.
        Return ONLY the alt text (no quotes, no markdown, and no extra commentary).
        Keep it concise (short phrase or one sentence) and do not start with "Image of".
        """;

    private static string BuildImagePrompt(string contextBefore, string contextAfter)
    {
        // reference prompt https://github.com/ASUCICREPO/PDF_Accessibility/blob/main/javascript_docker/alt-text.js
        var sb = new StringBuilder();
        sb.Append("Generate alt text for the provided image embedded in a PDF document. ");
        sb.Append("Use the surrounding PDF text context to improve accuracy, but do not invent details not visible in the image.");
        sb.Append('\n');
        sb.Append(
            """
            Guidelines:
            - Describe the key information the image conveys (objects/people/scene), and include any visible text that is necessary to understand it.
            - If the image is functional (e.g., icon/button), describe the function or intended action.
            - If the image is a chart/diagram, summarize the main takeaway rather than listing every value.
            - If the image contains a mathematical equation, spell out every symbol/operator using explicit phrases like "open parenthesis", "close parenthesis", "plus", "minus", "times", "divided by", "equals", and "to the power of".
            - For subscripts/superscripts, describe them explicitly using "with subscript … end subscript" and "with superscript … end superscript".
            """);

        var context = BuildContext(contextBefore, contextAfter, marker: "[IMAGE]");
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append("\nContext:\n");
            sb.Append(context);
        }

        return sb.ToString();
    }

    private static string BuildLinkPrompt(string? target, string linkText, string contextBefore, string contextAfter)
    {
        var sb = new StringBuilder();
        sb.Append("Generate accessible replacement text for a PDF link. ");
        sb.Append("Prefer meaningful visible link text; otherwise infer a concise label from the target and context.");

        if (!string.IsNullOrWhiteSpace(target))
        {
            sb.Append("\nTarget: ");
            sb.Append(target.Trim());
        }

        if (!string.IsNullOrWhiteSpace(linkText))
        {
            sb.Append("\nVisible text: ");
            sb.Append(RemediationHelpers.NormalizeWhitespace(linkText));
        }

        var context = BuildContext(contextBefore, contextAfter, marker: "[LINK]");
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append("\nContext:\n");
            sb.Append(context);
        }

        return sb.ToString();
    }

    private static string BuildContext(string contextBefore, string contextAfter, string marker)
    {
        var before = RemediationHelpers.NormalizeWhitespace(contextBefore);
        var after = RemediationHelpers.NormalizeWhitespace(contextAfter);

        if (string.IsNullOrWhiteSpace(before))
        {
            return string.IsNullOrWhiteSpace(after) ? string.Empty : $"{marker} {after}".Trim();
        }

        if (string.IsNullOrWhiteSpace(after))
        {
            return $"{before} {marker}".Trim();
        }

        return $"{before} {marker} {after}".Trim();
    }

    /// <summary>
    /// Normalizes and bounds model output, returning a fallback when the output is empty.
    /// </summary>
    private static string NormalizeAltText(string text, string fallback)
    {
        text = RemediationHelpers.NormalizeWhitespace(text);

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        // Avoid pathological long outputs.
        const int maxChars = 300;
        if (text.Length > maxChars)
        {
            text = text[..maxChars].Trim();
        }

        return text;
    }
}
