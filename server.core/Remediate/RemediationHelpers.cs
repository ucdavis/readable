using System.Text;
using OpenAI.Chat;

namespace server.core.Remediate;

internal static class RemediationHelpers
{
    public static string NormalizeWhitespace(string text)
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

    public static string ExtractFirstTextOrEmpty(ChatCompletion completion)
    {
        if (completion.Content.Count == 0)
        {
            return string.Empty;
        }

        return completion.Content[0].Text ?? string.Empty;
    }

    public static ChatCompletionOptions CreateFastChatOptions(string model, int maxOutputTokenCount)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxOutputTokenCount,
        };

        if (SupportsReasoningEffort(model))
        {
#pragma warning disable OPENAI001
            options.ReasoningEffortLevel = ChatReasoningEffortLevel.Low;
#pragma warning restore OPENAI001
        }

        return options;
    }

    private static bool SupportsReasoningEffort(string model)
    {
        model = NormalizeWhitespace(model).ToLowerInvariant();
        return model.StartsWith("gpt-5", StringComparison.Ordinal)
            || model.StartsWith("o1", StringComparison.Ordinal)
            || model.StartsWith("o3", StringComparison.Ordinal)
            || model.StartsWith("o4", StringComparison.Ordinal);
    }
}
