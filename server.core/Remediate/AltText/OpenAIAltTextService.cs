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
        var text = ExtractText(result.Value);
        return NormalizeAltText(text, fallback: GetFallbackAltTextForImage());
    }

    public async Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<ChatMessage> messages =
        [
            new SystemChatMessage(BuildSystemInstructions()),
            new UserChatMessage(BuildLinkPrompt(request.Target, request.LinkText, request.ContextBefore, request.ContextAfter)),
        ];

        ClientResult<ChatCompletion> result = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions(), cancellationToken);
        var text = ExtractText(result.Value);
        return NormalizeAltText(text, fallback: GetFallbackAltTextForLink());
    }

    public string GetFallbackAltTextForImage() => "sample image alt text";

    public string GetFallbackAltTextForLink() => "sample link alt text";

    private static string BuildSystemInstructions() =>
        "You write accessible PDF alt text. Return ONLY the alt text, with no quotes, no markdown, and no extra commentary. "
        + "Prefer a short phrase or one concise sentence. Don't start with 'Image of'.";

    private static string BuildImagePrompt(string contextBefore, string contextAfter)
    {
        var sb = new StringBuilder();
        sb.Append("Write alt text for the image in this PDF. ");
        sb.Append("Use the surrounding document context if helpful.");

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
        sb.Append("Write accessible alt text (replacement text) for this PDF link. ");
        sb.Append("Return a short phrase. Prefer meaningful visible text; otherwise infer a concise label from the target and context.");

        if (!string.IsNullOrWhiteSpace(target))
        {
            sb.Append("\nTarget: ");
            sb.Append(target.Trim());
        }

        if (!string.IsNullOrWhiteSpace(linkText))
        {
            sb.Append("\nVisible text: ");
            sb.Append(NormalizeWhitespace(linkText));
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
        var before = NormalizeWhitespace(contextBefore);
        var after = NormalizeWhitespace(contextAfter);

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

    private static string ExtractText(ChatCompletion completion)
    {
        if (completion.Content.Count == 0)
        {
            return string.Empty;
        }

        return completion.Content[0].Text ?? string.Empty;
    }

    private static string NormalizeAltText(string text, string fallback)
    {
        text = NormalizeWhitespace(text);

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
